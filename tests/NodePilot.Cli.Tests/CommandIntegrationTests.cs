using FluentAssertions;
using NodePilot.Cli;
using NodePilot.Cli.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// Each public command runs end-to-end against a WireMock server with a primed DPAPI
/// session. We assert exit codes (the script-facing contract) plus that the command
/// actually hit the right endpoint. Commands are thin wrappers — but if any of them
/// silently regresses to the wrong path/verb, these tests catch it.
/// </summary>
[Collection(CommandTestCollection.Name)]
public class CommandIntegrationTests
{
    [Fact]
    public void ProductionDi_HttpLoopbackRequiresExplicitAllowInsecureFlag()
    {
        using var h = new CommandTestHarness(autoAllowInsecure: false);

        var result = h.Run("health");

        result.ExitCode.Should().Be(ExitCodes.Error);
        h.Server.LogEntries.Should().BeEmpty(
            "the HTTPS guard must reject the endpoint before any bearer-bearing request is sent");
    }

    // ---- Auth --------------------------------------------------------------

    [Fact]
    public void AuthLogin_Success_StoresTokenAndReturnsZero()
    {
        using var h = new CommandTestHarness(authenticated: false);
        h.Server.Given(Request.Create().WithPath("/api/auth/login").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                token = "fresh", userId = Guid.NewGuid(), username = "admin", role = "Admin",
            }));

        var result = h.Run("auth", "login", "--username", "admin", "--password", "pw12345678");
        result.ExitCode.Should().Be(ExitCodes.Success);
        h.Tokens.Load("default")!.Token.Should().Be("fresh");
    }

    [Fact]
    public void AuthLogin_BadCredentials_ReturnsAuthRequired()
    {
        using var h = new CommandTestHarness(authenticated: false);
        h.Server.Given(Request.Create().WithPath("/api/auth/login").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));

        var result = h.Run("auth", "login", "--username", "admin", "--password", "wrong");
        result.ExitCode.Should().Be(ExitCodes.AuthRequired);
    }

    [Fact]
    public void AuthLogout_RevokesAndDeletesLocal()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/auth/logout").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("auth", "logout");
        result.ExitCode.Should().Be(ExitCodes.Success);
        h.Tokens.Load("default").Should().BeNull();
    }

    [Fact]
    public void AuthLogout_ServerUnreachable_StillWipesLocalSession()
    {
        using var h = new CommandTestHarness();
        // No mapping for /api/auth/logout → WireMock returns 404, command must still
        // delete the local session so the user isn't stuck with a bad token.
        var result = h.Run("auth", "logout");
        result.ExitCode.Should().Be(ExitCodes.Success);
        h.Tokens.Load("default").Should().BeNull();
    }

    [Fact]
    public void AuthWhoami_Authenticated_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/auth/me").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = Guid.NewGuid(), username = "tester", role = "Admin",
            }));

        var result = h.Run("auth", "whoami");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("tester");
    }

    [Fact]
    public void AuthWhoami_NoSession_ReturnsAuthRequired()
    {
        using var h = new CommandTestHarness(authenticated: false);
        var result = h.Run("auth", "whoami");
        result.ExitCode.Should().Be(ExitCodes.AuthRequired);
    }

    // ---- Workflow ----------------------------------------------------------

    [Fact]
    public void WorkflowList_RendersJson()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/workflows").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleList));

        var result = h.Run("workflow", "list");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Build");
    }

    [Fact]
    public void WorkflowGet_ByGuid_Resolves()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));

        var result = h.Run("workflow", "get", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Build");
    }

    [Fact]
    public void WorkflowLock_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/lock").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));

        var result = h.Run("workflow", "lock", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowLock_WhenLockedByOther_ReturnsErrorWithHint()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/lock").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(423).WithBodyAsJson(new
            {
                title = "Locked", detail = "checked out by alice",
            }));

        var result = h.Run("workflow", "lock", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void WorkflowUnlock_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/unlock").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));

        var result = h.Run("workflow", "unlock", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowEnable_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/enable").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("workflow", "enable", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowDisable_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/disable").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("workflow", "disable", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowCancelAll_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/cancel-all").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { total = 2, signalled = 2 }));

        var result = h.Run("workflow", "cancel-all", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowDuplicate_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/duplicate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBody(Single(copyId, "Build (Copy)")));

        var result = h.Run("workflow", "duplicate", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowDelete_NonInteractive_DeletesWithoutPrompt()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        // CommandAppTester runs without an attached console — Console.IsInputRedirected
        // is true, which the delete command uses to skip the interactive confirm.
        var result = h.Run("workflow", "delete", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowVersions_RendersList()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/versions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { version = 2, name = "v2", createdAt = DateTime.UtcNow,
                      createdBy = "admin", changeNote = (string?)null, isCurrent = true },
            }));

        var result = h.Run("workflow", "versions", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowRollback_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/rollback/3").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));

        var result = h.Run("workflow", "rollback", id.ToString(), "3", "--reason", "revert");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowPublish_RequiresFile_AndPosts()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var defPath = Path.Combine(h.ConfigDir, "wf.json");
        File.WriteAllText(defPath, "{\"nodes\":[],\"edges\":[]}");

        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/publish").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));

        var result = h.Run("workflow", "publish", id.ToString(), "--file", defPath);
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowPublish_FileMissing_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));

        var result = h.Run("workflow", "publish", id.ToString(), "--file", Path.Combine(h.ConfigDir, "missing.json"));
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void WorkflowPublish_FileInvalidJson_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var defPath = Path.Combine(h.ConfigDir, "broken.json");
        File.WriteAllText(defPath, "{this is not json");

        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));

        var result = h.Run("workflow", "publish", id.ToString(), "--file", defPath);
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void WorkflowRun_NoWait_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var execId = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/execute").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202).WithBodyAsJson(new
            {
                id = execId, workflowId = id, status = "Pending",
                startedAt = DateTime.UtcNow, completedAt = (DateTime?)null,
                triggeredBy = "tester", errorMessage = (string?)null,
            }));

        var result = h.Run("workflow", "run", id.ToString(), "--params", "env=stg");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowRun_BadParams_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));

        var result = h.Run("workflow", "run", id.ToString(), "--params", "noequals");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void WorkflowExport_OneToFile_WritesFile()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        var outPath = Path.Combine(h.ConfigDir, "exported.json");
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/export").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Envelope(single: true)));

        var result = h.Run("workflow", "export", id.ToString(), "--out", outPath);
        result.ExitCode.Should().Be(ExitCodes.Success);
        File.Exists(outPath).Should().BeTrue();
    }

    [Fact]
    public void WorkflowExport_AllToStdout_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/workflows/export").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Envelope(single: false)));

        var result = h.Run("workflow", "export", "--all");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowExport_NoTargetAndNoAll_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("workflow", "export");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void WorkflowImport_FromFile_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var inPath = Path.Combine(h.ConfigDir, "import.json");
        File.WriteAllText(inPath, Envelope(single: true));
        h.Server.Given(Request.Create().WithPath("/api/workflows/import").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                created = 1,
                workflows = new[] { new { id = Guid.NewGuid(), name = "Build", originalName = (string?)null } },
                errors = Array.Empty<string>(),
            }));

        var result = h.Run("workflow", "import", "--file", inPath);
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void WorkflowImport_FileMissing_ReturnsError()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("workflow", "import", "--file", "nonexistent.json");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    // ---- Exec --------------------------------------------------------------

    [Fact]
    public void ExecList_NoFilter_ReturnsRows()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/executions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var result = h.Run("exec", "list");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void ExecList_WithWorkflowFilter_ResolvesAndCalls()
    {
        using var h = new CommandTestHarness();
        var workflowId = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{workflowId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(workflowId, "Build")));
        h.Server.Given(Request.Create().WithPath("/api/executions").UsingGet()
                .WithParam("workflowId", workflowId.ToString()))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var result = h.Run("exec", "list", "--workflow", workflowId.ToString(), "--limit", "5");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void ExecGet_RendersExecution()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/executions/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id, workflowId = Guid.NewGuid(), status = "Succeeded",
                startedAt = DateTime.UtcNow, completedAt = DateTime.UtcNow,
                triggeredBy = "tester", errorMessage = (string?)null,
            }));

        var result = h.Run("exec", "get", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void ExecSteps_RendersSteps()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/executions/{id}/steps").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var result = h.Run("exec", "steps", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void ExecCancel_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/executions/{id}/cancel").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("exec", "cancel", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void ExecWatch_NoSignalR_PollsToCompletion()
    {
        using var h = new CommandTestHarness();
        var execId = Guid.NewGuid();
        // First poll → Running, second → Succeeded.
        var scenario = "watch-" + execId;
        h.Server.Given(Request.Create().WithPath($"/api/executions/{execId}").UsingGet())
            .InScenario(scenario).WillSetStateTo("step-1")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = execId, workflowId = Guid.NewGuid(), status = "Running",
                startedAt = DateTime.UtcNow, completedAt = (DateTime?)null,
                triggeredBy = "tester", errorMessage = (string?)null,
            }));
        h.Server.Given(Request.Create().WithPath($"/api/executions/{execId}").UsingGet())
            .InScenario(scenario).WhenStateIs("step-1")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = execId, workflowId = Guid.NewGuid(), status = "Succeeded",
                startedAt = DateTime.UtcNow, completedAt = DateTime.UtcNow,
                triggeredBy = "tester", errorMessage = (string?)null,
            }));
        h.Server.Given(Request.Create().WithPath($"/api/executions/{execId}/steps").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var result = h.Run("exec", "watch", execId.ToString(), "--no-signalr");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void ExecRetry_ReturnsNewExecutionAndZero()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/executions/{id}/retry").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202).WithBodyAsJson(new
            {
                id = Guid.NewGuid(), workflowId = Guid.NewGuid(), status = "Pending",
                startedAt = DateTime.UtcNow, completedAt = (DateTime?)null,
                triggeredBy = (string?)null, errorMessage = (string?)null,
            }));

        var result = h.Run("exec", "retry", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    // ---- Audit / System / Config -------------------------------------------

    [Fact]
    public void AuditList_AdminOnly_PassesFiltersThrough()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/audit").UsingGet()
                .WithParam("action", "WORKFLOW_PUBLISHED")
                .WithParam("take", "5"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { items = Array.Empty<object>(), nextCursor = (object?)null }));

        var result = h.Run("audit", "list", "--action", "WORKFLOW_PUBLISHED", "--limit", "5");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void AuditList_Forbidden_ReturnsPermissionDenied()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/audit").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403));

        var result = h.Run("audit", "list");
        result.ExitCode.Should().Be(ExitCodes.PermissionDenied);
    }

    [Fact]
    public void Health_BothOk_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/healthz/live").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        h.Server.Given(Request.Create().WithPath("/healthz/ready").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var result = h.Run("health");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void Health_ReadyDown_ReturnsError()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/healthz/live").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        h.Server.Given(Request.Create().WithPath("/healthz/ready").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(503).WithBody("db unreachable"));

        var result = h.Run("health");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void Health_PassiveFollower_ShowsLeaderStatusButReturnsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/healthz/live").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        h.Server.Given(Request.Create().WithPath("/healthz/ready").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        h.Server.Given(Request.Create().WithPath("/healthz/leader").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(503)
                .WithBodyAsJson(new { status = "follower", nodeId = "NODE-B", reason = "not_leader" }));

        var result = h.Run("health");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("follower");
    }

    [Fact]
    public void CronNext_ReturnsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/triggers/schedule/next-fires").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                fires = new[] { DateTime.UtcNow },
                summary = "every hour",
            }));

        var result = h.Run("cron", "next", "0 0 * * * ?", "--count", "1");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void ConfigGet_RendersProfiles()
    {
        using var h = new CommandTestHarness(authenticated: false);
        var result = h.Run("config", "get");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("default");
    }

    [Fact]
    public void ConfigSet_Server_PersistsValue()
    {
        using var h = new CommandTestHarness(authenticated: false);
        var result = h.Run("config", "set", "server", "https://np.new");
        result.ExitCode.Should().Be(ExitCodes.Success);
        h.Config.Load().Profiles["default"].Server.Should().Be("https://np.new");
    }

    [Fact]
    public void ConfigSet_DefaultProfile_PersistsValue()
    {
        using var h = new CommandTestHarness(authenticated: false);
        var result = h.Run("config", "set", "default-profile", "prod");
        result.ExitCode.Should().Be(ExitCodes.Success);
        h.Config.Load().DefaultProfile.Should().Be("prod");
    }

    [Fact]
    public void ConfigSet_UnknownKey_ReturnsError()
    {
        using var h = new CommandTestHarness(authenticated: false);
        var result = h.Run("config", "set", "made-up-key", "x");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    // ---- Sample bodies -----------------------------------------------------

    private static string Single(Guid id, string name) => $$"""
    { "id": "{{id}}", "name": "{{name}}", "description": null,
      "definitionJson": "{}", "version": 1, "isEnabled": true,
      "createdAt": "2026-04-01T00:00:00Z", "updatedAt": "2026-04-01T00:00:00Z",
      "createdBy": null, "updatedBy": null, "activityCount": 1, "triggerTypes": [],
      "lastExecution": null, "successCount": 0, "totalCount": 0, "avgDurationMs": null,
      "checkedOutByUserId": null, "checkedOutByUserName": null, "checkedOutAt": null }
    """;

    private static string SampleList => $"[{Single(Guid.NewGuid(), "Build")},{Single(Guid.NewGuid(), "Report")}]";

    private static string Envelope(bool single)
    {
        var item = """{ "name": "Build", "description": null, "definition": { "nodes": [], "edges": [] }, "isEnabled": true }""";
        return single
            ? $$"""
              { "schema": "nodepilot-workflow-export/v1", "exportVersion": 1,
                "exportedAt": "2026-04-01T00:00:00Z", "workflow": {{item}}, "workflows": null }
              """
            : $$"""
              { "schema": "nodepilot-workflow-export/v1", "exportVersion": 1,
                "exportedAt": "2026-04-01T00:00:00Z", "workflow": null, "workflows": [{{item}}] }
              """;
    }
}
