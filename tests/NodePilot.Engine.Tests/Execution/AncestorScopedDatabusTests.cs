using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Execution;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

/// <summary>
/// The databus is scoped to a step's graph ancestors.
///
/// <para>Before, the scheduler's shared result map was handed to every step unfiltered, so a
/// template could resolve against a node on an unrelated parallel branch — but only when that
/// branch happened to finish first. The same definition with the same inputs would resolve on one
/// run and fail the unresolved-template check on the next, and a workflow reliably green on a
/// developer machine could fail intermittently under production load.</para>
///
/// <para>These tests pin the two halves: an ancestor's output resolves, a sibling branch's does
/// not, regardless of completion order.</para>
/// </summary>
[Collection("SerialEngineTests")]
public sealed class AncestorScopedDatabusTests : IDisposable
{
    private readonly NodePilotDbContext _db;
    private readonly IServiceProvider _serviceProvider;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly WorkflowEngine _engine;

    /// <summary>Config each step received, captured so the assertions can read what was
    /// substituted.</summary>
    private readonly Dictionary<string, string> _seenConfigByStep = new(StringComparer.Ordinal);

    public AncestorScopedDatabusTests()
    {
        var recorder = new Mock<IActivityExecutor>();
        recorder.Setup(e => e.ActivityType).Returns("log");
        recorder.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (StepExecutionContext ctx, JsonElement cfg, CancellationToken _) =>
            {
                lock (_seenConfigByStep) _seenConfigByStep[ctx.StepId] = cfg.GetRawText();

                // Steps whose id ends in "-slow" hold the branch open. The cross-branch test
                // needs the sibling to be demonstrably FINISHED before the consumer starts —
                // otherwise the reference fails simply because the value had not arrived yet,
                // and the test would pass with or without ancestor scoping.
                if (ctx.StepId.EndsWith("-slow", StringComparison.Ordinal))
                    await Task.Delay(250, CancellationToken.None);

                return new ActivityResult { Success = true, Output = $"out-of-{ctx.StepId}" };
            });

