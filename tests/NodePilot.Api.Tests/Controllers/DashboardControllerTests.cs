using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NodePilot.Ai;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class DashboardControllerTests
{
    private static DashboardController NewController(NodePilot.Data.NodePilotDbContext db, string role = "Admin",
        IOptionsMonitor<LlmOptions>? llmOptions = null,
        NodePilot.Core.Interfaces.IMaintenanceWindowEvaluator? maintenance = null)
    {
        var controller = new DashboardController(db, new AlwaysAllowAuthorizationService(),
            llmOptions: llmOptions, maintenance: maintenance);
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
        // No LlmOptions monitor wired -> defaults to disabled.
        stats.LlmEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Get_LlmEnabled_ReflectsOptionsMonitor()
    {
        var db = TestDbFactory.Create();

        // Enabled monitor with a resolvable active profile -> banner surfaces "AI activated".
        var enabled = new StaticOptionsMonitor<LlmOptions>(LlmTestOptions.WithProfile());
        var statsEnabled = (await NewController(db, llmOptions: enabled).Get(CancellationToken.None))
            .Result.As<OkObjectResult>().Value.As<DashboardStats>();
        statsEnabled.LlmEnabled.Should().BeTrue();

        // Disabled monitor -> "AI disabled".
        var disabled = new StaticOptionsMonitor<LlmOptions>(LlmTestOptions.WithProfile(enabled: false));
        var statsDisabled = (await NewController(db, llmOptions: disabled).Get(CancellationToken.None))
            .Result.As<OkObjectResult>().Value.As<DashboardStats>();
        statsDisabled.LlmEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Get_LlmEnabled_IsFalseWhenNoActiveProfileResolves()
    {
        // The switch alone isn't enough: without an active profile every AI endpoint answers 503,
        // so the banner must not claim the feature is on.
        var db = TestDbFactory.Create();
        var monitor = new StaticOptionsMonitor<LlmOptions>(LlmTestOptions.EnabledWithoutProfile());

        var stats = (await NewController(db, llmOptions: monitor).Get(CancellationToken.None))
            .Result.As<OkObjectResult>().Value.As<DashboardStats>();

        stats.LlmEnabled.Should().BeFalse();
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

        // Enabled with scheduleTrigger -> armed
        db.Workflows.Add(new Workflow {
            Id = Guid.NewGuid(), Name = "Nightly Backup", IsEnabled = true, UpdatedAt = DateTime.UtcNow,
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"scheduleTrigger"}}]}""",
            TriggerTypesJson = """["scheduleTrigger"]"""
        });
        // Enabled with manualTrigger only -> NOT armed
        db.Workflows.Add(new Workflow {
            Id = Guid.NewGuid(), Name = "On-Demand", IsEnabled = true, UpdatedAt = DateTime.UtcNow,
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"manualTrigger"}}]}""",
            TriggerTypesJson = """["manualTrigger"]"""
        });
        // Disabled with scheduleTrigger -> NOT armed (kill-switch)
        db.Workflows.Add(new Workflow {
            Id = Guid.NewGuid(), Name = "Quarantined", IsEnabled = false, UpdatedAt = DateTime.UtcNow,
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"scheduleTrigger"}}]}""",
            TriggerTypesJson = """["scheduleTrigger"]"""
        });
        // Enabled with webhook + schedule -> armed with both
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

    // ---- Maintenance windows on the departure board ---------------------------------------
    //
    // The board must not promise a start that an active window will swallow. Crucially the
    // verdict is asked at the PREDICTED FIRE TIME, not at "now" — that is the same moment
    // TriggerOrchestrator evaluates, so a window active now but closed by the fire time must
    // NOT flag the row, and vice versa.

    /// <summary>An always-armed workflow: hourly cron, so NextFireUtc is within the next
    /// hour.</summary>
    /// <summary>
    /// A point in time strictly between "now" and the next firing of <see
    /// cref="ArmedCronWorkflow"/>'s
    /// cron, which is the top of the coming hour. The two blackout tests below split their verdict
    /// on
    /// this instant, so it must never land on or past the fire time — a fixed "now + 30 s" did
    /// exactly
    /// that whenever the suite ran in the last half minute of an hour, silently inverting both of
    /// them
    /// (observed in CI at 19:00:20Z). Anchoring on the actual boundary holds at every wall-clock
    /// moment.
    /// </summary>
    private static DateTime CutoffBeforeNextHourlyFire()
    {
        var now = DateTime.UtcNow;
        var nextFire = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
        return now.AddTicks(Math.Max(1, (nextFire - now).Ticks / 2));
    }

    private static Workflow ArmedCronWorkflow(string name = "Nightly Backup")
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsEnabled = true,
            UpdatedAt = DateTime.UtcNow,
            TriggerTypesJson = """["scheduleTrigger"]""",
            DefinitionJson = """
            {"nodes":[{"id":"t1","type":"scheduleTrigger","position":{"x":0,"y":0},
              "data":{"label":"Hourly","activityType":"scheduleTrigger",
              "config":{"cronExpression":"0 0 * * * ?"}}}],"edges":[]}
            """,
        };

    [Fact]
    public async Task Get_ArmedTrigger_NoEvaluatorWired_BlockedByWindowNameIsNull()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(ArmedCronWorkflow());
        await db.SaveChangesAsync();

        var result = await NewController(db).Get(CancellationToken.None);

        var stats = result.Result.As<OkObjectResult>().Value.As<DashboardStats>();
        stats.ArmedTriggers.Should().ContainSingle().Which.BlockedByWindowName.Should().BeNull();
    }

    [Fact]
    public async Task Get_ArmedTrigger_NoWindowBlocks_BlockedByWindowNameIsNull()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(ArmedCronWorkflow());
        await db.SaveChangesAsync();

        var result = await NewController(db, maintenance: StubMaintenanceWindowEvaluator.AllowAll)
            .Get(CancellationToken.None);

        var stats = result.Result.As<OkObjectResult>().Value.As<DashboardStats>();
        stats.ArmedTriggers.Should().ContainSingle().Which.BlockedByWindowName.Should().BeNull();
    }

    [Fact]
    public async Task Get_ArmedTrigger_BlackoutActiveAtFireTimeButNotNow_IsFlagged()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(ArmedCronWorkflow());
        await db.SaveChangesAsync();

        // Blocks only from the cutoff on — i.e. not "now", but by the time the cron fires.
        var cutoff = CutoffBeforeNextHourlyFire();
        var evaluator = new StubMaintenanceWindowEvaluator
        {
            VerdictAt = at => at >= cutoff
                ? new MaintenanceEvaluation(true, Guid.NewGuid(), "Weekend Freeze", null, MaintenanceMode.Blackout)
                : MaintenanceEvaluation.Allowed,
        };

        var result = await NewController(db, maintenance: evaluator).Get(CancellationToken.None);

        var stats = result.Result.As<OkObjectResult>().Value.As<DashboardStats>();
        stats.ArmedTriggers.Should().ContainSingle().Which.BlockedByWindowName.Should().Be("Weekend Freeze");
    }

    [Fact]
    public async Task Get_ArmedTrigger_BlackoutActiveNowButClosedByFireTime_IsNotFlagged()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(ArmedCronWorkflow());
        await db.SaveChangesAsync();

        // The honest inverse: evaluating at "now" would have flagged this row wrongly.
        var cutoff = CutoffBeforeNextHourlyFire();
        var evaluator = new StubMaintenanceWindowEvaluator
        {
            VerdictAt = at => at < cutoff
                ? new MaintenanceEvaluation(true, Guid.NewGuid(), "Ends Soon", null, MaintenanceMode.Blackout)
                : MaintenanceEvaluation.Allowed,
        };

        var result = await NewController(db, maintenance: evaluator).Get(CancellationToken.None);

        var stats = result.Result.As<OkObjectResult>().Value.As<DashboardStats>();
        var armed = stats.ArmedTriggers.Should().ContainSingle().Subject;
        armed.NextFireUtc.Should().NotBeNull();
        armed.BlockedByWindowName.Should().BeNull();
        evaluator.Calls.Should().ContainSingle()
            .Which.NowUtc.Should().Be(armed.NextFireUtc!.Value);
    }

    [Fact]
    public async Task Get_ArmedTrigger_EventDriven_HasNoPrediction_IsEvaluatedAtNow()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "Webhook Job", IsEnabled = true, UpdatedAt = DateTime.UtcNow,
            TriggerTypesJson = """["webhookTrigger"]""",
            DefinitionJson = "{}",
        });
        await db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var evaluator = StubMaintenanceWindowEvaluator.Blocking("Global Freeze");
        var result = await NewController(db, maintenance: evaluator).Get(CancellationToken.None);

        var stats = result.Result.As<OkObjectResult>().Value.As<DashboardStats>();
        var armed = stats.ArmedTriggers.Should().ContainSingle().Subject;
        armed.NextFireUtc.Should().BeNull();
        armed.BlockedByWindowName.Should().Be("Global Freeze");
        // No prediction to aim at -> the only honest question is "is it blocked right now".
        evaluator.Calls.Should().ContainSingle().Which.NowUtc.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task Get_ArmedTrigger_AllowOnlyBlock_IsFlagged()
    {
        // GetWindowsAffecting cannot express "outside the allow-only window"; Evaluate can.
        // This test pins that the controller uses the latter.
        var db = TestDbFactory.Create();
        db.Workflows.Add(ArmedCronWorkflow());
        await db.SaveChangesAsync();

        var evaluator = new StubMaintenanceWindowEvaluator
        {
            Verdict = new MaintenanceEvaluation(true, Guid.NewGuid(), "Business Hours Only", null, MaintenanceMode.AllowOnly),
        };

        var result = await NewController(db, maintenance: evaluator).Get(CancellationToken.None);

        var stats = result.Result.As<OkObjectResult>().Value.As<DashboardStats>();
        stats.ArmedTriggers.Should().ContainSingle().Which.BlockedByWindowName.Should().Be("Business Hours Only");
    }

    [Fact]
    public async Task Get_ArmedTrigger_EvaluatedWithTheWorkflowsOwnFolderId()
    {
        // Windows target a workflow directly or via folder ancestry — passing the wrong folder
        // would silently mis-evaluate every scoped window.
        var db = TestDbFactory.Create();
        var folderId = Guid.NewGuid();
        db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = folderId,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "Prod",
            Path = "/Prod",
            Depth = 1,
        });
        var wf = ArmedCronWorkflow();
        wf.FolderId = folderId;
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var evaluator = StubMaintenanceWindowEvaluator.AllowAll;
        await NewController(db, maintenance: evaluator).Get(CancellationToken.None);

        var call = evaluator.Calls.Should().ContainSingle().Subject;
        call.WorkflowId.Should().Be(wf.Id);
        call.FolderId.Should().Be(folderId);
    }
}
