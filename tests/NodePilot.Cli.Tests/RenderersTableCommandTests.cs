using FluentAssertions;
using NodePilot.Cli;
using NodePilot.Cli.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// Drives every <c>np</c> command whose render callback lives in
/// <see cref="NodePilot.Cli.Output.Renderers"/> with <c>-o table</c> and a stubbed API
/// response, so the human-facing table/grid renderers actually execute through the real
/// Spectre command pipeline (JSON is the harness default and bypasses the render lambda).
///
/// <para>Assertions target rendered content that survives the harness's 80-column,
/// no-colour output: short single-token cells for the many-column list tables, and the
/// richer multi-word values for the 2-column detail grids. The point is to prove the
/// renderer ran and emitted real values — not just that the exit code was 0.</para>
/// </summary>
[Collection(CommandTestCollection.Name)]
public class RenderersTableCommandTests
{
    // Full WorkflowResponse JSON body — every field the CLI DTO expects. `name`, `enabled`,
    // lock owner, and last-execution vary per call so the branch-heavy Workflows renderer
    // lights up both sides (enabled/disabled, locked/free, last-run present/absent).
    private static object WorkflowJson(
        Guid id, string name, bool enabled, string? lockOwner, bool withLastExec,
        string? description = null, string[]? triggers = null) => new
    {
        id,
        name,
        description,
        definitionJson = "{}",
        version = 4,
        isEnabled = enabled,
        createdAt = DateTime.UtcNow,
        updatedAt = DateTime.UtcNow,
        createdBy = (string?)null,
        updatedBy = "admin",
        activityCount = 3,
        triggerTypes = triggers ?? new[] { "manualTrigger" },
        lastExecution = withLastExec
            ? new
            {
                id = Guid.NewGuid(),
                status = "Succeeded",
                startedAt = DateTime.UtcNow,
                completedAt = DateTime.UtcNow,
                durationMs = 1200L,
            }
            : null,
        successCount = 1,
        totalCount = 1,
        avgDurationMs = 100.0,
        checkedOutByUserId = lockOwner is null ? (Guid?)null : Guid.NewGuid(),
        checkedOutByUserName = lockOwner,
        checkedOutAt = lockOwner is null ? (DateTime?)null : DateTime.UtcNow,
    };

    // ---- workflow list / get / versions / version --------------------------