        var trigger = new Mock<IActivityExecutor>();
        trigger.Setup(e => e.ActivityType).Returns("manualTrigger");
        trigger.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "{}" });

        // Stand-in for RunScriptActivity: the out-of-scope gate lives in StepRunner and runs
        // before the executor, so no real PowerShell is needed — but the type must be registered,
        // and capturing the config proves what WOULD have reached the script.
        var script = new Mock<IActivityExecutor>();
        script.Setup(e => e.ActivityType).Returns("runScript");
        script.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(),
                It.IsAny<JsonElement>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((StepExecutionContext ctx, JsonElement cfg, CancellationToken _) =>
            {
                lock (_seenConfigByStep) _seenConfigByStep[ctx.StepId] = cfg.GetRawText();
                return new ActivityResult { Success = true, Output = "script-ran" };
            });

        var registry = new ActivityRegistry(new[] { recorder.Object, trigger.Object, script.Object });
        (_db, var sp, _connection) = TestDbContext.CreateWithScopedServices(registry);
        _serviceProvider = sp;
        _engine = new WorkflowEngine(_db, NullLogger<WorkflowEngine>.Instance, _serviceProvider,
            new Mock<IExecutionNotifier>().Object);
    }

    public void Dispose() => _connection.Dispose();

    // ------------------------------------------------------------------ AncestorIndex

    [Fact]
    public void AncestorIndex_LinearChain_AccumulatesEveryPredecessor()
    {
        var index = AncestorIndex.Build(new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["a"] = [],
            ["b"] = ["a"],
            ["c"] = ["b"],
        });

        index["a"].Should().BeEmpty();
        index["b"].Should().BeEquivalentTo("a");
        index["c"].Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public void AncestorIndex_SiblingBranches_DoNotSeeEachOther()
    {
        var index = AncestorIndex.Build(new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["root"] = [],
            ["left"] = ["root"],
            ["right"] = ["root"],
        });

        index["left"].Should().BeEquivalentTo("root");
        index["right"].Should().BeEquivalentTo("root");
        index["left"].Should().NotContain("right");
        index["right"].Should().NotContain("left");
    }

    [Fact]
    public void AncestorIndex_DiamondJoin_SeesBothBranches()
    {
        var index = AncestorIndex.Build(new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["root"] = [],
            ["left"] = ["root"],
            ["right"] = ["root"],
            ["join"] = ["left", "right"],
        });

        index["join"].Should().BeEquivalentTo("root", "left", "right");
    }

    /// <summary>
    /// A cyclic graph never runs (it produces no roots), but the index is also built while
    /// validating one — it must terminate rather than recurse forever.
    /// </summary>
    [Fact]
    public void AncestorIndex_Cycle_Terminates()
    {
        var index = AncestorIndex.Build(new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["a"] = ["c"],
            ["b"] = ["a"],
            ["c"] = ["b"],
        });

        index.Should().HaveCount(3);
        index["a"].Should().Contain("c");
    }

    // ------------------------------------------------------------------ AncestorScopedResults

    [Fact]
    public void ScopedResults_HidesNonAncestors_AndExposesAncestors()
    {
        var all = new Dictionary<string, ActivityResult>(StringComparer.Ordinal)
        {
            ["ancestor"] = new() { Success = true, Output = "yes" },
            ["sibling"] = new() { Success = true, Output = "no" },
        };
        var scoped = new AncestorScopedResults(
            all,
            new HashSet<string>(StringComparer.Ordinal) { "ancestor" },
            new HashSet<string>(StringComparer.Ordinal) { "ancestor", "sibling" });

        scoped.TryGetValue("ancestor", out var visible).Should().BeTrue();
        visible.Output.Should().Be("yes");
        scoped.TryGetValue("sibling", out _).Should().BeFalse();
        scoped.ContainsKey("sibling").Should().BeFalse();
        scoped.Keys.Should().BeEquivalentTo("ancestor");
        scoped.Count.Should().Be(1);
    }

    /// <summary>
    /// An ancestor that has not produced a result yet must simply be absent, not throw — the
    /// unresolved-template diagnostic depends on a clean miss.
    /// </summary>
    [Fact]
    public void ScopedResults_AncestorWithoutResult_IsAbsent()
    {
        var scoped = new AncestorScopedResults(
            new Dictionary<string, ActivityResult>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "ancestor" },
            new HashSet<string>(StringComparer.Ordinal) { "ancestor" });

        scoped.ContainsKey("ancestor").Should().BeFalse();
        scoped.Count.Should().Be(0);
    }

    /// <summary>
    /// Determines scope from the graph so unfinished and completed sibling nodes are equally
    /// unreadable.
    /// </summary>
    [Fact]
    public void ScopedResults_NonAncestorNode_IsOutOfScopeEvenBeforeItHasRun()
    {
        var knownNodes = new HashSet<string>(StringComparer.Ordinal) { "ancestor", "finished-sibling", "running-sibling" };
        var scoped = new AncestorScopedResults(
            new Dictionary<string, ActivityResult>(StringComparer.Ordinal)
            {
                ["finished-sibling"] = new() { Success = true, Output = "done" },
            },
            new HashSet<string>(StringComparer.Ordinal) { "ancestor" },
            knownNodes);

        scoped.IsNonAncestorNode("finished-sibling").Should().BeTrue("a sibling that finished is out of scope");
        scoped.IsNonAncestorNode("running-sibling").Should().BeTrue(
            "a sibling that has not finished is out of scope for the same reason — the graph decides, not the clock");
        scoped.IsNonAncestorNode("ancestor").Should().BeFalse("a predecessor is readable");
        scoped.IsNonAncestorNode("typo-step").Should().BeFalse(
            "an id that names no node at all stays a plain missing reference, not a wiring error");
    }

    // ------------------------------------------------------------------ end to end

    [Fact]
    public async Task Execution_StepReadingItsAncestorsOutput_Resolves()
    {
        // trigger -> producer -> consumer, consumer reads {{producer.output}}
        var workflow = NewWorkflow(
            Node("producer"),
            Node("consumer", message: "{{producer.output}}"),
            Edge("trigger-1", "producer"),
            Edge("producer", "consumer"));

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        _seenConfigByStep["consumer"].Should().Contain("out-of-producer",
            "an ancestor's output must reach the step");
    }

    /// <summary>
    /// Ensures a consumer cannot read a completed sibling branch outside its predecessor path.
    /// </summary>
    [Fact]
    public async Task Execution_StepReadingAFinishedSiblingBranch_StillFails()
    {
        var workflow = NewWorkflow(
            Node("branch-a-slow"),
            Node("sibling"),
            Node("consumer", message: "{{sibling.output}}"),
            Edge("trigger-1", "branch-a-slow"),
            Edge("trigger-1", "sibling"),
            Edge("branch-a-slow", "consumer"));

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        // Precondition for the test to mean anything: the sibling really did complete first.
        var siblingStep = await FindStepAsync(execution.Id, "sibling");
        siblingStep.Status.Should().Be(ExecutionStatus.Succeeded);
        var consumerStep = await FindStepAsync(execution.Id, "consumer");
        siblingStep.CompletedAt.Should().NotBeNull();
        consumerStep.StartedAt.Should().NotBeNull();
        siblingStep.CompletedAt!.Value.Should().BeOnOrBefore(consumerStep.StartedAt!.Value,
            "the fixture must actually produce the race it is testing");

        execution.Status.Should().Be(ExecutionStatus.Failed,
            "a cross-branch reference is not on any predecessor path and must fail even when the value is present");
        consumerStep.Status.Should().Be(ExecutionStatus.Failed);
        consumerStep.ErrorOutput.Should().Contain("Unresolved template variable");

        // The diagnostic must name the real cause. "has not run or does not exist" would send the
        // author hunting for a step that visibly ran and succeeded in the same execution.
        consumerStep.ErrorOutput.Should().Contain("not on a predecessor path");
        consumerStep.ErrorOutput.Should().NotContain("has not run or does not exist");
    }

    /// <summary>
    /// A reference to a step that genuinely does not exist must keep the original wording —
    /// the out-of-scope hint would be wrong there.
    /// </summary>
    [Fact]
    public async Task Execution_ReferenceToAnUnknownStep_KeepsTheMissingStepDiagnostic()
    {
        var workflow = NewWorkflow(
            Node("consumer", message: "{{does-not-exist.output}}"),
            Edge("trigger-1", "consumer"));

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Failed);
        var consumerStep = await FindStepAsync(execution.Id, "consumer");
        consumerStep.ErrorOutput.Should().Contain("has not run or does not exist");
        consumerStep.ErrorOutput.Should().NotContain("not on a predecessor path");
    }

    /// <summary>
    /// The trigger is an ancestor of everything, so its own outputs stay reachable — the scoping
    /// must not cut the run off from its entry point.
    /// </summary>
    [Fact]
    public async Task Execution_StepReadingTheTrigger_Resolves()
    {
        var workflow = NewWorkflow(
            Node("consumer", message: "{{trigger-1.output}}"),
            Edge("trigger-1", "consumer"));

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        _seenConfigByStep["consumer"].Should().Contain("{}");
    }

    /// <summary>
    /// Ensures the runScript placeholder exemption does not permit cross-branch references.
    ///
    /// <para>runScript resolves templates with PowerShell quoting, so unresolved placeholders may
    /// be valid script text. References to known nodes still require graph validation.</para>
    ///
    /// <para>A reference to a node outside the predecessor path is invalid for runScript.</para>
    /// </summary>
    [Fact]
    public async Task Execution_RunScriptReadingASibling_FailsInsteadOfRunningWithThePlaceholder()
    {
        var workflow = NewWorkflow(
            Node("branch-a-slow"),
            Node("sibling"),
            ScriptNode("script", "$wert = {{sibling.output}}"),
            Edge("trigger-1", "branch-a-slow"),
            Edge("trigger-1", "sibling"),
            Edge("branch-a-slow", "script"));

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Failed,
            "a cross-branch reference must not reach PowerShell as a literal");
        var scriptStep = await FindStepAsync(execution.Id, "script");
        scriptStep.Status.Should().Be(ExecutionStatus.Failed);
        scriptStep.ErrorOutput.Should().Contain("not on a predecessor path");
    }

    /// <summary>
    /// Ensures a running sibling remains out of scope and cannot reach PowerShell as a literal
    /// placeholder. Scope depends on the graph rather than branch timing.
    /// </summary>
    [Fact]
    public async Task Execution_RunScriptReadingAStillRunningSibling_FailsToo()
    {
        var workflow = NewWorkflow(
            Node("sibling-slow"),
            ScriptNode("script", "$wert = {{sibling-slow.output}}"),
            Edge("trigger-1", "sibling-slow"),
            Edge("trigger-1", "script"));

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Failed,
            "the reference is unreadable because of the graph, not because of timing");
        var scriptStep = await FindStepAsync(execution.Id, "script");
        scriptStep.Status.Should().Be(ExecutionStatus.Failed);
        scriptStep.ErrorOutput.Should().Contain("not on a predecessor path");
        _seenConfigByStep.Should().NotContainKey("script",
            "the step must fail before the placeholder can reach the script body");
    }

    /// <summary>
    /// The narrowing must stay narrow. A runScript whose text merely contains something that
    /// looks like a template — a typo, a deleted step, or genuine PowerShell braces — keeps the
    /// old tolerant behaviour, because that exemption exists for good reasons.
    /// </summary>
    [Fact]
    public async Task Execution_RunScriptWithAnUnknownReference_StillRuns()
    {
        var workflow = NewWorkflow(
            ScriptNode("script", "Write-Output 'ok {{typo-step.output}}'"),
            Edge("trigger-1", "script"));

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded,
            "only the out-of-scope case is fatal for runScript — unknown references stay tolerated");
    }

    /// <summary>A runScript reading a genuine ancestor is unaffected.</summary>
    [Fact]
    public async Task Execution_RunScriptReadingItsAncestor_Runs()
    {
        var workflow = NewWorkflow(
            Node("producer"),
            ScriptNode("script", "Write-Output {{producer.output}}"),
            Edge("trigger-1", "producer"),
            Edge("producer", "script"));

        var execution = await _engine.ExecuteAsync(workflow, "manual", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
    }

    // ------------------------------------------------------------------ fixture helpers

    private const string TriggerNodeJson =
        """{"id":"trigger-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","config":{}}}""";

    // Built by concatenation rather than raw interpolation: the JSON braces collide with the
    // {{ }} interpolation delimiters and the escaping obscures the fixture more than it helps.
    private static string Node(string id, string? message = null)
    {
        var config = message is null ? "{}" : "{\"message\":\"" + message + "\"}";
        return "{\"id\":\"" + id + "\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},"
             + "\"data\":{\"activityType\":\"log\",\"config\":" + config + "}}";
    }

    /// <summary>A runScript node — the activity that resolves its own templates.</summary>
    private static string ScriptNode(string id, string script)
    {
        var escaped = JsonSerializer.Serialize(script);
        return "{\"id\":\"" + id + "\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},"
             + "\"data\":{\"activityType\":\"runScript\",\"config\":{\"script\":" + escaped + "}}}";
    }

    private static string Edge(string source, string target) =>
        "{\"id\":\"e-" + source + "-" + target + "\",\"source\":\"" + source + "\",\"target\":\"" + target + "\"}";

    private Workflow NewWorkflow(params string[] parts)
    {
        var nodes = parts.Where(p => p.Contains("\"position\"", StringComparison.Ordinal)).ToList();
        var edges = parts.Where(p => !p.Contains("\"position\"", StringComparison.Ordinal)).ToList();
        var nodeList = string.Join(",", new[] { TriggerNodeJson }.Concat(nodes));
        var edgeList = string.Join(",", edges);
        var json = "{\"nodes\":[" + nodeList + "],\"edges\":[" + edgeList + "]}";

        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "AncestorScope", DefinitionJson = json };
        _db.Workflows.Add(workflow);
        _db.SaveChanges();
        return workflow;
    }

    private async Task<StepExecution> FindStepAsync(Guid executionId, string stepId)
    {
        await using var read = new NodePilotDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<NodePilotDbContext>()
                .UseSqlite(_connection).Options);
        return await read.StepExecutions
            .AsNoTracking()
            .FirstAsync(s => s.WorkflowExecutionId == executionId && s.StepId == stepId);
    }
}
