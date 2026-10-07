using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Agents;
using NodePilot.Engine.Tests.Helpers;
using NodePilot.Engine.Triggers;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

[Collection("SerialEngineTests")]
public sealed class AgentReadWorkflowTests
{
    [Theory]
    [InlineData("wmiQuery", "{\"className\":\"Win32_Product\"}")]
    [InlineData("wmiQuery", "{\"mode\":\"invokeMethod\",\"className\":\"Win32_Service\"}")]
    [InlineData("restApi", "{\"method\":\"DELETE\"}")]
    [InlineData("restApi", "{\"method\":\"GET\",\"body\":\"payload\"}")]
    [InlineData("fileOperation", "{\"operation\":\"delete\"}")]
    [InlineData("folderOperation", "{\"operation\":\"create\"}")]
    [InlineData("registryOperation", "{\"operation\":\"write\"}")]
    [InlineData("runScript", "{\"script\":\"Get-CimInstance Win32_Product\"}")]
    [InlineData("custom:anything", "{}")]
    [InlineData("sql", "{\"query\":\"DELETE FROM records\"}")]
    [InlineData("startWorkflow", "{\"workflowNameOrId\":\"11111111-1111-1111-1111-111111111111\",\"waitForCompletion\":false}")]
    [InlineData("startWorkflow", "{\"workflowNameOrId\":\"{{manual.child}}\"}")]
    public void PreflightRejectsWritersUnknownActivitiesAndDetachedChildren(string type, string config)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentReadOnlyWorkflowScope.ValidateWorkflow(
            Workflow(type, JsonDocument.Parse(config).RootElement)));

    [Theory]
    [InlineData("wmiQuery", "{\"className\":\"Win32_Service\"}")]
    [InlineData("wmiQuery", "{\"mode\":\"wql\",\"query\":\"SELECT Name FROM Win32_Service\"}")]
    [InlineData("restApi", "{\"method\":\"GET\",\"url\":\"https://example.test/status\"}")]
    [InlineData("fileOperation", "{\"operation\":\"exists\",\"path\":\"C:\\\\Windows\\\\win.ini\"}")]
    [InlineData("folderOperation", "{\"operation\":\"list\"}")]
    [InlineData("registryOperation", "{\"operation\":\"read\"}")]
    [InlineData("runScript", "{\"script\":\"Get-Service | Select-Object Name,Status\"}")]
    [InlineData("startWorkflow", "{\"workflowNameOrId\":\"11111111-1111-1111-1111-111111111111\",\"waitForCompletion\":true}")]
    public void PreflightAllowsSupportedReads(string type, string config)
        => AgentReadOnlyWorkflowScope.ValidateWorkflow(Workflow(type, JsonDocument.Parse(config).RootElement));

    [Fact]
    public async Task ScopeFlowsThroughAsyncChildrenAndDoesNotLeakToOtherRuns()
    {
        Assert.False(AgentReadOnlyWorkflowScope.IsActive);
        async Task Child()
        {
            using var scope = AgentReadOnlyWorkflowScope.Enter();
            await Task.Run(() => Assert.True(AgentReadOnlyWorkflowScope.IsActive), TestContext.Current.CancellationToken);
            using (AgentReadOnlyWorkflowScope.Enter()) Assert.True(AgentReadOnlyWorkflowScope.IsActive);
            Assert.True(AgentReadOnlyWorkflowScope.IsActive);
        }
        await Child();
        Assert.False(AgentReadOnlyWorkflowScope.IsActive);
    }

    [Fact]
    public async Task RealEngineAllowsPureStepsButRejectsResolvedUnsafeScriptBeforePowerShellStarts()
    {
        var registry = new ActivityRegistry([new ManualTrigger(), new GenerateTextActivity(),
            new RunScriptActivity(null!, NullLogger<RunScriptActivity>.Instance)]);
        var (db, services, connection) = TestDbContext.CreateWithScopedServices(registry);
        await using var ownedDb = db; await using var ownedServices = services; await using var ownedConnection = connection;
        var engine = new WorkflowEngine(db, NullLogger<WorkflowEngine>.Instance, services, Mock.Of<IExecutionNotifier>());
        var safe = Workflow("generateText", JsonSerializer.SerializeToElement(new { mode = "guid" }));
        var unsafeScript = Workflow("runScript", JsonSerializer.SerializeToElement(new { script = "Get-CimInstance {{manual.class}}" }));
        safe.PublishedByUserId = null; unsafeScript.PublishedByUserId = null;
        db.Workflows.AddRange(safe, unsafeScript); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using (AgentReadOnlyWorkflowScope.Enter())
        {
            var success = await engine.ExecuteAsync(safe, "test", TestContext.Current.CancellationToken);
            Assert.Equal(ExecutionStatus.Succeeded, success.Status);
            var failure = await engine.ExecuteAsync(unsafeScript, "test", TestContext.Current.CancellationToken,
                new Dictionary<string, string> { ["class"] = "Win32_Product" });
            Assert.Equal(ExecutionStatus.Failed, failure.Status);
            var step = await db.StepExecutions.AsNoTracking().SingleAsync(s => s.WorkflowExecutionId == failure.Id && s.StepId == "check", TestContext.Current.CancellationToken);
            Assert.Contains("read-only", step.ErrorOutput);
            Assert.Equal(1, step.AttemptCount);
        }
        Assert.False(AgentReadOnlyWorkflowScope.IsActive);
    }

    [Fact]
    public void PublicationAndFixedIdentityAreRequired()
    {
        var workflow = Workflow("generateText", JsonSerializer.SerializeToElement(new { }));
        workflow.IsEnabled = false;
        Assert.Throws<UnauthorizedAccessException>(() => AgentReadOnlyWorkflowScope.ValidateWorkflow(workflow));
        workflow.IsEnabled = true; workflow.PublishedByUserId = null;
        Assert.Throws<UnauthorizedAccessException>(() => AgentReadOnlyWorkflowScope.ValidateWorkflow(workflow));
    }

    private static Workflow Workflow(string type, JsonElement config) => new()
    {
        Id = Guid.NewGuid(), Name = "read check", IsEnabled = true, PublishedByUserId = Guid.NewGuid(),
        DefinitionJson = JsonSerializer.Serialize(new
        {
            nodes = new[]
            {
                new { id = "start", type = "activity", data = new { activityType = "manualTrigger", config = JsonSerializer.SerializeToElement(new { }) } },
                new { id = "check", type = "activity", data = new { activityType = type, config } }
            },
            edges = new[] { new { id = "e", source = "start", target = "check" } }
        })
    };
}
