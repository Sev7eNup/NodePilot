using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// The iteration body of <see cref="ForEachActivity"/> — everything past validation:
/// per-item parameter seeding, the fail-fast vs continueOnError contract, the aggregate
/// output parameters downstream steps branch on, per-item timeout, and parallelism.
/// <see cref="ForEachActivityExtraTests"/> covers the parsing and cap branches that
/// short-circuit before the loop.
/// </summary>
public sealed class ForEachActivityExecutionTests : IDisposable
{
    private readonly NodePilotDbContext _db = TestDbContext.Create();
    private readonly Workflow _child;

    public ForEachActivityExecutionTests()
    {
        _child = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Child",
            DefinitionJson = "{}",
            IsEnabled = true,
        };
        _db.Workflows.Add(_child);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------- happy path

    [Fact]
    public async Task ExecuteAsync_EveryItemSucceeds_ReportsAggregateCounts()
    {
        var engine = new FakeEngine();

        var result = await Run(engine, "a\nb\nc");

        result.Success.Should().BeTrue();
        result.OutputParameters["total"].Should().Be("3");
        result.OutputParameters["succeeded"].Should().Be("3");
        result.OutputParameters["failed"].Should().Be("0");
        result.OutputParameters["skipped"].Should().Be("0");
        result.Output.Should().Contain("3/3 succeeded");
    }

    [Fact]
    public async Task ExecuteAsync_SeedsItemAndIndexParametersPerIteration()
    {
        var engine = new FakeEngine();

        await Run(engine, "alpha\nbeta");

        engine.Calls.Should().HaveCount(2);
        engine.Calls[0].Parameters!["item"].Should().Be("alpha");
        engine.Calls[0].Parameters!["index"].Should().Be("0");
        engine.Calls[1].Parameters!["item"].Should().Be("beta");
        engine.Calls[1].Parameters!["index"].Should().Be("1");
    }

    [Fact]
    public async Task ExecuteAsync_CustomParameterNames_AreHonoured()
    {
        var engine = new FakeEngine();

        await Run(engine, "x", extraConfig: new Dictionary<string, object?>
        {
            ["itemParameterName"] = "hostName",
            ["indexParameterName"] = "position",
        });

        engine.Calls.Single().Parameters!.Should().ContainKey("hostName");
        engine.Calls.Single().Parameters!.Should().ContainKey("position");
    }

    [Fact]
    public async Task ExecuteAsync_StaticParameters_AreMergedUnderTheIterationValues()
    {
        var engine = new FakeEngine();

        await Run(engine, "only", extraConfig: new Dictionary<string, object?>
        {
            ["parameters"] = new Dictionary<string, string> { ["environment"] = "prod", ["item"] = "static" },
        });

        var parameters = engine.Calls.Single().Parameters!;
        parameters["environment"].Should().Be("prod");
        parameters["item"].Should().Be("only", "the iteration value wins over a colliding static param");
    }

    [Fact]
    public async Task ExecuteAsync_PassesTheIncrementedCallDepthToTheChild()
    {
        var engine = new FakeEngine();

        await Run(engine, "one");

        engine.Calls.Single().Parameters!.Should().ContainKey(WorkflowRecursion.CallDepthKey);
    }

    [Fact]
    public async Task ExecuteAsync_ResultsJson_CarriesPerItemDetail()
    {
        var engine = new FakeEngine();

        var result = await Run(engine, "a\nb");

        using var document = JsonDocument.Parse(result.OutputParameters["results"]);
        var entries = document.RootElement.EnumerateArray().ToList();
        entries.Should().HaveCount(2);
        entries[0].GetProperty("item").GetString().Should().Be("a");
        entries[0].GetProperty("status").GetString().Should().Be("Succeeded");
        entries[0].GetProperty("executionId").GetString().Should().NotBeNullOrEmpty();
    }

    // ---------------------------------------------------------------- failure handling

    [Fact]
    public async Task ExecuteAsync_ChildFailsWithoutContinueOnError_StepFails()
    {
        var engine = new FakeEngine { FailFrom = 0 };

        var result = await Run(engine, "a\nb\nc");

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().NotBeNullOrEmpty();
        result.OutputParameters.Should().ContainKey("firstError");
    }

    [Fact]
    public async Task ExecuteAsync_ChildFailsWithoutContinueOnError_StopsTheRemainingItems()
    {
        var engine = new FakeEngine { FailFrom = 0 };

        var result = await Run(engine, "a\nb\nc");

        // Fail-fast cancels the shared token; items never started are reported as skipped
        // rather than silently dropped from the aggregate.
        var accounted = int.Parse(result.OutputParameters["succeeded"])
                        + int.Parse(result.OutputParameters["failed"])
                        + int.Parse(result.OutputParameters["skipped"]);
        accounted.Should().Be(3);
        engine.Calls.Count.Should().BeLessThan(3, "fail-fast must not run the whole list");
    }

    [Fact]
    public async Task ExecuteAsync_ContinueOnError_RunsEveryItemAndStillSucceeds()
    {
        var engine = new FakeEngine { FailFrom = 1 };

        var result = await Run(engine, "a\nb\nc", extraConfig: new Dictionary<string, object?>
        {
            ["continueOnError"] = true,
        });

        engine.Calls.Should().HaveCount(3);
        result.OutputParameters["failed"].Should().Be("2");
        result.OutputParameters["succeeded"].Should().Be("1");
        result.Success.Should().BeTrue("continueOnError lets downstream branch on param.failed");
    }

