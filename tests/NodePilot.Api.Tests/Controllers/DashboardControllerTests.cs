using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NodePilot.Ai;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class DashboardControllerTests
{
    private static DashboardController NewController(NodePilot.Data.NodePilotDbContext db, string role = "Admin",
        IOptionsMonitor<LlmOptions>? llmOptions = null)
    {
        var controller = new DashboardController(db, new AlwaysAllowAuthorizationService(), llmOptions: llmOptions);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, role) }, "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static WorkflowExecution MakeExecution(Guid workflowId, ExecutionStatus status,
        DateTime? startedAt = null, DateTime? completedAt = null)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            Status = status,
            StartedAt = startedAt ?? DateTime.UtcNow,
            CompletedAt = completedAt
        };

    [Fact]
    public async Task Get_EmptyDb_ReturnsAllZeros()
    {
        var db = TestDbFactory.Create();
        var result = await NewController(db).Get(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = ok.Value.Should().BeAssignableTo<DashboardStats>().Subject;

        stats.WorkflowsTotal.Should().Be(0);
        stats.WorkflowsEnabled.Should().Be(0);
        stats.MachinesTotal.Should().Be(0);
        stats.Last24h.Total.Should().Be(0);
        stats.Last24h.Succeeded.Should().Be(0);
        stats.Last24h.Failed.Should().Be(0);
        stats.TopWorkflows.Should().BeEmpty();
        // No LlmOptions monitor wired → defaults to disabled.
        stats.LlmEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Get_LlmEnabled_ReflectsOptionsMonitor()
    {
        var db = TestDbFactory.Create();

        // Enabled monitor → banner surfaces "AI activated".
        var enabled = new StaticOptionsMonitor<LlmOptions>(new LlmOptions { Enabled = true });
        var statsEnabled = (await NewController(db, llmOptions: enabled).Get(CancellationToken.None))
            .Result.As<OkObjectResult>().Value.As<DashboardStats>();
        statsEnabled.LlmEnabled.Should().BeTrue();

        // Disabled monitor → "AI disabled".
        var disabled = new StaticOptionsMonitor<LlmOptions>(new LlmOptions { Enabled = false });
        var statsDisabled = (await NewController(db, llmOptions: disabled).Get(CancellationToken.None))
            .Result.As<OkObjectResult>().Value.As<DashboardStats>();
        statsDisabled.LlmEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Get_MixedStatuses_CountsCorrectly()
    {
        var db = TestDbFactory.Create();
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = wfId, Name = "W", DefinitionJson = "{}", IsEnabled = true, UpdatedAt = DateTime.UtcNow });
        db.WorkflowExecutions.AddRange(
            MakeExecution(wfId, ExecutionStatus.Succeeded),
            MakeExecution(wfId, ExecutionStatus.Succeeded),
            MakeExecution(wfId, ExecutionStatus.Failed),
            MakeExecution(wfId, ExecutionStatus.Running),
            MakeExecution(wfId, ExecutionStatus.Cancelled));
        await db.SaveChangesAsync();

        var result = await NewController(db).Get(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = ok.Value.Should().BeAssignableTo<DashboardStats>().Subject;
        stats.Last24h.Total.Should().Be(5);
        stats.Last24h.Succeeded.Should().Be(2);
        stats.Last24h.Failed.Should().Be(1);
        stats.Last24h.Running.Should().Be(1);
        stats.Last24h.Cancelled.Should().Be(1);
    }

    [Fact]
    public async Task Get_Returns24HourBuckets()
    {
        var db = TestDbFactory.Create();
        var result = await NewController(db).Get(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = ok.Value.Should().BeAssignableTo<DashboardStats>().Subject;
        stats.Last24hBuckets.Should().HaveCount(24);
    }

    [Fact]
    public async Task Get_OldExecution_NotCountedIn24hStats()
    {
        var db = TestDbFactory.Create();
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = wfId, Name = "W", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow });
        // Execution older than 24h
        db.WorkflowExecutions.Add(MakeExecution(wfId, ExecutionStatus.Succeeded,
            startedAt: DateTime.UtcNow.AddDays(-2)));
        await db.SaveChangesAsync();

        var result = await NewController(db).Get(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = ok.Value.Should().BeAssignableTo<DashboardStats>().Subject;
        // The execution is counted in the all-time total but NOT in 24h counts
        stats.ExecutionsTotal.Should().Be(1);
        stats.Last24h.Total.Should().Be(0);
    }

    [Fact]
    public async Task Get_TopWorkflowsByRunCount_OrderedDescending()
    {
        var db = TestDbFactory.Create();
        var wf1Id = Guid.NewGuid();
        var wf2Id = Guid.NewGuid();
        db.Workflows.AddRange(
            new Workflow { Id = wf1Id, Name = "Rare", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow },
            new Workflow { Id = wf2Id, Name = "Frequent", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow });
        db.WorkflowExecutions.Add(MakeExecution(wf1Id, ExecutionStatus.Succeeded));
        for (var i = 0; i < 5; i++)
            db.WorkflowExecutions.Add(MakeExecution(wf2Id, ExecutionStatus.Succeeded));
        await db.SaveChangesAsync();

        var result = await NewController(db).Get(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = ok.Value.Should().BeAssignableTo<DashboardStats>().Subject;
        stats.TopWorkflows.Should().HaveCountGreaterThanOrEqualTo(1);
        stats.TopWorkflows[0].Name.Should().Be("Frequent");
    }

    [Fact]
    public async Task Get_WorkflowCounts_EnabledVsDisabled()
    {
        var db = TestDbFactory.Create();
        db.Workflows.AddRange(
            new Workflow { Id = Guid.NewGuid(), Name = "Active1", DefinitionJson = "{}", IsEnabled = true, UpdatedAt = DateTime.UtcNow },
            new Workflow { Id = Guid.NewGuid(), Name = "Active2", DefinitionJson = "{}", IsEnabled = true, UpdatedAt = DateTime.UtcNow },
            new Workflow { Id = Guid.NewGuid(), Name = "Disabled", DefinitionJson = "{}", IsEnabled = false, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await NewController(db).Get(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = ok.Value.Should().BeAssignableTo<DashboardStats>().Subject;
        stats.WorkflowsTotal.Should().Be(3);
        stats.WorkflowsEnabled.Should().Be(2);
    }

    [Fact]
    public async Task Get_ArmedTriggers_OnlyEnabledWorkflowsWithAutomaticTrigger()
    {
        var db = TestDbFactory.Create();

        // Enabled with scheduleTrigger → armed
        db.Workflows.Add(new Workflow {
            Id = Guid.NewGuid(), Name = "Nightly Backup", IsEnabled = true, UpdatedAt = DateTime.UtcNow,
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"scheduleTrigger"}}]}""",
            TriggerTypesJson = """["scheduleTrigger"]"""
        });
        // Enabled with manualTrigger only → NOT armed
        db.Workflows.Add(new Workflow {
            Id = Guid.NewGuid(), Name = "On-Demand", IsEnabled = true, UpdatedAt = DateTime.UtcNow,
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"manualTrigger"}}]}""",
            TriggerTypesJson = """["manualTrigger"]"""
        });
        // Disabled with scheduleTrigger → NOT armed (kill-switch)
        db.Workflows.Add(new Workflow {
            Id = Guid.NewGuid(), Name = "Quarantined", IsEnabled = false, UpdatedAt = DateTime.UtcNow,
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"scheduleTrigger"}}]}""",
            TriggerTypesJson = """["scheduleTrigger"]"""
        });
        // Enabled with webhook + schedule → armed with both
        db.Workflows.Add(new Workflow {
            Id = Guid.NewGuid(), Name = "Alert Pipeline", IsEnabled = true, UpdatedAt = DateTime.UtcNow,
            DefinitionJson = """{"nodes":[{"id":"a","data":{"activityType":"webhookTrigger"}},{"id":"b","data":{"activityType":"scheduleTrigger"}}]}""",
            TriggerTypesJson = """["scheduleTrigger","webhookTrigger"]"""
        });
        await db.SaveChangesAsync();

        var result = await NewController(db).Get(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = ok.Value.Should().BeAssignableTo<DashboardStats>().Subject;

        stats.ArmedTriggers.Should().HaveCount(2);
        stats.ArmedTriggers.Should().Contain(a => a.WorkflowName == "Nightly Backup"
            && a.TriggerTypes.SequenceEqual(new[] { "scheduleTrigger" }));
        stats.ArmedTriggers.Should().Contain(a => a.WorkflowName == "Alert Pipeline"
            && a.TriggerTypes.Contains("webhookTrigger") && a.TriggerTypes.Contains("scheduleTrigger"));
    }

    [Fact]
    public async Task Get_MachineCounts_ReachableVsUnreachable()
    {
        var db = TestDbFactory.Create();
        db.ManagedMachines.AddRange(
            new ManagedMachine { Id = Guid.NewGuid(), Name = "Up1", Hostname = "h1", IsReachable = true },
            new ManagedMachine { Id = Guid.NewGuid(), Name = "Up2", Hostname = "h2", IsReachable = true },
            new ManagedMachine { Id = Guid.NewGuid(), Name = "Down", Hostname = "h3", IsReachable = false });
        await db.SaveChangesAsync();

        var result = await NewController(db).Get(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = ok.Value.Should().BeAssignableTo<DashboardStats>().Subject;
        stats.MachinesTotal.Should().Be(3);
        stats.MachinesReachable.Should().Be(2);
    }

    [Fact]
    public async Task Get_WindowHours1_ReturnsSingleHourBucket()
    {
        var db = TestDbFactory.Create();
        var result = await NewController(db).Get(CancellationToken.None, windowHours: 1);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = ok.Value.Should().BeAssignableTo<DashboardStats>().Subject;
        stats.Last24hBuckets.Should().HaveCount(1);
    }

    [Fact]
    public async Task Get_WindowHours1_CurrentHourExecution_AppearsInOnlyBucket()
    {
        var db = TestDbFactory.Create();
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = wfId, Name = "W", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow });
        db.WorkflowExecutions.Add(MakeExecution(wfId, ExecutionStatus.Succeeded, startedAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        var result = await NewController(db).Get(CancellationToken.None, windowHours: 1);

        var stats = result.Result.As<OkObjectResult>().Value.As<DashboardStats>();
        stats.Last24h.Total.Should().Be(1);
        stats.Last24h.Succeeded.Should().Be(1);
        stats.Last24hBuckets.Should().ContainSingle()
            .Which.Succeeded.Should().Be(1);
    }

    [Fact]
    public async Task Get_WindowHours7d_Returns24AggregatedBuckets()
    {
        var db = TestDbFactory.Create();
        var result = await NewController(db).Get(CancellationToken.None, windowHours: 168);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = ok.Value.Should().BeAssignableTo<DashboardStats>().Subject;
        // >24 h windows fold into ≤24 display buckets so chart density stays constant.
        stats.Last24hBuckets.Should().HaveCount(24);
    }

    [Fact]
    public async Task Get_WindowHours30d_Returns24AggregatedBuckets()
    {
        var db = TestDbFactory.Create();
        var result = await NewController(db).Get(CancellationToken.None, windowHours: 720);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var stats = ok.Value.Should().BeAssignableTo<DashboardStats>().Subject;
        stats.Last24hBuckets.Should().HaveCount(24);
    }

    [Fact]
    public async Task Get_WindowHoursOutOfRange_ClampsToDefault24()
    {
        var db = TestDbFactory.Create();
        // 0 and absurd values both clamp to 24 rather than rejecting.
        var resultZero = await NewController(db).Get(CancellationToken.None, windowHours: 0);
        var resultHuge = await NewController(db).Get(CancellationToken.None, windowHours: 99999);

        var statsZero = resultZero.Result.As<OkObjectResult>().Value.As<DashboardStats>();
        var statsHuge = resultHuge.Result.As<OkObjectResult>().Value.As<DashboardStats>();
        statsZero.Last24hBuckets.Should().HaveCount(24);
        statsHuge.Last24hBuckets.Should().HaveCount(24);
    }

    [Fact]
    public async Task Get_WindowHours7d_IncludesExecutionOlderThan24h()
    {
        var db = TestDbFactory.Create();
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new Workflow { Id = wfId, Name = "W", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow });
        // 2 days old: outside the 24 h default window, inside the 7 d window.
        db.WorkflowExecutions.Add(MakeExecution(wfId, ExecutionStatus.Succeeded,
            startedAt: DateTime.UtcNow.AddDays(-2)));
        await db.SaveChangesAsync();

        var resultDefault = await NewController(db).Get(CancellationToken.None);
        var result7d = await NewController(db).Get(CancellationToken.None, windowHours: 168);

        var statsDefault = resultDefault.Result.As<OkObjectResult>().Value.As<DashboardStats>();
        var stats7d = result7d.Result.As<OkObjectResult>().Value.As<DashboardStats>();
        statsDefault.Last24h.Total.Should().Be(0);
        stats7d.Last24h.Total.Should().Be(1);
        stats7d.Last24h.Succeeded.Should().Be(1);
    }
}
