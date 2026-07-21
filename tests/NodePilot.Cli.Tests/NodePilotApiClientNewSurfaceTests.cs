using System.Net;
using System.Text.Json;
using FluentAssertions;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// API-client tests for the CLI surface added to catch up with backend endpoints introduced
/// after the CLI's initial release: external trigger, admin settings, secrets reencrypt,
/// shared folders + RBAC, observability raw queries, workflow contract/coverage, auth
/// methods, step-test.
/// </summary>
public sealed class NodePilotApiClientNewSurfaceTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly NodePilotApiClient _client;

    public NodePilotApiClientNewSurfaceTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url + "/") };
        _client = new NodePilotApiClient(http) { BearerToken = "test-token" };
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    // ---- Auth methods -------------------------------------------------------

    [Fact]
    public async Task GetAuthMethodsAsync_ReturnsTiles()
    {
        _server.Given(Request.Create().WithPath("/api/auth/methods").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   local = true, ldap = true, windows = false, windowsEndpoint = (string?)null,
               }));
        var methods = await _client.GetAuthMethodsAsync(CancellationToken.None);
        methods.Local.Should().BeTrue();
        methods.Ldap.Should().BeTrue();
        methods.Windows.Should().BeFalse();
    }

    // ---- Workflow contract / coverage --------------------------------------

    [Fact]
    public async Task GetContractAsync_ReturnsInputsAndOutputs()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/contract").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   workflowId = id, workflowName = "Deploy",
                   hasManualTrigger = true, hasReturnData = true, hasMultipleReturnDataNodes = false,
                   inputs = new object[] { new { name = "env", type = "string", required = true, @default = "prod", description = (string?)null, hasConflict = false } },
                   outputs = new object[] { new { name = "__executionId", source = "system" }, new { name = "version", source = "single" } },
               }));
        var c = await _client.GetContractAsync(id, CancellationToken.None);
        c.WorkflowName.Should().Be("Deploy");
        c.Inputs.Should().HaveCount(1);
        c.Outputs.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetContractByNameAsync_HitsByNameRoute()
    {
        _server.Given(Request.Create().WithPath("/api/workflows/by-name/DailyReport/contract").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   workflowId = Guid.NewGuid(), workflowName = "DailyReport",
                   hasManualTrigger = false, hasReturnData = false, hasMultipleReturnDataNodes = false,
                   inputs = Array.Empty<object>(), outputs = Array.Empty<object>(),
               }));
        var c = await _client.GetContractByNameAsync("DailyReport", CancellationToken.None);
        c.WorkflowName.Should().Be("DailyReport");
    }

    [Fact]
    public async Task GetCoverageAsync_PassesWindowDays()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/coverage").WithParam("windowDays", "60").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   workflowId = id, windowDays = 60, totalExecutions = 3,
                   oldestExecutionInWindow = DateTime.UtcNow.AddDays(-50),
                   nodes = new object[] { new { stepId = "s1", executedCount = 3, failedCount = 0, skippedCount = 0, lastExecutedAt = (DateTime?)null, lastSucceededAt = (DateTime?)null, lastFailedAt = (DateTime?)null } },
               }));
        var c = await _client.GetCoverageAsync(id, 60, CancellationToken.None);
        c.WindowDays.Should().Be(60);
        c.Nodes.Should().ContainSingle();
    }

    // ---- External Trigger ---------------------------------------------------

    [Fact]
    public async Task TriggerExternalAsync_SendsApiKeyHeader_AndParsesExecution()
    {
        var execId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/trigger/Deploy").UsingPost()
                .WithHeader("X-Api-Key", "secret-key-thirty-two-bytes-long!!"))
               .RespondWith(Response.Create().WithStatusCode(202).WithBodyAsJson(new
               {
                   id = execId, workflowId = Guid.NewGuid(), status = "Pending",
                   startedAt = DateTime.UtcNow, completedAt = (DateTime?)null,
                   triggeredBy = "api", errorMessage = (string?)null,
               }));
        var (execution, replayed) = await _client.TriggerExternalAsync(
            "Deploy", "secret-key-thirty-two-bytes-long!!", null, null, null, CancellationToken.None);
        execution.Id.Should().Be(execId);
        replayed.Should().BeFalse();
    }

    [Fact]
    public async Task TriggerExternalAsync_SetsIdempotencyHeader_AndReadsReplayedFlag()
    {
        _server.Given(Request.Create().WithPath("/api/trigger/Deploy").UsingPost()
                .WithHeader("X-Api-Key", "key")
                .WithHeader("Idempotency-Key", "abc-123"))
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Idempotent-Replayed", "true")
                   .WithBodyAsJson(new
                   {
                       id = Guid.NewGuid(), workflowId = Guid.NewGuid(), status = "Succeeded",
                       startedAt = DateTime.UtcNow.AddMinutes(-2), completedAt = DateTime.UtcNow.AddMinutes(-1),
                       triggeredBy = "api", errorMessage = (string?)null,
                   }));
        var (_, replayed) = await _client.TriggerExternalAsync(
            "Deploy", "key", null, null, "abc-123", CancellationToken.None);
        replayed.Should().BeTrue();
    }

    [Fact]
    public async Task TriggerExternalAsync_Unauthorized_Throws()
    {
        _server.Given(Request.Create().WithPath("/api/trigger/Deploy").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(401));
        var ex = await Assert.ThrowsAsync<ApiException>(() => _client.TriggerExternalAsync(
            "Deploy", "wrong", null, null, null, CancellationToken.None));
        ex.IsUnauthorized.Should().BeTrue();
    }

    // ---- Settings ------------------------------------------------------------

    [Fact]
    public async Task GetSettingsStatusAsync_Parses()
    {
        _server.Given(Request.Create().WithPath("/api/admin/settings/status").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   overridesPath = "/srv/nodepilot/appsettings.runtime.json",
                   restartRequired = false,
                   restartRequiredSince = (DateTimeOffset?)null,
                   restartRequiredFor = Array.Empty<string>(),
                   lastSavedAt = (DateTimeOffset?)null,
                   lastSavedBy = (string?)null,
               }));
        var status = await _client.GetSettingsStatusAsync(CancellationToken.None);
        status.OverridesPath.Should().Be("/srv/nodepilot/appsettings.runtime.json");
        status.RestartRequired.Should().BeFalse();
    }

    [Fact]
    public async Task GetSystemInfoAsync_Parses()
    {
        _server.Given(Request.Create().WithPath("/api/admin/settings/system-info").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   appVersion = "1.2.3", overridesPath = "/x", databaseProvider = "postgres",
                   databaseHost = "db", secretsProvider = "AesGcm",
                   clusterEnabled = true, clusterNodeId = "node-a", clusterIsLeader = true,
                   jwtIssuer = "NodePilot", jwtAudience = "NodePilot",
               }));
        var info = await _client.GetSystemInfoAsync(CancellationToken.None);
        info.AppVersion.Should().Be("1.2.3");
        info.ClusterIsLeader.Should().BeTrue();
    }

    [Fact]
    public async Task GetSettingsSectionAsync_ReturnsEtagFromHeader()
    {
        _server.Given(Request.Create().WithPath("/api/admin/settings/Smtp").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("ETag", "\"abc-1\"")
                   .WithBodyAsJson(new
                   {
                       sectionPath = "Smtp", payload = new { host = "mail" }, etag = "\"abc-1\"",
                       isHotReloadable = true, effectiveSource = new { },
                   }));
        var (body, etag) = await _client.GetSettingsSectionAsync("Smtp", CancellationToken.None);
        using (body)
        {
            etag.Should().Be("\"abc-1\"");
            body.RootElement.GetProperty("sectionPath").GetString().Should().Be("Smtp");
        }
    }

    [Fact]
    public async Task PutSettingsSectionAsync_SendsIfMatch()
    {
        _server.Given(Request.Create().WithPath("/api/admin/settings/Smtp").UsingPut()
                .WithHeader("If-Match", "\"abc-1\""))
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new
                   {
                       sectionPath = "Smtp", payload = new { host = "mail2" }, etag = "\"abc-2\"",
                       isHotReloadable = true, effectiveSource = new { },
                   }));
        using var payload = JsonDocument.Parse("""{"host":"mail2","port":25,"from":"a@b"}""");
        using var saved = await _client.PutSettingsSectionAsync("Smtp", "\"abc-1\"", payload.RootElement, CancellationToken.None);
        saved.RootElement.GetProperty("etag").GetString().Should().Be("\"abc-2\"");
    }

    [Fact]
    public async Task PutSettingsSectionAsync_EtagMismatch_Throws()
    {
        _server.Given(Request.Create().WithPath("/api/admin/settings/Smtp").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(412)
                   .WithBodyAsJson(new { code = "ETAG_MISMATCH", message = "stale" }));
        using var payload = JsonDocument.Parse("""{"host":"x"}""");
        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            _client.PutSettingsSectionAsync("Smtp", "\"old\"", payload.RootElement, CancellationToken.None));
        ((int)ex.StatusCode).Should().Be(412);
    }

    [Fact]
    public async Task TestSmtpAsync_ForwardsBody()
    {
        _server.Given(Request.Create().WithPath("/api/admin/settings/test/smtp").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   ok = true, message = "smtp ok", durationMs = 42.0, errorKind = (string?)null,
               }));
        using var doc = JsonDocument.Parse("""{"host":"mail","port":25,"from":"a@b"}""");
        var req = new SmtpTestProbeRequest(doc.RootElement, "ops@x");
        var result = await _client.TestSmtpAsync(req, CancellationToken.None);
        result.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task TestLlmAsync_ForwardsBody()
    {
        _server.Given(Request.Create().WithPath("/api/admin/settings/test/llm").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   ok = false, message = "401", durationMs = 12.0, errorKind = "Unauthorized",
               }));
        using var doc = JsonDocument.Parse("""{"enabled":true,"baseUrl":"http://x","model":"m","maxTokens":4096,"timeoutSeconds":30}""");
        var result = await _client.TestLlmAsync(new LlmTestProbeRequest(doc.RootElement), CancellationToken.None);
        result.Ok.Should().BeFalse();
        result.ErrorKind.Should().Be("Unauthorized");
    }

    // ---- Secrets reencrypt --------------------------------------------------

    [Fact]
    public async Task ReencryptSecretsAsync_AcceptsTwoOhSeven()
    {
        _server.Given(Request.Create().WithPath("/api/secrets/reencrypt").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(207).WithBodyAsJson(new
               {
                   credentialsRewritten = 5,
                   credentialsSkipped = 1,
                   credentialSkipDetails = new[] { new { id = Guid.NewGuid(), name = "db", reason = "CryptographicException" } },
                   globalSecretsRewritten = 0,
                   globalSecretsSkipped = 0,
                   globalSecretSkipDetails = Array.Empty<object>(),
                   partialSuccess = true,
               }));
        var result = await _client.ReencryptSecretsAsync(CancellationToken.None);
        result.PartialSuccess.Should().BeTrue();
        result.CredentialsRewritten.Should().Be(5);
        result.CredentialSkipDetails.Should().ContainSingle();
    }

    [Fact]
    public async Task ReencryptSecretsAsync_TwoHundred()
    {
        _server.Given(Request.Create().WithPath("/api/secrets/reencrypt").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   credentialsRewritten = 3, credentialsSkipped = 0, credentialSkipDetails = Array.Empty<object>(),
                   globalSecretsRewritten = 1, globalSecretsSkipped = 0, globalSecretSkipDetails = Array.Empty<object>(),
                   partialSuccess = false,
               }));
        var result = await _client.ReencryptSecretsAsync(CancellationToken.None);
        result.PartialSuccess.Should().BeFalse();
    }

    // ---- Shared folders -----------------------------------------------------

    [Fact]
    public async Task ListSharedFoldersAsync_Parses()
    {
        _server.Given(Request.Create().WithPath("/api/shared-workflow-folders").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
               {
                   new
                   {
                       id = Guid.NewGuid(), parentFolderId = (Guid?)null, name = "Root", path = "/",
                       depth = 0, createdAt = DateTime.UtcNow, createdByUserId = (Guid?)null,
                       workflowCount = 12,
                       capabilities = new { canRead = true, canRun = true, canEdit = true, canAdmin = true },
                   },
               }));
        var rows = await _client.ListSharedFoldersAsync(CancellationToken.None);
        rows.Should().ContainSingle();
        rows[0].Capabilities.CanAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task GrantSharedFolderPermissionAsync_Posts()
    {
        var folderId = Guid.NewGuid();
        var permId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/shared-workflow-folders/{folderId}/permissions").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   id = permId, folderId, principalType = "User", principalKey = userId.ToString("D"),
                   principalDisplayName = "alice", role = "FolderOperator",
                   grantedAt = DateTime.UtcNow, grantedByUserId = (Guid?)null,
               }));
        var perm = await _client.GrantSharedFolderPermissionAsync(folderId,
            new GrantSharedFolderPermissionRequest("User", userId.ToString("D"), "FolderOperator"),
            CancellationToken.None);
        perm.Id.Should().Be(permId);
        perm.Role.Should().Be("FolderOperator");
    }

    [Fact]
    public async Task MoveWorkflowToFolderAsync_Posts()
    {
        var wf = Guid.NewGuid();
        var target = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{wf}/move-folder").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.MoveWorkflowToFolderAsync(wf, new MoveWorkflowToFolderRequest(target), CancellationToken.None);
        // Reaches here without throwing → success.
    }

    // ---- Observability ------------------------------------------------------

    [Fact]
    public async Task ObservabilityQueryAsync_PassesQueryAndTime()
    {
        _server.Given(Request.Create().WithPath("/api/observability/query")
                .WithParam("query", "up").WithParam("time", "1700000000").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   status = "success",
                   data = new { resultType = "vector", result = Array.Empty<object>() },
               }));
        using var doc = await _client.ObservabilityQueryAsync("up", 1700000000, CancellationToken.None);
        doc.RootElement.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task ObservabilityQueryRangeAsync_PassesAllParams()
    {
        _server.Given(Request.Create().WithPath("/api/observability/query_range")
                .WithParam("query", "up").WithParam("start", "100").WithParam("end", "200").WithParam("step", "15s")
                .UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   status = "success",
                   data = new { resultType = "matrix", result = Array.Empty<object>() },
               }));
        using var doc = await _client.ObservabilityQueryRangeAsync("up", 100, 200, "15s", CancellationToken.None);
        doc.RootElement.GetProperty("data").GetProperty("resultType").GetString().Should().Be("matrix");
    }

    // ---- Step Test ----------------------------------------------------------

    [Fact]
    public async Task TestStepAsync_PostsMocksAndConfig()
    {
        var wf = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{wf}/steps/step-1/test").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   success = true, output = "ok", errorOutput = (string?)null,
                   outputParameters = new Dictionary<string, string> { ["k"] = "v" },
                   durationMs = 12.5, errorMessage = (string?)null,
               }));
        using var cfg = JsonDocument.Parse("""{"script":"Get-Date"}""");
        var req = new StepTestRequest(new Dictionary<string, string> { ["upstream.output"] = "7" }, cfg.RootElement);
        var result = await _client.TestStepAsync(wf, "step-1", req, CancellationToken.None);
        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("k");
    }

    [Fact]
    public async Task ListStepTestContextRunsAsync_PassesLimit()
    {
        var wf = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{wf}/steps/step-1/test-context/runs")
                .WithParam("limit", "5").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));
        var runs = await _client.ListStepTestContextRunsAsync(wf, "step-1", 5, CancellationToken.None);
        runs.Should().BeEmpty();
    }

    // ---- DbAdmin SQL console ------------------------------------------------

    [Fact]
    public async Task GetDbAdminInfoAsync_ReturnsCapabilities()
    {
        _server.Given(Request.Create().WithPath("/api/dbadmin/info").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   provider = "postgres",
                   allowWriteQueries = true,
                   queryTimeoutSeconds = 30,
                   queryMaxRows = 10_000,
               }));
        var info = await _client.GetDbAdminInfoAsync(CancellationToken.None);
        info.Provider.Should().Be("postgres");
        info.AllowWriteQueries.Should().BeTrue();
        info.QueryMaxRows.Should().Be(10_000);
    }

    [Fact]
    public async Task ExecuteDbAdminQueryAsync_ReadMode_DoesNotSendConfirmHeader()
    {
        _server.Given(Request.Create().WithPath("/api/dbadmin/query").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   columns = new[] { new { name = "Id", type = "int" } },
                   rows = new[] { new object[] { 1 } },
                   rowsAffected = (int?)null,
                   durationMs = 12,
                   truncated = false,
                   mode = "read",
               }));

        var resp = await _client.ExecuteDbAdminQueryAsync("SELECT 1", writeMode: false, CancellationToken.None);

        resp.Mode.Should().Be("read");
        resp.Rows.Should().HaveCount(1);

        var captured = _server.LogEntries.Last();
        captured.RequestMessage.Headers.Should().NotContainKey("X-Confirm-Write");
        captured.RequestMessage.Body.Should().Contain("\"read\"");
    }

    [Fact]
    public async Task ExecuteDbAdminQueryAsync_WriteMode_SendsConfirmHeader()
    {
        _server.Given(Request.Create().WithPath("/api/dbadmin/query").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   columns = Array.Empty<object>(),
                   rows = Array.Empty<object[]>(),
                   rowsAffected = 1,
                   durationMs = 5,
                   truncated = false,
                   mode = "write",
               }));

        var resp = await _client.ExecuteDbAdminQueryAsync("UPDATE Users SET IsActive = 0", writeMode: true, CancellationToken.None);

        resp.Mode.Should().Be("write");
        resp.RowsAffected.Should().Be(1);

        var captured = _server.LogEntries.Last();
        captured.RequestMessage.Headers!["X-Confirm-Write"].Should().ContainSingle().Which.Should().Be("ALLOW");
        captured.RequestMessage.Body.Should().Contain("\"write\"");
    }

    // ---- Operations / NOC graph --------------------------------------------

    [Fact]
    public async Task GetOperationsGraphAsync_ParsesNodesEdgesRunningAndCapabilities()
    {
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        var exec = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/operations/graph").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   nodes = new[]
                   {
                       new { workflowId = parent, name = "Parent", folderId = Guid.Empty, folderPath = "/", isEnabled = true, runningCount = 1, lastStatus = (string?)"Running", callFrequency = (int?)7 },
                       new { workflowId = child, name = "Child", folderId = Guid.Empty, folderPath = "/", isEnabled = true, runningCount = 0, lastStatus = (string?)null, callFrequency = (int?)null },
                   },
                   edges = new[]
                   {
                       new { id = "e1", source = parent, target = (Guid?)child, kind = "startWorkflow", refStatus = "Resolved", rawRef = child.ToString(), callCount = 1 },
                   },
                   running = new[]
                   {
                       new { executionId = exec, workflowId = parent, status = "Running", startedAt = DateTime.UtcNow },
                   },
                   recent = new[]
                   {
                       new { executionId = Guid.NewGuid(), workflowId = child, status = "Succeeded", startedAt = DateTime.UtcNow.AddMinutes(-7), completedAt = DateTime.UtcNow.AddMinutes(-5), parentExecutionId = (Guid?)exec },
                   },
                   capabilities = new { canCancel = true },
               }));

        var graph = await _client.GetOperationsGraphAsync(CancellationToken.None);

        graph.Nodes.Should().HaveCount(2);
        graph.Nodes[0].Name.Should().Be("Parent");
        graph.Nodes[0].RunningCount.Should().Be(1);
        graph.Edges.Should().ContainSingle().Which.Target.Should().Be(child);
        graph.Running.Should().ContainSingle().Which.ExecutionId.Should().Be(exec);
        graph.Recent.Should().ContainSingle().Which.Status.Should().Be("Succeeded");
        graph.Recent[0].ParentExecutionId.Should().Be(exec);
        graph.Capabilities.CanCancel.Should().BeTrue();
    }
}
