using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;
using NodePilot.Engine.Tests.Helpers;

namespace NodePilot.Engine.Tests.Execution;

public class StepTestContextProviderTests
{
    /// <summary>
    /// 3-node line graph: a → b → c. Step c's context must include a and b as upstream
    /// ancestors plus the values their last execution produced.
    /// </summary>
    [Fact]
    public async Task GetContextAsync_LinearGraph_PullsLastRunValuesForAncestors()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        var def = JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                Node("a", "runScript", outputVariable: "checkDisk"),
                Node("b", "runScript"),
                Node("c", "runScript"),
            },
            edges = new object[] { Edge("a", "b"), Edge("b", "c") }
        });

        db.Workflows.Add(new Workflow { Id = workflowId, Name = "wf", DefinitionJson = def });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowId = workflowId,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAt = DateTime.UtcNow,
        });
        db.StepExecutions.AddRange(
            new StepExecution
            {
                Id = Guid.NewGuid(), WorkflowExecutionId = executionId, StepId = "a",
                StepType = "runScript", Status = ExecutionStatus.Succeeded,
                Output = "7", OutputParametersJson = """{"freeGb":"7"}"""
            },
            new StepExecution
            {
                Id = Guid.NewGuid(), WorkflowExecutionId = executionId, StepId = "b",
                StepType = "runScript", Status = ExecutionStatus.Failed,
                ErrorOutput = "boom"
            });
        await db.SaveChangesAsync();

        var provider = new StepTestContextProvider(db, new StubGlobalVariableStore());
        var ctx = await provider.GetContextAsync(workflowId, "c", executionId, default);

        ctx.ExecutionId.Should().Be(executionId);
        ctx.Status.Should().Be(nameof(ExecutionStatus.Succeeded));
        // alias for step "a" → "checkDisk"; step "b" stays as its raw id.
        var keys = ctx.Variables.Select(v => v.Key).ToArray();
        keys.Should().Contain("checkDisk.output");
        keys.Should().Contain("checkDisk.param.freeGb");
        keys.Should().Contain("b.error");
        // The step under test must NOT appear in its own context.
        keys.Should().NotContain(k => k.StartsWith("c."));
        ctx.Variables.Single(v => v.Key == "checkDisk.output").Value.Should().Be("7");
        ctx.Variables.Single(v => v.Key == "checkDisk.param.freeGb").Value.Should().Be("7");
        ctx.Variables.Single(v => v.Key == "b.error").Value.Should().Be("boom");
        ctx.Variables.Single(v => v.Key == "b.success").Value.Should().Be("false");
    }

    [Fact]
    public async Task GetContextAsync_NoExecution_FallsBackToSchemaOnly()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        var def = JsonSerializer.Serialize(new
        {
            nodes = new object[] { Node("a", "runScript"), Node("b", "runScript") },
            edges = new object[] { Edge("a", "b") }
        });
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "wf", DefinitionJson = def });
        await db.SaveChangesAsync();

        var provider = new StepTestContextProvider(db, new StubGlobalVariableStore());
        var ctx = await provider.GetContextAsync(workflowId, "b", null, default);

        ctx.ExecutionId.Should().BeNull();
        ctx.Variables.Should().Contain(v => v.Key == "a.output" && v.Value == null);
        ctx.Variables.Should().Contain(v => v.Key == "a.success" && v.Value == null);
    }

    [Fact]
    public async Task GetContextAsync_NoExecution_IncludesAncestorsBehindDisabledEdges()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        var def = JsonSerializer.Serialize(new
        {
            nodes = new object[] { Node("a", "runScript"), Node("b", "runScript"), Node("c", "runScript") },
            edges = new object[] { Edge("a", "b", disabled: true), Edge("b", "c") }
        });
        db.Workflows.Add(new Workflow { Id = workflowId, Name = "wf", DefinitionJson = def });
        await db.SaveChangesAsync();

        var provider = new StepTestContextProvider(db, new StubGlobalVariableStore());
        var ctx = await provider.GetContextAsync(workflowId, "c", null, default);

        var keys = ctx.Variables.Select(v => v.Key).ToArray();
        keys.Should().Contain("a.output");
        keys.Should().Contain("b.output");
    }

    [Fact]
    public async Task GetContextAsync_GlobalsAlwaysIncluded()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        db.Workflows.Add(new Workflow
        {
            Id = workflowId, Name = "wf",
            DefinitionJson = JsonSerializer.Serialize(new { nodes = new[] { Node("a", "log") }, edges = Array.Empty<object>() })
        });
        await db.SaveChangesAsync();

        var provider = new StepTestContextProvider(db,
            new StubGlobalVariableStore(("ENV", "stg"), ("API_HOST", "10.0.0.1")));
        var ctx = await provider.GetContextAsync(workflowId, "a", null, default);

        ctx.Variables.Should().Contain(v => v.Key == "globals.ENV" && v.Value == "stg");
        ctx.Variables.Should().Contain(v => v.Key == "globals.API_HOST" && v.Value == "10.0.0.1");
    }

    /// <summary>
    /// Security regression: IsSecret globals must be masked to "***" in the context-preview
    /// payload, matching the redaction applied by GlobalVariablesController.Project. Without
    /// this, an Admin/Operator with access to the workflow editor could read secret-global
    /// plaintext values via /api/workflows/{id}/steps/{stepId}/test-context that the dedicated
    /// /api/global-variables endpoint already redacts.
    /// </summary>
    [Fact]
    public async Task GetContextAsync_SecretGlobals_AreMasked()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        db.Workflows.Add(new Workflow
        {
            Id = workflowId, Name = "wf",
            DefinitionJson = JsonSerializer.Serialize(new { nodes = new[] { Node("a", "log") }, edges = Array.Empty<object>() })
        });
        await db.SaveChangesAsync();

        var store = new StubGlobalVariableStore(("ENV", "stg"), ("API_TOKEN", "super-secret"));
        store.SetSecret("API_TOKEN", true);

        var provider = new StepTestContextProvider(db, store);
        var ctx = await provider.GetContextAsync(workflowId, "a", null, default);

        ctx.Variables.Single(v => v.Key == "globals.ENV").Value.Should().Be("stg");
        ctx.Variables.Single(v => v.Key == "globals.API_TOKEN").Value.Should().Be("***");
    }

    [Fact]
    public async Task GetContextAsync_StepDidNotRunInPickedExecution_EmitsSchemaOnlyForThatAncestor()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        db.Workflows.Add(new Workflow
        {
            Id = workflowId, Name = "wf",
            DefinitionJson = JsonSerializer.Serialize(new
            {
                nodes = new[] { Node("a", "runScript"), Node("b", "runScript") },
                edges = new[] { Edge("a", "b") }
            })
        });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId, WorkflowId = workflowId, Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-1), CompletedAt = DateTime.UtcNow,
        });
        // Step "a" never ran in this execution — only "b" persisted (impossible in real
        // engine but a clean way to assert the schema-only fallback per ancestor).
        await db.SaveChangesAsync();

        var provider = new StepTestContextProvider(db, new StubGlobalVariableStore());
        var ctx = await provider.GetContextAsync(workflowId, "b", executionId, default);

        ctx.Variables.Should().Contain(v => v.Key == "a.output" && v.Value == null);
        ctx.Variables.Should().Contain(v => v.Key == "a.success" && v.Value == null);
    }

    [Fact]
    public async Task GetContextAsync_CorruptOutputParametersJson_EmitsMarkerEntry()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        db.Workflows.Add(new Workflow
        {
            Id = workflowId, Name = "wf",
            DefinitionJson = JsonSerializer.Serialize(new
            {
                nodes = new[] { Node("a", "runScript"), Node("b", "runScript") },
                edges = new[] { Edge("a", "b") }
            })
        });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId, WorkflowId = workflowId, Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-1), CompletedAt = DateTime.UtcNow,
        });
        db.StepExecutions.Add(new StepExecution
        {
            Id = Guid.NewGuid(), WorkflowExecutionId = executionId, StepId = "a",
            StepType = "runScript", Status = ExecutionStatus.Succeeded,
            OutputParametersJson = "{not valid json"
        });
        await db.SaveChangesAsync();

        var provider = new StepTestContextProvider(db, new StubGlobalVariableStore());
        var ctx = await provider.GetContextAsync(workflowId, "b", executionId, default);

        ctx.Variables.Should().Contain(v => v.Key == "a.param.<invalid>");
    }

    [Fact]
    public async Task ListRunsAsync_FlagsStepRanCorrectly()
    {
        using var db = TestDbFactory.Create();
        var workflowId = Guid.NewGuid();
        var ranId = Guid.NewGuid();
        var skippedId = Guid.NewGuid();

        db.Workflows.Add(new Workflow { Id = workflowId, Name = "wf", DefinitionJson = "{}" });
        db.WorkflowExecutions.AddRange(
            new WorkflowExecution
            {
                Id = ranId, WorkflowId = workflowId, Status = ExecutionStatus.Succeeded,
                StartedAt = DateTime.UtcNow.AddMinutes(-2), CompletedAt = DateTime.UtcNow.AddMinutes(-1)
            },
            new WorkflowExecution
            {
                Id = skippedId, WorkflowId = workflowId, Status = ExecutionStatus.Succeeded,
                StartedAt = DateTime.UtcNow.AddMinutes(-4), CompletedAt = DateTime.UtcNow.AddMinutes(-3)
            });
        db.StepExecutions.Add(new StepExecution
        {
            Id = Guid.NewGuid(), WorkflowExecutionId = ranId, StepId = "target",
            StepType = "runScript", Status = ExecutionStatus.Succeeded
        });
        await db.SaveChangesAsync();

        var provider = new StepTestContextProvider(db, new StubGlobalVariableStore());
        var runs = await provider.ListRunsAsync(workflowId, "target", 10, default);

        runs.Should().HaveCount(2);
        runs.Single(r => r.ExecutionId == ranId).StepRan.Should().BeTrue();
        runs.Single(r => r.ExecutionId == skippedId).StepRan.Should().BeFalse();
        // Newest first.
        runs[0].ExecutionId.Should().Be(ranId);
    }

    private static object Node(string id, string activityType, string? outputVariable = null) => new
    {
        id,
        type = "activity",
        position = new { x = 0, y = 0 },
        data = new
        {
            activityType,
            outputVariable,
            label = id,
            config = new { }
        }
    };

    private static object Edge(string source, string target, bool disabled = false) => new
    {
        id = $"{source}-{target}",
        source,
        target,
        data = new { disabled },
    };
}
