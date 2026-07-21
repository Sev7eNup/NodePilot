using FluentAssertions;
using NodePilot.Cli;
using NodePilot.Cli.Tests.Infra;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// End-to-end command tests for the CLI surface added to catch up with API endpoints that
/// were introduced after the CLI's initial release (trigger / settings / contract / coverage /
/// auth methods / secrets / shared-folder / observability query / step-test). The point of
/// this file is **not** to re-test the HTTP client — that's covered by
/// NodePilotApiClientNewSurfaceTests — but to exercise the actual Spectre command parsing,
/// exit codes, and stdout shape that scripts depend on.
/// </summary>
[Collection(CommandTestCollection.Name)]
public class CommandIntegrationNewSurfaceTests
{
    // ---- auth methods (anonymous) -------------------------------------------

    [Fact]
    public void AuthMethods_RendersJson()
    {
        using var h = new CommandTestHarness(authenticated: false);
        h.Server.Given(Request.Create().WithPath("/api/auth/methods").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                local = true, ldap = false, windows = true, windowsEndpoint = "/api/auth/windows",
            }));

        var result = h.Run("auth", "methods");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("\"local\":true").And.Contain("/api/auth/windows");
    }

    // ---- workflow trigger ---------------------------------------------------

    [Fact]
    public void WorkflowTrigger_NoApiKey_FailsBeforeFiring()
    {
        using var h = new CommandTestHarness();
        // No mapping set up — if the command tries to fire, it would 404. We expect it to
        // fail BEFORE any HTTP call (validation error on stderr), so absence of a hit on
        // /api/trigger is the second assertion.
        var result = h.Run("workflow", "trigger", "Deploy");
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("API-Key");
        h.Server.LogEntries.Should().NotContain(e =>
            e.RequestMessage.AbsolutePath.StartsWith("/api/trigger/"));
    }

    [Fact]
    public void WorkflowTrigger_WithApiKey_FiresAndReturnsExecution()
    {
        using var h = new CommandTestHarness();
        var execId = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/trigger/Deploy").UsingPost()
                .WithHeader("X-Api-Key", new ExactMatcher("secret-key-x")))
            .RespondWith(Response.Create().WithStatusCode(202).WithBodyAsJson(new
            {
                id = execId, workflowId = Guid.NewGuid(), status = "Pending",
                startedAt = DateTime.UtcNow, completedAt = (DateTime?)null,
                triggeredBy = "api", errorMessage = (string?)null,
            }));

        var result = h.Run("workflow", "trigger", "Deploy", "--api-key", "secret-key-x");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain(execId.ToString());
    }

    [Fact]
    public void WorkflowTrigger_WaitWithoutSession_FailsBeforeFiring()
    {
        // Regression guard: --wait must NOT fire the workflow when there is no JWT session,
        // because polling the execution status afterwards is impossible (the polling endpoint
        // is auth-only). The mapping for /api/trigger is deliberately registered so that — if
        // the pre-check were broken — the trigger would succeed with 202 and the test would
        // (incorrectly) pass.
        using var h = new CommandTestHarness(authenticated: false);
        h.Server.Given(Request.Create().WithPath("/api/trigger/Deploy").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202).WithBodyAsJson(new
            {
                id = Guid.NewGuid(), workflowId = Guid.NewGuid(), status = "Pending",
                startedAt = DateTime.UtcNow, completedAt = (DateTime?)null,
                triggeredBy = "api", errorMessage = (string?)null,
            }));

        var result = h.Run("workflow", "trigger", "Deploy", "--api-key", "k", "--wait");
        result.ExitCode.Should().Be(ExitCodes.AuthRequired);
        // The fire-before-pre-check bug would have left the trigger endpoint hit once;
        // we assert it was NOT.
        h.Server.LogEntries.Should().NotContain(e =>
            e.RequestMessage.AbsolutePath == "/api/trigger/Deploy");
    }

    [Fact]
    public void WorkflowTrigger_IdempotentReplay_SurfacesReplayedHeader()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/trigger/Deploy").UsingPost()
                .WithHeader("Idempotency-Key", new ExactMatcher("dedupe-1")))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Idempotent-Replayed", "true")
                .WithBodyAsJson(new
                {
                    id = Guid.NewGuid(), workflowId = Guid.NewGuid(), status = "Succeeded",
                    startedAt = DateTime.UtcNow, completedAt = DateTime.UtcNow,
                    triggeredBy = "api", errorMessage = (string?)null,
                }));

        var result = h.Run("workflow", "trigger", "Deploy", "--api-key", "k", "--idempotency-key", "dedupe-1");
        result.ExitCode.Should().Be(ExitCodes.Success);
        // OutputWriter.Info routes the replay message to stderr so machine-readable stdout
        // stays clean for `np ... -o json | jq` — assertion targets stderr accordingly.
        result.StdErr.Should().Contain("Idempotent-Replayed");
    }

    // ---- workflow contract / coverage --------------------------------------

    [Fact]
    public void WorkflowContract_ByGuid_RendersInputsAndOutputs()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/contract").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                workflowId = id, workflowName = "Deploy",
                hasManualTrigger = true, hasReturnData = true, hasMultipleReturnDataNodes = false,
                inputs = new object[] { new { name = "env", type = "string", required = true, @default = "prod", description = (string?)null, hasConflict = false } },
                outputs = new object[] { new { name = "__executionId", source = "system" } },
            }));

        var result = h.Run("workflow", "contract", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("\"name\":\"env\"");
    }

    [Fact]
    public void WorkflowContract_ByName_HitsByNameEndpoint()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/workflows/by-name/Daily/contract").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                workflowId = Guid.NewGuid(), workflowName = "Daily",
                hasManualTrigger = false, hasReturnData = false, hasMultipleReturnDataNodes = false,
                inputs = Array.Empty<object>(), outputs = Array.Empty<object>(),
            }));

        var result = h.Run("workflow", "contract", "Daily");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Daily");
    }

    [Fact]
    public void WorkflowCoverage_PassesWindowDays()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        // The CLI resolves the workflow by Guid via GET /api/workflows/{id} first
        // (WorkflowResolver) — wire that response so the resolve step succeeds.
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleWorkflow(id)));
        h.Server.Given(Request.Create().WithPath($"/api/workflows/{id}/coverage").WithParam("windowDays", "60").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                workflowId = id, windowDays = 60, totalExecutions = 2,
                oldestExecutionInWindow = DateTime.UtcNow.AddDays(-10),
                nodes = Array.Empty<object>(),
            }));

        var result = h.Run("workflow", "coverage", id.ToString(), "--window-days", "60");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("\"windowDays\":60");
    }

    // ---- secrets reencrypt --------------------------------------------------

    [Fact]
    public void SecretsReencrypt_Clean_ReturnsSuccess()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/secrets/reencrypt").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                credentialsRewritten = 3, credentialsSkipped = 0, credentialSkipDetails = Array.Empty<object>(),
                globalSecretsRewritten = 1, globalSecretsSkipped = 0, globalSecretSkipDetails = Array.Empty<object>(),
                partialSuccess = false,
            }));

        var result = h.Run("secrets", "reencrypt", "--yes");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void SecretsReencrypt_Partial_ReturnsErrorCode()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/secrets/reencrypt").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(207).WithBodyAsJson(new
            {
                credentialsRewritten = 2, credentialsSkipped = 1,
                credentialSkipDetails = new[] { new { id = Guid.NewGuid(), name = "x", reason = "CryptographicException" } },
                globalSecretsRewritten = 0, globalSecretsSkipped = 0, globalSecretSkipDetails = Array.Empty<object>(),
                partialSuccess = true,
            }));

        var result = h.Run("secrets", "reencrypt", "--yes");
        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    // ---- settings -----------------------------------------------------------

    [Fact]
    public void SettingsStatus_RendersGrid()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/admin/settings/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                overridesPath = "/srv/x",
                restartRequired = false,
                restartRequiredSince = (DateTimeOffset?)null,
                restartRequiredFor = Array.Empty<string>(),
                lastSavedAt = (DateTimeOffset?)null,
                lastSavedBy = (string?)null,
            }));

        var result = h.Run("settings", "status");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("/srv/x");
    }

    [Fact]
    public void SettingsGet_EtagOnly_PrintsBareEtag()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/admin/settings/Smtp").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("ETag", "\"abc-1\"")
                .WithBodyAsJson(new
                {
                    sectionPath = "Smtp", payload = new { host = "mail" }, etag = "\"abc-1\"",
                    isHotReloadable = true, effectiveSource = new { },
                }));

        var result = h.Run("settings", "get", "Smtp", "--etag-only");
        result.ExitCode.Should().Be(ExitCodes.Success);
        // The point of --etag-only: nothing but the ETag on stdout, so shell-capture is clean.
        result.Output.Trim().Should().Be("\"abc-1\"");
    }

    [Fact]
    public void SettingsPut_RequiresEtag()
    {
        using var h = new CommandTestHarness();
        var file = Path.Combine(Path.GetTempPath(), "smtp-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, """{"host":"mail","port":25,"from":"a@b.c"}""");
        try
        {
            // No --etag → command must fail without sending the request.
            var result = h.Run("settings", "put", "Smtp", "--file", file);
            result.ExitCode.Should().Be(ExitCodes.Error);
            result.StdErr.Should().Contain("--etag");
            h.Server.LogEntries.Should().NotContain(e =>
                e.RequestMessage.AbsolutePath == "/api/admin/settings/Smtp"
                && e.RequestMessage.Method == "PUT");
        }
        finally { try { File.Delete(file); } catch { } }
    }

    // ---- shared-folder ------------------------------------------------------

    [Fact]
    public void SharedFolderList_RendersJson()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/shared-workflow-folders").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new
                {
                    id = Guid.NewGuid(), parentFolderId = (Guid?)null, name = "Root", path = "/",
                    depth = 0, createdAt = DateTime.UtcNow, createdByUserId = (Guid?)null,
                    workflowCount = 0,
                    capabilities = new { canRead = true, canRun = true, canEdit = true, canAdmin = true },
                },
            }));

        var result = h.Run("shared-folder", "list");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("\"path\":\"/\"");
    }

    [Fact]
    public void WorkflowMoveFolder_RequiresTargetFolder()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("workflow", "move-folder", Guid.NewGuid().ToString());
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("--target-folder");
    }

    // ---- observability query ------------------------------------------------

    [Fact]
    public void ObservabilityQuery_JsonOutput_PrintsCompactJson()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/observability/query").WithParam("query", "up").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                status = "success",
                data = new { resultType = "vector", result = Array.Empty<object>() },
            }));

        var result = h.Run("observability", "query", "--query", "up");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("\"resultType\":\"vector\"");
    }

    [Fact]
    public void ObservabilityQuery_YamlOutput_PrintsYaml()
    {
        // Regression guard: -o yaml must not silently turn into JSON. Asserting on
        // `resultType: ...` (YAML key-value form) instead of `"resultType":` (JSON form).
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/observability/query").WithParam("query", "up").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                status = "success",
                data = new { resultType = "vector", result = Array.Empty<object>() },
            }));

        var result = h.Run("observability", "query", "--query", "up", "-o", "yaml");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("resultType: vector");
        result.Output.Should().NotContain("\"resultType\":");
    }

    [Fact]
    public void SettingsGet_YamlOutput_PrintsYaml()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/admin/settings/Smtp").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                sectionPath = "Smtp", payload = new { host = "mail" }, etag = "\"abc-1\"",
                isHotReloadable = true, effectiveSource = new { },
            }));

        var result = h.Run("settings", "get", "Smtp", "-o", "yaml");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("sectionPath: Smtp");
        result.Output.Should().NotContain("\"sectionPath\":");
    }

    // ---- helpers ------------------------------------------------------------

    private static string SampleWorkflow(Guid id) => $$"""
    {
      "id": "{{id}}",
      "name": "wf",
      "description": null,
      "definitionJson": "{}",
      "version": 1,
      "isEnabled": true,
      "createdAt": "2026-01-01T00:00:00Z",
      "updatedAt": "2026-01-01T00:00:00Z",
      "createdBy": null,
      "updatedBy": null,
      "activityCount": 0,
      "triggerTypes": [],
      "lastExecution": null,
      "successCount": 0,
      "totalCount": 0,
      "avgDurationMs": null,
      "checkedOutByUserId": null,
      "checkedOutByUserName": null,
      "checkedOutAt": null
    }
    """;

    // ---- np db info / np db query ------------------------------------------

    [Fact]
    public void DbInfo_RendersProviderAndFlags()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/dbadmin/info").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                provider = "postgres",
                allowWriteQueries = false,
                queryTimeoutSeconds = 30,
                queryMaxRows = 10_000,
            }));

        var result = h.Run("db", "info");
        result.ExitCode.Should().Be(ExitCodes.Success);
        // Default output is JSON in the harness — assert against that shape.
        result.Output.Should().Contain("\"provider\":\"postgres\"")
                      .And.Contain("\"queryMaxRows\":10000");
    }

    [Fact]
    public void DbQuery_ReadMode_ReturnsRowsAndExitsZero()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/dbadmin/query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                columns = new[] { new { name = "Username", type = "string" } },
                rows = new[] { new object[] { "alice" }, new object[] { "bob" } },
                rowsAffected = (int?)null,
                durationMs = 4,
                truncated = false,
                mode = "read",
            }));

        var result = h.Run("db", "query", "--sql", "SELECT Username FROM Users");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("alice").And.Contain("bob").And.Contain("\"mode\":\"read\"");
    }

    [Fact]
    public void DbQuery_RequiresSqlOrFile()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("db", "query");
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("Pflicht");
    }

    [Fact]
    public void DbQuery_WriteMode_WithYes_SendsConfirmHeader()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/dbadmin/query").UsingPost()
                .WithHeader("X-Confirm-Write", new ExactMatcher("ALLOW")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                columns = Array.Empty<object>(),
                rows = Array.Empty<object[]>(),
                rowsAffected = 3,
                durationMs = 7,
                truncated = false,
                mode = "write",
            }));

        var result = h.Run("db", "query", "--sql", "UPDATE Users SET IsActive = 0", "--write", "--yes");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("\"rowsAffected\":3").And.Contain("\"mode\":\"write\"");
    }
}
