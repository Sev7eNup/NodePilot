using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class JunctionActivityTests
{
    private readonly JunctionActivity _activity = new();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task ExecuteAsync_DefaultMode_IsWaitAll()
    {
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "j1" };
        var result = await _activity.ExecuteAsync(ctx, Parse("{}"), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("\"mode\": \"waitAll\"");
    }

    [Fact]
    public async Task ExecuteAsync_AggregatesUpstreamOutputParameters_OnlyFromPreviousResults()
    {
        // Junction post-A5 hardening: aggregates ONLY upstream OutputParameters via the
        // engine-provided PreviousResults dict. It does NOT scan the flat Variables map
        // (which would leak globals.*, manual.*, .output/.error from non-converging steps).
        var ctx = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "j1",
            Variables = new Dictionary<string, string>
            {
                ["globals.SECRET"] = "leak-me",
                ["manual.ignored"] = "should-not-leak",
                ["unrelated.output"] = "noise",
            },
            PreviousResults = new Dictionary<string, ActivityResult>
            {
                ["branchA"] = new ActivityResult { Success = true, OutputParameters = new Dictionary<string, string> { ["count"] = "42" } },
                ["branchB"] = new ActivityResult { Success = true, OutputParameters = new Dictionary<string, string> { ["state"] = "done" } },
            },
        };

        var result = await _activity.ExecuteAsync(ctx, Parse("{\"mode\":\"waitAny\"}"), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("count").WhoseValue.Should().Be("42");
        result.OutputParameters.Should().ContainKey("state").WhoseValue.Should().Be("done");
        result.OutputParameters.Should().NotContainKey("globals.SECRET");
        result.OutputParameters.Should().NotContainKey("manual.ignored");
        result.OutputParameters.Should().NotContainKey("unrelated.output");
        result.Output.Should().NotContain("leak-me");
    }

    [Fact]
    public async Task ExecuteAsync_WaitNofM_EmitsModeInOutput()
    {
        var ctx = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "j1",
            Variables = new Dictionary<string, string> { ["a.output"] = "x" },
        };

        var result = await _activity.ExecuteAsync(ctx, Parse("{\"mode\":\"waitNofM\",\"n\":2}"), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("waitNofM");
    }
}

