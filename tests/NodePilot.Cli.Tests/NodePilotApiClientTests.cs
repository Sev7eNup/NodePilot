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
/// Each public method on <see cref="NodePilotApiClient"/> gets at least one happy-path
/// test against a WireMock server, plus the error pathways that the CLI specifically
/// branches on (401 / 403 / 423 / 409 / 404 / 500 / non-JSON 5xx body).
/// </summary>
public sealed class NodePilotApiClientTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly NodePilotApiClient _client;

    public NodePilotApiClientTests()
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

    // ---- Auth ---------------------------------------------------------------

    [Fact]
    public async Task LoginAsync_PostsCredentialsAndReturnsToken()
    {
        var userId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/auth/login").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   token = "jwt-abc", userId, username = "admin", role = "Admin",
               }));

        var resp = await _client.LoginAsync(new LoginRequest("admin", "pw12345678"), null, CancellationToken.None);
        resp.Token.Should().Be("jwt-abc");
        resp.Username.Should().Be("admin");
        resp.Role.Should().Be("Admin");
        resp.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task LoginAsync_WithSetupToken_AddsXSetupTokenHeader()
    {
        _server.Given(Request.Create().WithPath("/api/auth/login").UsingPost()
                .WithHeader("X-Setup-Token", "bootstrap-secret"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   token = "jwt", userId = Guid.NewGuid(), username = "admin", role = "Admin",
               }));

        var resp = await _client.LoginAsync(new LoginRequest("admin", "pw12345678"), "bootstrap-secret", CancellationToken.None);
        resp.Token.Should().Be("jwt");
    }

    [Fact]
    public async Task LoginAsync_OptsInToTokenInBody_ViaXAuthTokenResponseHeader()
    {
        // The CLI is a Bearer client: it must opt in to receiving the JWT in the body, because
        // the server now withholds the token from browser (cookie) callers by default — a
        // security hardening from a past audit finding (tracked as H-5).
        _server.Given(Request.Create().WithPath("/api/auth/login").UsingPost()
                .WithHeader("X-Auth-Token-Response", "true"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   token = "jwt", userId = Guid.NewGuid(), username = "admin", role = "Admin",
               }));

        var resp = await _client.LoginAsync(new LoginRequest("admin", "pw12345678"), null, CancellationToken.None);
        resp.Token.Should().Be("jwt");
    }

    [Fact]
    public async Task LoginAsync_BadCredentials_ThrowsUnauthorized()
    {
        _server.Given(Request.Create().WithPath("/api/auth/login").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(401));

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            _client.LoginAsync(new LoginRequest("admin", "wrong"), null, CancellationToken.None));
        ex.IsUnauthorized.Should().BeTrue();
    }

    [Fact]
    public async Task LogoutAsync_PostsAndAcceptsNoContent()
    {
        _server.Given(Request.Create().WithPath("/api/auth/logout").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(204));

        await _client.LogoutAsync(CancellationToken.None);
        _server.LogEntries.Should().Contain(e => e.RequestMessage.AbsolutePath == "/api/auth/logout");
    }

    [Fact]
    public async Task RefreshAsync_ReturnsRotatedToken()
    {
        _server.Given(Request.Create().WithPath("/api/auth/refresh").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   token = "rotated", userId = Guid.NewGuid(), username = "admin", role = "Admin",
               }));

        var resp = await _client.RefreshAsync(CancellationToken.None);
        resp.Token.Should().Be("rotated");
    }

    [Fact]
    public async Task MeAsync_ReturnsClaims()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/auth/me").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   id, username = "alice", role = "Operator",
               }));

        var me = await _client.MeAsync(CancellationToken.None);
        me.Username.Should().Be("alice");
        me.Role.Should().Be("Operator");
        me.Id.Should().Be(id);
    }

    // ---- Workflows ----------------------------------------------------------

    [Fact]
    public async Task ListWorkflowsAsync_ReturnsRows()
    {
        _server.Given(Request.Create().WithPath("/api/workflows").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleListBody));

        var rows = await _client.ListWorkflowsAsync(CancellationToken.None);
        rows.Should().HaveCount(2);
        rows[0].Name.Should().Be("Build");
        rows[1].Name.Should().Be("Report");
    }

    [Fact]
    public async Task GetWorkflowAsync_NotFound_ThrowsApiException()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(404));

        var ex = await Assert.ThrowsAsync<ApiException>(() => _client.GetWorkflowAsync(id, CancellationToken.None));
        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LockWorkflowAsync_LockedResponse_ThrowsApiExceptionWithIsLocked()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/lock").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(423).WithBodyAsJson(new
               {
                   title = "Locked", detail = "Workflow already checked out by alice.",
               }));

        var ex = await Assert.ThrowsAsync<ApiException>(() => _client.LockWorkflowAsync(id, CancellationToken.None));
        ex.IsLocked.Should().BeTrue();
        ex.Detail.Should().Contain("alice");
    }

    [Fact]
    public async Task UnlockWorkflowAsync_RoundtripsResponse()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/unlock").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleSingleBody(id, "Build")));

        var w = await _client.UnlockWorkflowAsync(id, CancellationToken.None);
        w.Id.Should().Be(id);
        w.Name.Should().Be("Build");
    }

    [Fact]
    public async Task PublishWorkflowAsync_PostsBody()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/publish").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleSingleBody(id, "Build")));

        var w = await _client.PublishWorkflowAsync(id,
            new PublishWorkflowRequest("Build", "desc", "{\"nodes\":[]}"), CancellationToken.None);
        w.Id.Should().Be(id);
    }

    [Fact]
    public async Task PublishWorkflowAsync_Forbidden_ThrowsForbidden()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/publish").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(403));

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            _client.PublishWorkflowAsync(id, new PublishWorkflowRequest("x", null, "{}"), CancellationToken.None));
        ex.IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task EnableWorkflowAsync_PostsAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/enable").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.EnableWorkflowAsync(id, CancellationToken.None);
    }

    [Fact]
    public async Task EnableWorkflowAsync_Locked_PropagatesLocked()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/enable").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(423));
        var ex = await Assert.ThrowsAsync<ApiException>(() => _client.EnableWorkflowAsync(id, CancellationToken.None));
        ex.IsLocked.Should().BeTrue();
    }

    [Fact]
    public async Task DisableWorkflowAsync_PostsAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/disable").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.DisableWorkflowAsync(id, CancellationToken.None);
    }

    [Fact]
    public async Task CancelAllAsync_ReturnsCounts()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/cancel-all").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { total = 5, signalled = 3 }));

        var result = await _client.CancelAllAsync(id, CancellationToken.None);
        result.Total.Should().Be(5);
        result.Signalled.Should().Be(3);
    }

    [Fact]
    public async Task DuplicateWorkflowAsync_ReturnsCopy()
    {
        var id = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/duplicate").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(201).WithBody(SampleSingleBody(copyId, "Build (Copy)")));

        var copy = await _client.DuplicateWorkflowAsync(id, CancellationToken.None);
        copy.Id.Should().Be(copyId);
        copy.Name.Should().Be("Build (Copy)");
    }

    [Fact]
    public async Task DeleteWorkflowAsync_DeletesAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingDelete())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.DeleteWorkflowAsync(id, CancellationToken.None);
    }

    [Fact]
    public async Task ExportOneAsync_ReturnsEnvelope()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/export").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleEnvelopeBody(single: true)));

        var env = await _client.ExportOneAsync(id, CancellationToken.None);
        env.Schema.Should().Be("nodepilot-workflow-export/v1");
        env.Workflow.Should().NotBeNull();
        env.Workflow!.Name.Should().Be("Build");
    }

    [Fact]
    public async Task ExportAllAsync_ReturnsBulkEnvelope()
    {
        _server.Given(Request.Create().WithPath("/api/workflows/export").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleEnvelopeBody(single: false)));

        var env = await _client.ExportAllAsync(CancellationToken.None);
        env.Workflows.Should().NotBeNull();
        env.Workflows!.Should().HaveCount(2);
    }

    [Fact]
    public async Task ImportAsync_PostsEnvelope()
    {
        _server.Given(Request.Create().WithPath("/api/workflows/import").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   created = 1,
                   workflows = new[] { new { id = Guid.NewGuid(), name = "Imported", originalName = (string?)null } },
                   errors = Array.Empty<string>(),
               }));

        var env = JsonSerializer.Deserialize<WorkflowExportEnvelope>(
            SampleEnvelopeBody(single: true), NodePilotApiClient.JsonOptions)!;
        var result = await _client.ImportAsync(env, folderId: null, CancellationToken.None);
        result.Created.Should().Be(1);
        result.Workflows.Should().ContainSingle();
    }

    [Fact]
    public async Task ImportAsync_WithFolder_AppendsFolderIdQuery()
    {
        var folderId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/workflows/import")
                    .WithParam("folderId", folderId.ToString()).UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   created = 1,
                   workflows = new[] { new { id = Guid.NewGuid(), name = "Imported", originalName = (string?)null } },
                   errors = Array.Empty<string>(),
               }));

        var env = JsonSerializer.Deserialize<WorkflowExportEnvelope>(
            SampleEnvelopeBody(single: true), NodePilotApiClient.JsonOptions)!;
        var result = await _client.ImportAsync(env, folderId, CancellationToken.None);
        result.Created.Should().Be(1);
    }

    [Fact]
    public async Task ListVersionsAsync_ReturnsRows()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/versions").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
               {
                   new { version = 2, name = "v2", createdAt = DateTime.UtcNow,
                         createdBy = "admin", changeNote = (string?)null, isCurrent = true },
                   new { version = 1, name = "v1", createdAt = DateTime.UtcNow.AddDays(-1),
                         createdBy = "admin", changeNote = (string?)null, isCurrent = false },
               }));

        var versions = await _client.ListVersionsAsync(id, CancellationToken.None);
        versions.Should().HaveCount(2);
        versions[0].IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task RollbackAsync_PostsBody()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/rollback/3").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleSingleBody(id, "Build")));

        var w = await _client.RollbackAsync(id, 3, new RollbackRequest("revert"), CancellationToken.None);
        w.Id.Should().Be(id);
    }

    // ---- Executions ---------------------------------------------------------

    [Fact]
    public async Task ListExecutionsAsync_NoFilter_HitsBaseEndpoint()
    {
        _server.Given(Request.Create().WithPath("/api/executions").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var rows = await _client.ListExecutionsAsync(null, CancellationToken.None);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ListExecutionsAsync_WithFilter_AddsWorkflowIdQuery()
    {
        var workflowId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/executions").UsingGet()
                .WithParam("workflowId", workflowId.ToString()))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var rows = await _client.ListExecutionsAsync(workflowId, CancellationToken.None);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task GetExecutionAsync_ReturnsResponse()
    {
        var id = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/executions/{id}").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   id, workflowId, status = "Succeeded",
                   startedAt = DateTime.UtcNow, completedAt = DateTime.UtcNow,
                   triggeredBy = "admin", errorMessage = (string?)null,
               }));

        var e = await _client.GetExecutionAsync(id, CancellationToken.None);
        e.Id.Should().Be(id);
        e.Status.Should().Be("Succeeded");
    }

    [Fact]
    public async Task GetStepsAsync_ReturnsRows()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/executions/{id}/steps").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
               {
                   new
                   {
                       id = Guid.NewGuid(), stepId = "s1", stepName = "Disk", stepType = "runScript",
                       targetMachine = (string?)null, status = "Succeeded",
                       startedAt = (DateTime?)DateTime.UtcNow, completedAt = (DateTime?)DateTime.UtcNow,
                       output = (string?)"ok", errorOutput = (string?)null,
                       attemptCount = 1, pausedAt = (DateTime?)null,
                       variablesSnapshot = (string?)null, traceOutput = (string?)null,
                   },
               }));

        var steps = await _client.GetStepsAsync(id, CancellationToken.None);
        steps.Should().ContainSingle();
        steps[0].StepId.Should().Be("s1");
    }

    [Fact]
    public async Task CancelExecutionAsync_PostsAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/executions/{id}/cancel").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.CancelExecutionAsync(id, CancellationToken.None);
    }

    [Fact]
    public async Task RetryExecutionAsync_ReturnsNewExecution()
    {
        var id = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/executions/{id}/retry").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(202).WithBodyAsJson(new
               {
                   id = newId, workflowId, status = "Pending",
                   startedAt = DateTime.UtcNow, completedAt = (DateTime?)null,
                   triggeredBy = (string?)null, errorMessage = (string?)null,
               }));

        var retry = await _client.RetryExecutionAsync(id, CancellationToken.None);
        retry.Id.Should().Be(newId);
        retry.Status.Should().Be("Pending");
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_PostsParameters()
    {
        var id = Guid.NewGuid();
        var execId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/execute").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(202).WithBodyAsJson(new
               {
                   id = execId, workflowId = id, status = "Pending",
                   startedAt = DateTime.UtcNow, completedAt = (DateTime?)null,
                   triggeredBy = "admin", errorMessage = (string?)null,
               }));

        var req = new ExecuteWorkflowRequest(new() { ["env"] = "stg" }, TimeoutSeconds: 60, Debug: false);
        var resp = await _client.ExecuteWorkflowAsync(id, req, CancellationToken.None);
        resp.Id.Should().Be(execId);
    }

    // ---- Audit / Cron / Health ---------------------------------------------

    [Fact]
    public async Task AuditAsync_BuildsQueryStringFromAllFilters()
    {
        var since = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc);
        var userId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();

        _server.Given(Request.Create().WithPath("/api/audit").UsingGet()
                .WithParam("action", "WORKFLOW_PUBLISHED")
                .WithParam("resourceType", "Workflow")
                .WithParam("resourceId", resourceId.ToString())
                .WithParam("userId", userId.ToString())
                .WithParam("ipAddress", "10.0.0.1")
                .WithParam("take", "10"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { items = Array.Empty<object>(), nextCursor = (object?)null }));

        var page = await _client.AuditAsync(
            "WORKFLOW_PUBLISHED", "Workflow", resourceId, userId,
            "10.0.0.1", since, until, null, null, 10, CancellationToken.None);
        page.Items.Should().BeEmpty();
        page.NextCursor.Should().BeNull();

        var entry = _server.LogEntries.Should().ContainSingle(e => e.RequestMessage.AbsolutePath == "/api/audit").Subject;
        entry.RequestMessage.RawQuery.Should().Contain("since=").And.Contain("until=");
    }

    [Fact]
    public async Task AuditAsync_NoFilters_ReturnsItemsAndCursor()
    {
        var cursorId = Guid.NewGuid();
        var cursorTs = DateTime.UtcNow.AddMinutes(-5);
        _server.Given(Request.Create().WithPath("/api/audit").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   items = new[]
                   {
                       new { id = Guid.NewGuid(), timestamp = DateTime.UtcNow, userId = (Guid?)null,
                             username = "alice", action = "LOGIN_SUCCESS",
                             resourceType = (string?)null, resourceId = (Guid?)null,
                             details = (string?)null, ipAddress = "::1" },
                   },
                   nextCursor = new { timestamp = cursorTs, id = cursorId },
               }));

        var page = await _client.AuditAsync(null, null, null, null, null, null, null, null, null, null, CancellationToken.None);
        page.Items.Should().ContainSingle();
        page.Items[0].Action.Should().Be("LOGIN_SUCCESS");
        page.Items[0].Username.Should().Be("alice");
        page.Items[0].IpAddress.Should().Be("::1");
        page.NextCursor.Should().NotBeNull();
        page.NextCursor!.Id.Should().Be(cursorId);
    }

    [Fact]
    public async Task AuditAsync_PassesCursorOnSecondPage()
    {
        var afterTs = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var afterId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/audit").UsingGet()
                .WithParam("afterId", afterId.ToString()))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { items = Array.Empty<object>(), nextCursor = (object?)null }));

        var page = await _client.AuditAsync(null, null, null, null, null, null, null, afterTs, afterId, null, CancellationToken.None);
        page.Items.Should().BeEmpty();

        var entry = _server.LogEntries.Should().ContainSingle(e => e.RequestMessage.AbsolutePath == "/api/audit").Subject;
        entry.RequestMessage.RawQuery.Should().Contain("afterTs=").And.Contain($"afterId={afterId}");
    }

    [Fact]
    public async Task AuditAsync_Forbidden_ThrowsForbidden()
    {
        _server.Given(Request.Create().WithPath("/api/audit").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(403));
        var ex = await Assert.ThrowsAsync<ApiException>(() => _client.AuditAsync(null, null, null, null, null, null, null, null, null, null, CancellationToken.None));
        ex.IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task CronNextFiresAsync_EncodesQueryString()
    {
        _server.Given(Request.Create().WithPath("/api/triggers/schedule/next-fires").UsingGet()
                .WithParam("cron", "0 0 * * * ?")
                .WithParam("count", "3"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   fires = new[] { DateTime.UtcNow, DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(2) },
                   summary = "every hour",
               }));

        var resp = await _client.CronNextFiresAsync("0 0 * * * ?", 3, CancellationToken.None);
        resp.Fires.Should().HaveCount(3);
        resp.Summary.Should().Be("every hour");
    }

    [Fact]
    public async Task HealthAsync_BothOk_ReturnsTrueTrue()
    {
        _server.Given(Request.Create().WithPath("/healthz/live").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/healthz/ready").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));

        var (live, ready, _, _) = await _client.HealthAsync(CancellationToken.None);
        live.Should().BeTrue();
        ready.Should().BeTrue();
    }

    [Fact]
    public async Task HealthAsync_ReadyDown_ReturnsDetail()
    {
        _server.Given(Request.Create().WithPath("/healthz/live").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/healthz/ready").UsingGet()).RespondWith(Response.Create().WithStatusCode(503).WithBody("db unreachable"));

        var (live, ready, detail, _) = await _client.HealthAsync(CancellationToken.None);
        live.Should().BeTrue();
        ready.Should().BeFalse();
        detail.Should().Contain("db");
    }

    [Fact]
    public async Task HealthAsync_LeaderOk_ReturnsLeaderStatus()
    {
        _server.Given(Request.Create().WithPath("/healthz/live").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/healthz/ready").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/healthz/leader").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { status = "leader", nodeId = "NODE-A" }));

        var (_, _, _, leader) = await _client.HealthAsync(CancellationToken.None);
        leader.Should().Be("leader");
    }

    [Fact]
    public async Task HealthAsync_Follower503_SurfacesStatusWithoutFoldingIntoReady()
    {
        _server.Given(Request.Create().WithPath("/healthz/live").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/healthz/ready").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/healthz/leader").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(503).WithBodyAsJson(new { status = "follower", nodeId = "NODE-B", reason = "not_leader" }));

        var (live, ready, _, leader) = await _client.HealthAsync(CancellationToken.None);
        live.Should().BeTrue();
        ready.Should().BeTrue();
        leader.Should().Be("follower");
    }

    [Fact]
    public async Task HealthAsync_LeaderNonJsonBody_ReturnsNullLeaderStatus()
    {
        _server.Given(Request.Create().WithPath("/healthz/live").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/healthz/ready").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/healthz/leader").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("not here"));

        var (_, _, _, leader) = await _client.HealthAsync(CancellationToken.None);
        leader.Should().BeNull();
    }

    // ---- Generic plumbing ---------------------------------------------------

    [Fact]
    public async Task NonJsonError_PreservesRawBodyAsDetail()
    {
        _server.Given(Request.Create().WithPath("/api/workflows").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(500).WithBody("internal-server-error"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => _client.ListWorkflowsAsync(CancellationToken.None));
        ex.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        ex.RawBody.Should().Contain("internal");
    }

    [Fact]
    public async Task ProblemDetailsError_ParsesTitleAndDetail()
    {
        _server.Given(Request.Create().WithPath("/api/workflows").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(409).WithBodyAsJson(new
               {
                   title = "Conflict", detail = "Idempotency replay.",
               }));
        var ex = await Assert.ThrowsAsync<ApiException>(() => _client.ListWorkflowsAsync(CancellationToken.None));
        ex.IsConflict.Should().BeTrue();
        ex.Title.Should().Be("Conflict");
        ex.Detail.Should().Be("Idempotency replay.");
    }

    [Fact]
    public void BearerToken_RoundtripsViaAuthorizationHeader()
    {
        _client.BearerToken = "abc";
        _client.Http.DefaultRequestHeaders.Authorization!.Scheme.Should().Be("Bearer");
        _client.Http.DefaultRequestHeaders.Authorization!.Parameter.Should().Be("abc");
        _client.BearerToken = null;
        _client.Http.DefaultRequestHeaders.Authorization.Should().BeNull();
    }

    // ---- Sample bodies ------------------------------------------------------

    private static string SampleListBody => """
    [
      { "id": "00000000-0000-0000-0000-000000000001", "name": "Build", "description": null,
        "definitionJson": "{}", "version": 1, "isEnabled": true,
        "createdAt": "2026-04-01T00:00:00Z", "updatedAt": "2026-04-01T00:00:00Z",
        "createdBy": null, "updatedBy": null,
        "activityCount": 3, "triggerTypes": ["manualTrigger"],
        "lastExecution": null, "successCount": 0, "totalCount": 0, "avgDurationMs": null,
        "checkedOutByUserId": null, "checkedOutByUserName": null, "checkedOutAt": null
      },
      { "id": "00000000-0000-0000-0000-000000000002", "name": "Report", "description": "daily",
        "definitionJson": "{}", "version": 1, "isEnabled": false,
        "createdAt": "2026-04-01T00:00:00Z", "updatedAt": "2026-04-01T00:00:00Z",
        "createdBy": null, "updatedBy": null,
        "activityCount": 5, "triggerTypes": ["scheduleTrigger"],
        "lastExecution": null, "successCount": 0, "totalCount": 0, "avgDurationMs": null,
        "checkedOutByUserId": null, "checkedOutByUserName": null, "checkedOutAt": null
      }
    ]
    """;

    private static string SampleSingleBody(Guid id, string name) => $$"""
    {
      "id": "{{id}}", "name": "{{name}}", "description": null,
      "definitionJson": "{}", "version": 1, "isEnabled": true,
      "createdAt": "2026-04-01T00:00:00Z", "updatedAt": "2026-04-01T00:00:00Z",
      "createdBy": null, "updatedBy": null,
      "activityCount": 1, "triggerTypes": [],
      "lastExecution": null, "successCount": 0, "totalCount": 0, "avgDurationMs": null,
      "checkedOutByUserId": null, "checkedOutByUserName": null, "checkedOutAt": null
    }
    """;

    private static string SampleEnvelopeBody(bool single)
    {
        var item = """{ "name": "Build", "description": null, "definition": { "nodes": [], "edges": [] }, "isEnabled": true }""";
        if (single)
            return $$"""
            { "schema": "nodepilot-workflow-export/v1", "exportVersion": 1,
              "exportedAt": "2026-04-01T00:00:00Z", "workflow": {{item}}, "workflows": null }
            """;
        return $$"""
        { "schema": "nodepilot-workflow-export/v1", "exportVersion": 1,
          "exportedAt": "2026-04-01T00:00:00Z", "workflow": null, "workflows": [{{item}}, {{item}}] }
        """;
    }
}