    [Fact]
    public void WorkflowList_Table_RendersEnabledDisabledAndLockOwner()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/workflows").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                WorkflowJson(Guid.NewGuid(), "Deploy", enabled: true, lockOwner: "bob", withLastExec: true),
                WorkflowJson(Guid.NewGuid(), "Cleanup", enabled: false, lockOwner: null, withLastExec: false),
            }));

        var result = h.Run("workflow", "list", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Deploy").And.Contain("Cleanup");
        result.Output.Should().Contain("bob"); // yellow lock-owner branch
    }

    [Fact]
    public void WorkflowGet_Table_RendersDetailGrid()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(
                WorkflowJson(id, "NightlyDeploy", enabled: true, lockOwner: "alice", withLastExec: true,
                    description: "runs nightly", triggers: new[] { "scheduleTrigger" })));

        var result = h.Run("workflow", "get", id.ToString(), "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("NightlyDeploy");
        result.Output.Should().Contain("runs nightly");   // Description row
        result.Output.Should().Contain("scheduleTrigger"); // Triggers row
        result.Output.Should().Contain("alice");           // Lock row
    }

    [Fact]
    public void WorkflowVersions_Table_RendersVersionRows()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(
                WorkflowJson(id, "wf", enabled: true, lockOwner: null, withLastExec: false)));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/versions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new { version = 2, name = "rel-2", createdAt = DateTime.UtcNow, createdBy = "admin", changeNote = "fix", isCurrent = true },
                new { version = 1, name = "rel-1", createdAt = DateTime.UtcNow.AddDays(-1), createdBy = "admin", changeNote = (string?)null, isCurrent = false },
            }));

        var result = h.Run("workflow", "versions", id.ToString(), "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("rel-2").And.Contain("rel-1");
        result.Output.Should().Contain("admin");
    }

    [Fact]
    public void WorkflowVersionGet_Table_RendersVersionDetailWithByteCount()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var def = "{\"nodes\":[]}";
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(
                WorkflowJson(id, "wf", enabled: true, lockOwner: null, withLastExec: false)));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/versions/3").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                version = 3, name = "rel-3", description = "third cut", definitionJson = def,
                createdAt = DateTime.UtcNow, createdBy = "admin", changeNote = "hotfix", isCurrent = true,
            }));

        var result = h.Run("workflow", "version", id.ToString(), "3", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("rel-3").And.Contain("hotfix");
        result.Output.Should().Contain($"{def.Length} bytes"); // Definition byte-count row
    }

    // ---- exec list / get / steps -------------------------------------------

    [Fact]
    public void ExecList_Table_RendersRows()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/executions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new
                {
                    id = Guid.NewGuid(), workflowId = Guid.NewGuid(), status = "Succeeded",
                    startedAt = DateTime.UtcNow.AddMinutes(-5), completedAt = DateTime.UtcNow, // → duration cell
                    triggeredBy = "alice", errorMessage = (string?)null,
                },
                new
                {
                    id = Guid.NewGuid(), workflowId = Guid.NewGuid(), status = "Running",
                    startedAt = DateTime.UtcNow, completedAt = (DateTime?)null, // → "-" duration branch
                    triggeredBy = (string?)null, errorMessage = (string?)null,
                },
            }));

        var result = h.Run("exec", "list", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("alice"); // TriggeredBy of the completed row
    }

    [Fact]
    public void ExecGet_Table_RendersDetailWithErrorAndTrace()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/executions/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id, workflowId = Guid.NewGuid(), status = "Failed",
                startedAt = DateTime.UtcNow.AddMinutes(-1), completedAt = DateTime.UtcNow,
                triggeredBy = "scheduler", errorMessage = "disk full", traceId = "trace-xyz",
            }));

        var result = h.Run("exec", "get", id.ToString(), "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("disk full");  // Error row (only rendered when present)
        result.Output.Should().Contain("trace-xyz");  // Trace row (only rendered when present)
        result.Output.Should().Contain("scheduler");
    }

    [Fact]
    public void ExecSteps_Table_RendersStepRows()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/executions/{id}/steps").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new
                {
                    id = Guid.NewGuid(), stepId = "s1", stepName = "Disk", stepType = "runScript",
                    targetMachine = (string?)null, status = "Succeeded",
                    startedAt = DateTime.UtcNow.AddSeconds(-3), completedAt = DateTime.UtcNow, // → duration cell
                    output = (string?)null, errorOutput = (string?)null, attemptCount = 1,
                    pausedAt = (DateTime?)null, variablesSnapshot = (string?)null, traceOutput = (string?)null,
                },
                new
                {
                    id = Guid.NewGuid(), stepId = "s2", stepName = "Wait", stepType = "delay",
                    targetMachine = (string?)null, status = "Skipped",
                    startedAt = (DateTime?)null, completedAt = (DateTime?)null, // → "-" duration branch
                    output = (string?)null, errorOutput = (string?)null, attemptCount = 0,
                    pausedAt = (DateTime?)null, variablesSnapshot = (string?)null, traceOutput = (string?)null,
                },
            }));

        var result = h.Run("exec", "steps", id.ToString(), "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Disk").And.Contain("Wait");
    }

    // ---- audit list --------------------------------------------------------

    [Fact]
    public void AuditList_Table_RendersEntriesAndNextCursorHint()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/audit").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                items = new object[]
                {
                    // long details → truncation branch; resource present; username wins over userId
                    new
                    {
                        id = Guid.NewGuid(), timestamp = DateTime.UtcNow, userId = Guid.NewGuid(), username = "bob",
                        action = "WORKFLOW_PUBLISHED", resourceType = "Workflow", resourceId = Guid.NewGuid(),
                        details = new string('x', 200), ipAddress = "10.9.8.7",
                    },
                    // no resource + no user → the "-" fallbacks
                    new
                    {
                        id = Guid.NewGuid(), timestamp = DateTime.UtcNow, userId = (Guid?)null, username = (string?)null,
                        action = "LOGIN", resourceType = (string?)null, resourceId = (Guid?)null,
                        details = (string?)null, ipAddress = (string?)null,
                    },
                },
                nextCursor = new { timestamp = DateTime.UtcNow, id = Guid.NewGuid() },
            }));

        var result = h.Run("audit", "list", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("bob");         // username column
        result.Output.Should().Contain("next cursor"); // NextCursor markup-line branch
    }

    // ---- machine list / get ------------------------------------------------

    [Fact]
    public void MachineList_Table_RendersReachabilityRows()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/machines").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new
                {
                    id = Guid.NewGuid(), name = "WEB01", hostname = "web01", winRmPort = 5986, useSsl = true,
                    defaultCredentialId = Guid.NewGuid(), tags = "prod",
                    lastConnectivityCheck = DateTime.UtcNow, isReachable = true,
                },
                new
                {
                    id = Guid.NewGuid(), name = "DB01", hostname = "db01", winRmPort = 5985, useSsl = false,
                    defaultCredentialId = (Guid?)null, tags = (string?)null,
                    lastConnectivityCheck = (DateTime?)null, isReachable = false, // → "no" + "-" branches
                },
            }));

        var result = h.Run("machine", "list", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("WEB01").And.Contain("DB01");
    }

    [Fact]
    public void MachineGet_Table_RendersDetailGrid()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/machines/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id, name = "APP01", hostname = "app01.corp", winRmPort = 5985, useSsl = false,
                defaultCredentialId = Guid.NewGuid(), tags = "prod,web",
                lastConnectivityCheck = DateTime.UtcNow, isReachable = true,
            }));

        var result = h.Run("machine", "get", id.ToString(), "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("APP01");
        result.Output.Should().Contain("app01.corp");
        result.Output.Should().Contain("prod,web"); // Tags row
    }

    // ---- credential list / get ---------------------------------------------

    [Fact]
    public void CredentialList_Table_RendersDomainOrDash()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/credentials").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new { id = Guid.NewGuid(), name = "svc-a", username = "deployer", domain = "CORP" },
                new { id = Guid.NewGuid(), name = "svc-b", username = "root", domain = (string?)null },
            }));

        var result = h.Run("credential", "list", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("svc-a").And.Contain("svc-b");
        result.Output.Should().Contain("CORP").And.Contain("deployer");
    }

    [Fact]
    public void CredentialGet_Table_RendersSingleRow()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/credentials/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id, name = "svc-solo", username = "admin", domain = "DOM",
            }));

        var result = h.Run("credential", "get", id.ToString(), "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("svc-solo").And.Contain("DOM");
    }

    // ---- globals list ------------------------------------------------------

    [Fact]
    public void GlobalsList_Table_RendersSecretFlagAndMaskedValue()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new { id = Guid.NewGuid(), name = "PlainVar", value = "hello", isSecret = false, description = "d", createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = "admin" },
                new { id = Guid.NewGuid(), name = "SecretVar", value = "***", isSecret = true, description = (string?)null, createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = "admin" },
            }));

        var result = h.Run("globals", "list", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("PlainVar").And.Contain("SecretVar");
        result.Output.Should().Contain("***"); // server-masked secret value rendered as-is
    }

    // ---- user list ---------------------------------------------------------

    [Fact]
    public void UserList_Table_RendersActiveFlagPerRow()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/users").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new { id = Guid.NewGuid(), username = "alice", role = "Admin", isActive = true, createdAt = DateTime.UtcNow },
                new { id = Guid.NewGuid(), username = "bob", role = "Viewer", isActive = false, createdAt = DateTime.UtcNow },
            }));

        var result = h.Run("user", "list", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("alice").And.Contain("bob");
        result.Output.Should().Contain("Admin").And.Contain("Viewer");
    }

    // ---- dashboard ---------------------------------------------------------

    [Fact]
    public void Dashboard_Table_RendersCountsAndTopWorkflows()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/stats/dashboard").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                workflowsTotal = 12, workflowsEnabled = 9,
                machinesTotal = 5, machinesReachable = 4,
                executionsTotal = 1340,
                last24h = new { total = 120, succeeded = 110, failed = 8, running = 1, cancelled = 1 },
                last24hBuckets = Array.Empty<object>(),
                topWorkflows = new object[] { new { id = Guid.NewGuid(), name = "Nightly", runCount = 50, successCount = 47, failCount = 3 } },
                running = Array.Empty<object>(),
                recent = Array.Empty<object>(),
                armedTriggers = Array.Empty<object>(),
            }));

        var result = h.Run("dashboard", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("9 enabled"); // Workflows grid row
        result.Output.Should().Contain("Nightly");    // Top-workflows nested table
    }

    // ---- workflow stats (StepStats) ----------------------------------------

    [Fact]
    public void WorkflowStats_Table_RendersFailRateTiers()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(
                WorkflowJson(id, "wf", enabled: true, lockOwner: null, withLastExec: false)));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/step-stats").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                check = new { totalRuns = 100, failedRuns = 50, failureRate = 0.50, avgDurationMs = 300, p95DurationMs = 800, lastDurationMs = 400 },  // red tier
                deploy = new { totalRuns = 1000, failedRuns = 2, failureRate = 0.002, avgDurationMs = 100, p95DurationMs = 200, lastDurationMs = 110 }, // grey tier
            }));

        var result = h.Run("workflow", "stats", id.ToString(), "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("check").And.Contain("deploy");
    }

    // ---- observability summary (TelemetrySummary) --------------------------

    [Fact]
    public void ObservabilitySummary_Table_RendersValueErrorAndNullPanels()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/observability/summary").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                available = true,
                panels = new object[]
                {
                    new { key = "cpu", title = "CPU", unit = "%", value = 42.5, error = (string?)null },   // value branch
                    new { key = "mem", title = "Mem", unit = "MB", value = (double?)null, error = "boom" }, // error branch
                    new { key = "disk", title = "Disk", unit = "GB", value = (double?)null, error = (string?)null }, // "-" branch
                },
            }));

        var result = h.Run("observability", "summary", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("cpu").And.Contain("mem").And.Contain("disk");
    }

    [Fact]
    public void ObservabilitySummary_Table_NotAvailable_ShowsNotConfigured()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/observability/summary").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                available = false, panels = Array.Empty<object>(),
            }));

        var result = h.Run("observability", "summary", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("not configured"); // early-return markup-line branch
    }

    // ---- maintenance list / get --------------------------------------------

    [Fact]
    public void MaintenanceList_Table_RendersWeeklyAndOneTimeWindows()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                MaintenanceJson(Guid.NewGuid(), "Patch", "Weekly", "Machine"),
                MaintenanceJson(Guid.NewGuid(), "Shot", "OneTime", "Global"),
            }));

        var result = h.Run("maintenance", "list", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Patch").And.Contain("Shot");
    }

    [Fact]
    public void MaintenanceGet_Table_RendersMatchingWindow()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                MaintenanceJson(id, "Solo", "Weekly", "Global"),
            }));

        var result = h.Run("maintenance", "get", id.ToString(), "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Solo");
    }

    [Fact]
    public void MaintenanceGet_UnknownId_ReturnsError()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var result = h.Run("maintenance", "get", Guid.NewGuid().ToString(), "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("nicht gefunden");
    }

    // ---- alerting list / get / deliveries ----------------------------------

    [Fact]
    public void AlertingList_Table_RendersRuleWithEventOverflow()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/alerting/rules").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                AlertingRuleJson(Guid.NewGuid(), "Prod"),
            }));

        var result = h.Run("alerting", "list", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Prod");
    }

    [Fact]
    public void AlertingGet_Table_RendersMatchingRule()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/alerting/rules").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                AlertingRuleJson(id, "Solo"),
            }));

        var result = h.Run("alerting", "get", id.ToString(), "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Solo");
    }

    [Fact]
    public void AlertingGet_UnknownId_ReturnsError()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/alerting/rules").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var result = h.Run("alerting", "get", Guid.NewGuid().ToString(), "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("nicht gefunden");
    }

    [Fact]
    public void AlertingDeliveries_Table_RendersAllStatusBranches()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/alerting/deliveries").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                DeliveryJson("R1", "Sent", attempt: 1, error: null, isTest: false),
                DeliveryJson("R2", "Failed", attempt: 2, error: "down", isTest: true),   // failed + [test] suffix
                DeliveryJson("R3", "Pending", attempt: 1, error: null, isTest: false),   // default (yellow) switch arm
            }));

        var result = h.Run("alerting", "deliveries", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("R1").And.Contain("R2").And.Contain("R3");
        result.Output.Should().Contain("down"); // Failed row's error cell
    }

    // ---- bonus: ExecWatcher.PollLoopAsync (reachable via --no-signalr) ------
    // The SignalR handler methods (HandleBatchItem/HandleStepStarted/HandleStepCompleted/
    // HandleExecutionStatus, ExecWatcher.cs ~117-158) are private statics over private nested
    // record types invoked only from a live hub's connection.On callbacks — not reachable in
    // a unit test. PollLoopAsync IS reachable: --no-signalr forces polling, and an
    // already-terminal execution makes the loop render once and return without any delay.

    [Fact]
    public void ExecWatch_NoSignalR_TerminalExecution_RendersDetailAndExitsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/executions/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id, workflowId = Guid.NewGuid(), status = "Succeeded",
                startedAt = DateTime.UtcNow.AddMinutes(-1), completedAt = DateTime.UtcNow,
                triggeredBy = "poller", errorMessage = (string?)null, traceId = "poll-trace",
            }));
        h.Server.Given(Request.Create().WithPath($"/api/executions/{id}/steps").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new
                {
                    id = Guid.NewGuid(), stepId = "s1", stepName = "Ping", stepType = "runScript",
                    targetMachine = (string?)null, status = "Succeeded",
                    startedAt = DateTime.UtcNow.AddSeconds(-2), completedAt = DateTime.UtcNow,
                    output = (string?)null, errorOutput = (string?)null, attemptCount = 1,
                    pausedAt = (DateTime?)null, variablesSnapshot = (string?)null, traceOutput = (string?)null,
                },
            }));

        var result = h.Run("exec", "watch", id.ToString(), "--no-signalr", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        // Final ExecutionDetail (table mode) → stdout; the per-step line goes to stderr.
        result.Output.Should().Contain("poll-trace");
        result.AnyOutput.Should().Contain("Ping"); // step surfaced by the poll loop
    }

    // ---- helpers -----------------------------------------------------------

    private static object MaintenanceJson(Guid id, string name, string recurrence, string scope) => new
    {
        id, name, description = (string?)null, isEnabled = true,
        mode = "Blackout", scopeKind = scope, recurrence,
        oneTimeStartUtc = recurrence == "OneTime" ? DateTime.UtcNow : (DateTime?)null,
        oneTimeEndUtc = recurrence == "OneTime" ? DateTime.UtcNow.AddHours(2) : (DateTime?)null,
        weeklyDaysMask = recurrence == "Weekly" ? 10 : 0,
        weeklyStartMinuteOfDay = recurrence == "Weekly" ? 540 : (int?)null,
        weeklyEndMinuteOfDay = recurrence == "Weekly" ? 1020 : (int?)null,
        cronExpression = (string?)null, durationMinutes = (int?)null, timeZoneId = "Europe/Berlin",
        targets = scope == "Global"
            ? Array.Empty<object>()
            : new object[] { new { targetKind = scope == "Folders" ? "Folder" : "Machine", targetId = Guid.NewGuid() } },
        createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = "admin",
    };

    private static object AlertingRuleJson(Guid id, string name) => new
    {
        id, name, description = (string?)null, isEnabled = true,
        eventTypes = new[] { "ExecutionFailed", "ExecutionSucceeded", "ExecutionCancelled" }, // 3 → "+1" overflow branch
        filterExpressionJson = (string?)null, scopeKind = "Global",
        cooldownMinutes = 15, minOccurrences = 1, occurrenceWindowMinutes = 0,
        routes = new object[] { new { id = Guid.NewGuid(), channel = "Email", target = "ops@x", secret = (string?)null, order = 0 } },
        targets = Array.Empty<object>(),
        createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = "admin",
    };

    private static object DeliveryJson(string rule, string status, int attempt, string? error, bool isTest) => new
    {
        id = Guid.NewGuid(), ruleId = Guid.NewGuid(), ruleName = rule,
        routeId = Guid.NewGuid(), channel = "Email", target = "ops@x",
        eventKey = "exec:abc:ExecutionFailed", status, attempt,
        createdAt = DateTime.UtcNow, sentAt = (DateTime?)null, error, isTest, summary = (string?)null,
    };
}