    [Fact]
    public async Task ExecuteAsync_ContinueOnErrorWithEveryItemFailing_StepFails()
    {
        var engine = new FakeEngine { FailFrom = 0 };

        var result = await Run(engine, "a\nb", extraConfig: new Dictionary<string, object?>
        {
            ["continueOnError"] = true,
        });

        result.Success.Should().BeFalse("continueOnError still needs at least one success");
    }

    [Fact]
    public async Task ExecuteAsync_ChildThrows_IsReportedAsAFailedItem()
    {
        var engine = new FakeEngine { Throw = new InvalidOperationException("child blew up") };

        var result = await Run(engine, "a", extraConfig: new Dictionary<string, object?>
        {
            ["continueOnError"] = true,
        });

        result.OutputParameters["failed"].Should().Be("1");
        result.OutputParameters["firstError"].Should().Contain("child blew up");
    }

    [Fact]
    public async Task ExecuteAsync_ItemTimeout_IsReportedPerItem()
    {
        var engine = new FakeEngine { Delay = TimeSpan.FromSeconds(30) };

        var result = await Run(engine, "slow", extraConfig: new Dictionary<string, object?>
        {
            ["timeoutSecondsPerItem"] = 1,
            ["continueOnError"] = true,
        });

        result.OutputParameters["failed"].Should().Be("1");
        result.OutputParameters["firstError"].Should().Contain("timed out");
    }

    // ---------------------------------------------------------------- parallelism

    [Fact]
    public async Task ExecuteAsync_MaxParallelism_RunsItemsConcurrently()
    {
        var engine = new FakeEngine { Delay = TimeSpan.FromMilliseconds(150) };

        var result = await Run(engine, "a\nb\nc\nd", extraConfig: new Dictionary<string, object?>
        {
            ["maxParallelism"] = 4,
        });

        result.OutputParameters["succeeded"].Should().Be("4");
        engine.MaxObservedConcurrency.Should().BeGreaterThan(1,
            "maxParallelism > 1 must actually overlap child executions");
    }

    [Fact]
    public async Task ExecuteAsync_DefaultParallelism_RunsItemsSequentially()
    {
        var engine = new FakeEngine { Delay = TimeSpan.FromMilliseconds(50) };

        await Run(engine, "a\nb\nc");

        engine.MaxObservedConcurrency.Should().Be(1, "the default is a sequential loop");
    }

    // ---------------------------------------------------------------- helpers

    private async Task<ActivityResult> Run(
        FakeEngine engine,
        string items,
        Dictionary<string, object?>? extraConfig = null)
    {
        var config = new Dictionary<string, object?>
        {
            ["childWorkflowNameOrId"] = _child.Id.ToString(),
            ["items"] = items,
            ["itemsFormat"] = "lines",
        };
        foreach (var (key, value) in extraConfig ?? []) config[key] = value;

        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowEngine>(engine);
        var activity = new ForEachActivity(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _db,
            new InMemorySubWorkflowGate());

        return await activity.ExecuteAsync(
            new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" },
            JsonDocument.Parse(JsonSerializer.Serialize(config)).RootElement,
            TestContext.Current.CancellationToken);
    }

    private sealed class FakeEngine : IWorkflowEngine
    {
        private int _running;
        private readonly Lock _sync = new();

        public List<(Workflow Workflow, Dictionary<string, string>? Parameters)> Calls { get; } = [];

        /// <summary>Index from which every child execution reports Failed. Null = all succeed.</summary>
        public int? FailFrom { get; init; }

        public Exception? Throw { get; init; }

        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        public int MaxObservedConcurrency { get; private set; }

        public async Task<WorkflowExecution> ExecuteAsync(
            Workflow workflow, string triggeredBy, CancellationToken ct,
            Dictionary<string, string>? inputParameters = null,
            int? timeoutSeconds = null,
            bool debugEnabled = false,
            Guid? startedByUserId = null,
            Guid? parentExecutionId = null,
            int callDepth = 0,
            Guid? executionIdOverride = null,
            bool interactiveRun = false)
        {
            int index;
            lock (_sync)
            {
                index = Calls.Count;
                Calls.Add((workflow, inputParameters));
                MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, ++_running);
            }

            try
            {
                if (Throw is not null) throw Throw;
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);

                var failed = FailFrom is { } from && index >= from;
                return new WorkflowExecution
                {
                    Id = Guid.NewGuid(),
                    WorkflowId = workflow.Id,
                    Status = failed ? ExecutionStatus.Failed : ExecutionStatus.Succeeded,
                    ErrorMessage = failed ? "child reported failure" : null,
                    StartedAt = DateTime.UtcNow,
                };
            }
            finally
            {
                lock (_sync) _running--;
            }
        }

        public Task<bool> CancelAsync(Guid executionId, string? cancelledBy = null, CancellationToken ct = default)
            => Task.FromResult(true);

        public bool Resume(Guid executionId, string stepId, DebugResumeCommand command,
            IReadOnlyDictionary<string, string>? overrides) => false;

        public IReadOnlyCollection<string> GetPausedSteps(Guid executionId) => [];
    }
}