public sealed class ReturnDataActivityTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db = TestDbContext.Create();

    public void Dispose() => _db.Dispose();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task ExecuteAsync_MissingData_ReturnsError()
    {
        var activity = new ReturnDataActivity(_db);
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "r1" };

        var result = await activity.ExecuteAsync(ctx, Parse("{}"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("data");
    }

    [Fact]
    public async Task ExecuteAsync_DataNonObject_ReturnsError()
    {
        var activity = new ReturnDataActivity(_db);
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "r1" };

        var result = await activity.ExecuteAsync(ctx, Parse("{\"data\":\"not-an-object\"}"), CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_PersistsReturnDataOnExecutionRow()
    {
        var execId = Guid.NewGuid();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        _db.Workflows.Add(wf);
        _db.WorkflowExecutions.Add(new WorkflowExecution { Id = execId, WorkflowId = wf.Id });
        await _db.SaveChangesAsync();

        var activity = new ReturnDataActivity(_db);
        var ctx = new StepExecutionContext { WorkflowExecutionId = execId, StepId = "r1" };
        var config = Parse("{\"data\":{\"status\":\"ok\",\"count\":7}}");

        var result = await activity.ExecuteAsync(ctx, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["status"].Should().Be("ok");
        result.OutputParameters["count"].Should().Be("7");

        var reloaded = await _db.WorkflowExecutions.AsNoTracking().FirstAsync(e => e.Id == execId);
        reloaded.ReturnData.Should().NotBeNullOrEmpty();
        reloaded.ReturnData.Should().Contain("\"status\":\"ok\"");
    }

    [Fact]
    public async Task ExecuteAsync_RemovesPerExecutionLockAfterWrite()
    {
        var execId = Guid.NewGuid();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        _db.Workflows.Add(wf);
        _db.WorkflowExecutions.Add(new WorkflowExecution { Id = execId, WorkflowId = wf.Id });
        await _db.SaveChangesAsync();

        var activity = new ReturnDataActivity(_db);
        var ctx = new StepExecutionContext { WorkflowExecutionId = execId, StepId = "r1" };

        var before = ReturnDataActivity.ActiveLockCount;
        var result = await activity.ExecuteAsync(ctx, Parse("{\"data\":{\"status\":\"ok\"}}"), CancellationToken.None);

        result.Success.Should().BeTrue();
        ReturnDataActivity.ActiveLockCount.Should().Be(before);
    }
}

public sealed class StartWorkflowActivityTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db = TestDbContext.Create();

    public void Dispose() => _db.Dispose();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private StartWorkflowActivity CreateActivity() =>
        new(Mock.Of<IServiceScopeFactory>(), _db, new InMemorySubWorkflowGate());

    [Fact]
    public async Task ExecuteAsync_MissingWorkflowNameOrId_ReturnsError()
    {
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "sw1" };

        var result = await activity.ExecuteAsync(ctx, Parse("{}"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("workflowNameOrId");
    }

    [Fact]
    public async Task ExecuteAsync_WorkflowNotFound_ReturnsError()
    {
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "sw1" };

        var result = await activity.ExecuteAsync(ctx, Parse("{\"workflowNameOrId\":\"DoesNotExist\"}"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_DisabledWorkflow_ReturnsError()
    {
        var child = new Workflow { Id = Guid.NewGuid(), Name = "Child", DefinitionJson = "{}", IsEnabled = false };
        _db.Workflows.Add(child);
        await _db.SaveChangesAsync();

        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "sw1" };

        var result = await activity.ExecuteAsync(ctx, Parse($"{{\"workflowNameOrId\":\"Child\"}}"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("disabled");
    }

    [Fact]
    public async Task ExecuteAsync_LockedChildWorkflow_ReturnsError()
    {
        // Lock atomically forces IsEnabled=false (see WorkflowsController.Lock), so a workflow
        // that is currently checked out for editing is automatically off-limits as a child.
        // This test pins that contract so a future "let lock-state preserve IsEnabled" change
        // would have to also change here — and a reviewer notices.
        var child = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Child",
            DefinitionJson = "{}",
            IsEnabled = false,
            CheckedOutByUserId = Guid.NewGuid(),
            CheckedOutAt = DateTime.UtcNow,
        };
        _db.Workflows.Add(child);
        await _db.SaveChangesAsync();

        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "sw1" };

        var result = await activity.ExecuteAsync(ctx, Parse($"{{\"workflowNameOrId\":\"Child\"}}"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("disabled");
    }

    [Fact]
    public async Task ExecuteAsync_DirectSelfCall_IsRejected()
    {
        var parentWorkflow = new Workflow { Id = Guid.NewGuid(), Name = "Parent", DefinitionJson = "{}" };
        _db.Workflows.Add(parentWorkflow);
        var execId = Guid.NewGuid();
        _db.WorkflowExecutions.Add(new WorkflowExecution { Id = execId, WorkflowId = parentWorkflow.Id });
        await _db.SaveChangesAsync();

        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = execId, StepId = "sw1" };

        var result = await activity.ExecuteAsync(
            ctx,
            Parse($"{{\"workflowNameOrId\":\"{parentWorkflow.Id}\"}}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("self-invocation");
    }

    [Fact]
    public async Task ExecuteAsync_MaxCallDepthExceeded_IsRejected()
    {
        var child = new Workflow { Id = Guid.NewGuid(), Name = "Child", DefinitionJson = "{}" };
        _db.Workflows.Add(child);
        await _db.SaveChangesAsync();

        var activity = CreateActivity();
        var ctx = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "sw1",
            Variables = new Dictionary<string, string> { ["manual.__callDepth"] = "10" },
        };

        var result = await activity.ExecuteAsync(ctx, Parse("{\"workflowNameOrId\":\"Child\"}"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("call depth");
    }
}

public sealed class ForEachActivityTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db = TestDbContext.Create();

    public void Dispose() => _db.Dispose();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private ForEachActivity CreateActivity() => new(Mock.Of<IServiceScopeFactory>(), _db, new InMemorySubWorkflowGate());

    [Fact]
    public async Task ExecuteAsync_MissingChildWorkflowNameOrId_ReturnsError()
    {
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx, Parse("{\"items\":\"[]\"}"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("childWorkflowNameOrId");
    }

    [Fact]
    public async Task ExecuteAsync_MissingItems_ReturnsError()
    {
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx, Parse("{\"childWorkflowNameOrId\":\"X\"}"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("items");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCollection_ReturnsSuccessWithZeroTotal()
    {
        // Empty collection is a no-op success — downstream decides via param.total == 0.
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("{\"childWorkflowNameOrId\":\"X\",\"items\":\"[]\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["total"].Should().Be("0");
        result.OutputParameters["succeeded"].Should().Be("0");
        result.OutputParameters["failed"].Should().Be("0");
        result.OutputParameters["results"].Should().Be("[]");
    }

    [Fact]
    public async Task ExecuteAsync_ReservedItemParamPrefix_IsRejected()
    {
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("{\"childWorkflowNameOrId\":\"X\",\"items\":\"[\\\"a\\\"]\",\"itemParameterName\":\"__callDepth\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("reserved");
    }

    [Fact]
    public async Task ExecuteAsync_ChildWorkflowNotFound_ReturnsError()
    {
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("{\"childWorkflowNameOrId\":\"Missing\",\"items\":\"[\\\"a\\\"]\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_DisabledChildWorkflow_ReturnsError()
    {
        var child = new Workflow { Id = Guid.NewGuid(), Name = "Child", DefinitionJson = "{}", IsEnabled = false };
        _db.Workflows.Add(child);
        await _db.SaveChangesAsync();

        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("{\"childWorkflowNameOrId\":\"Child\",\"items\":\"[\\\"a\\\"]\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("disabled");
    }

    [Fact]
    public async Task ExecuteAsync_SelfInvocation_IsRejected()
    {
        var parent = new Workflow { Id = Guid.NewGuid(), Name = "Loop", DefinitionJson = "{}" };
        _db.Workflows.Add(parent);
        var execId = Guid.NewGuid();
        _db.WorkflowExecutions.Add(new WorkflowExecution { Id = execId, WorkflowId = parent.Id });
        await _db.SaveChangesAsync();

        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = execId, StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse($"{{\"childWorkflowNameOrId\":\"{parent.Id}\",\"items\":\"[\\\"a\\\"]\"}}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("self-invocation");
    }

    [Fact]
    public async Task ExecuteAsync_MaxCallDepthExceeded_IsRejected()
    {
        var child = new Workflow { Id = Guid.NewGuid(), Name = "Child", DefinitionJson = "{}" };
        _db.Workflows.Add(child);
        await _db.SaveChangesAsync();

        var activity = CreateActivity();
        var ctx = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "fe1",
            Variables = new Dictionary<string, string> { ["manual.__callDepth"] = "10" },
        };

        var result = await activity.ExecuteAsync(ctx,
            Parse("{\"childWorkflowNameOrId\":\"Child\",\"items\":\"[\\\"a\\\"]\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("call depth");
    }

    [Fact]
    public async Task ExecuteAsync_ReservedStaticParamPrefix_IsRejected()
    {
        var child = new Workflow { Id = Guid.NewGuid(), Name = "Child", DefinitionJson = "{}" };
        _db.Workflows.Add(child);
        await _db.SaveChangesAsync();

        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("{\"childWorkflowNameOrId\":\"Child\",\"items\":\"[\\\"a\\\"]\",\"parameters\":{\"__callDepth\":\"0\"}}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("reserved");
    }

    [Fact]
    public async Task ExecuteAsync_LinesFormat_ParsesNewlineSeparatedAndPassesParsingStage()
    {
        // "host1\nhost2\n\nhost3\n" → 3 items. Unknown child → fails AFTER parsing, confirming
        // the parse itself didn't blow up and we reached the child-lookup stage.
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("{\"childWorkflowNameOrId\":\"Unknown\",\"items\":\"host1\\nhost2\\n\\nhost3\\n\",\"itemsFormat\":\"lines\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_StrictJsonWithNonArray_ReturnsParseError()
    {
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("{\"childWorkflowNameOrId\":\"X\",\"items\":\"not-json\",\"itemsFormat\":\"json\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("failed to parse");
    }
}
