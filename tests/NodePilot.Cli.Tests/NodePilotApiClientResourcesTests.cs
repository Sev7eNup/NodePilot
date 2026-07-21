using System.Net;
using FluentAssertions;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// API-client tests for the resource-CRUD endpoints (machines/credentials/globals/users),
/// folders, dashboard, observability, debug-resume, force-unlock, scorch-import and the
/// per-version + step-stats lookups. Existing surface is covered by
/// <see cref="NodePilotApiClientTests"/>; this file captures everything added to the CLI
/// after its initial release, to catch up with backend endpoints added since then.
/// </summary>
public sealed class NodePilotApiClientResourcesTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly NodePilotApiClient _client;

    public NodePilotApiClientResourcesTests()
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

    // ---- Machines -----------------------------------------------------------

    [Fact]
    public async Task ListMachinesAsync_ReturnsRows()
    {
        _server.Given(Request.Create().WithPath("/api/machines").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
               {
                   new
                   {
                       id = Guid.NewGuid(), name = "win-build-01", hostname = "build01.lab",
                       winRmPort = 5985, useSsl = false,
                       defaultCredentialId = (Guid?)null, tags = (string?)null,
                       lastConnectivityCheck = (DateTime?)DateTime.UtcNow, isReachable = true,
                   },
               }));

        var rows = await _client.ListMachinesAsync(CancellationToken.None);
        rows.Should().ContainSingle();
        rows[0].Name.Should().Be("win-build-01");
    }

    [Fact]
    public async Task GetMachineAsync_NotFound_Throws()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/machines/{id}").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(404));
        var ex = await Assert.ThrowsAsync<ApiException>(() => _client.GetMachineAsync(id, CancellationToken.None));
        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateMachineAsync_PostsAndReturnsCreated()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/machines").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
               {
                   id, name = "new", hostname = "new.lab",
                   winRmPort = 5985, useSsl = false,
                   defaultCredentialId = (Guid?)null, tags = (string?)null,
                   lastConnectivityCheck = (DateTime?)null, isReachable = false,
               }));

        var m = await _client.CreateMachineAsync(new CreateMachineRequest("new", "new.lab"), CancellationToken.None);
        m.Id.Should().Be(id);
        m.Name.Should().Be("new");
    }

    [Fact]
    public async Task UpdateMachineAsync_PutsAndAcceptsNoContent()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/machines/{id}").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.UpdateMachineAsync(id, new UpdateMachineRequest("x", "x.lab", 5986, true, null, null), CancellationToken.None);
    }

    [Fact]
    public async Task DeleteMachineAsync_DeletesAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/machines/{id}").UsingDelete())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.DeleteMachineAsync(id, CancellationToken.None);
    }

    [Fact]
    public async Task TestMachineAsync_ReturnsResult()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/machines/{id}/test").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   success = true, computerName = "WIN-01",
                   error = (string?)null, credentialUsed = "svc-build",
               }));

        var r = await _client.TestMachineAsync(id, null, CancellationToken.None);
        r.Success.Should().BeTrue();
        r.ComputerName.Should().Be("WIN-01");
        r.CredentialUsed.Should().Be("svc-build");
    }

    [Fact]
    public async Task TestMachineAsync_ServerFails_PropagatesAsApiException()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/machines/{id}/test").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(400).WithBodyAsJson(new { message = "no credential" }));
        await Assert.ThrowsAsync<ApiException>(() => _client.TestMachineAsync(id, null, CancellationToken.None));
    }

    // ---- Credentials --------------------------------------------------------

    [Fact]
    public async Task ListCredentialsAsync_ReturnsRows()
    {
        _server.Given(Request.Create().WithPath("/api/credentials").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
               {
                   new { id = Guid.NewGuid(), name = "svc", username = "svc-build", domain = (string?)"LAB" },
               }));
        var rows = await _client.ListCredentialsAsync(CancellationToken.None);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateCredentialAsync_PostsAndReturnsCreated()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/credentials").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
               {
                   id, name = "svc", username = "svc-build", domain = (string?)null,
               }));
        var c = await _client.CreateCredentialAsync(
            new CreateCredentialRequest("svc", "svc-build", "pw12345678", null), CancellationToken.None);
        c.Id.Should().Be(id);
    }

    [Fact]
    public async Task CreateCredentialAsync_ShortPassword_ServerRejects400()
    {
        _server.Given(Request.Create().WithPath("/api/credentials").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(400).WithBodyAsJson(new { error = "password too short" }));
        await Assert.ThrowsAsync<ApiException>(() => _client.CreateCredentialAsync(
            new CreateCredentialRequest("x", "x", "short", null), CancellationToken.None));
    }

    [Fact]
    public async Task UpdateCredentialAsync_NullPassword_KeepsServerSecret()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/credentials/{id}").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(204));
        // Null password should serialize to "password": null in the JSON body — server treats it
        // as "leave existing secret untouched". We don't introspect the body here; the contract
        // test is that the call succeeds (no client-side validation rejecting null).
        await _client.UpdateCredentialAsync(id,
            new UpdateCredentialRequest("svc", "svc-build", null, null), CancellationToken.None);
    }

    [Fact]
    public async Task DeleteCredentialAsync_DeletesAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/credentials/{id}").UsingDelete())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.DeleteCredentialAsync(id, CancellationToken.None);
    }

    // ---- Global Variables ---------------------------------------------------

    [Fact]
    public async Task ListGlobalVariablesAsync_ReturnsMaskedSecrets()
    {
        _server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
               {
                   new
                   {
                       id = Guid.NewGuid(), name = "STRIPE_KEY", value = "***",
                       isSecret = true, description = (string?)null,
                       createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = (string?)null,
                   },
                   new
                   {
                       id = Guid.NewGuid(), name = "ENV", value = "stg",
                       isSecret = false, description = (string?)null,
                       createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = (string?)null,
                   },
               }));
        var rows = await _client.ListGlobalVariablesAsync(CancellationToken.None);
        rows.Should().HaveCount(2);
        rows[0].Value.Should().Be("***");
        rows[1].Value.Should().Be("stg");
    }

    [Fact]
    public async Task CreateGlobalVariableAsync_PostsAndReturnsCreated()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/global-variables").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
               {
                   id, name = "ENV", value = "stg", isSecret = false, description = (string?)null,
                   createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = (string?)null,
               }));
        var v = await _client.CreateGlobalVariableAsync(
            new CreateGlobalVariableRequest("ENV", "stg", false, null), CancellationToken.None);
        v.Id.Should().Be(id);
    }

    [Fact]
    public async Task UpdateGlobalVariableAsync_PutsAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/global-variables/{id}").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.UpdateGlobalVariableAsync(id,
            new UpdateGlobalVariableRequest("ENV", "prod", false, null), CancellationToken.None);
    }

    [Fact]
    public async Task DeleteGlobalVariableAsync_DeletesAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/global-variables/{id}").UsingDelete())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.DeleteGlobalVariableAsync(id, CancellationToken.None);
    }

    [Fact]
    public async Task MoveGlobalVariableToFolderAsync_PostsAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/global-variables/{id}/move-folder").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.MoveGlobalVariableToFolderAsync(id, Guid.NewGuid(), CancellationToken.None);
    }

    // ---- Global Variable Folders --------------------------------------------

    [Fact]
    public async Task ListGlobalVariableFoldersAsync_ReturnsTree()
    {
        _server.Given(Request.Create().WithPath("/api/global-variable-folders").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
               {
                   new { id = Guid.NewGuid(), parentFolderId = (Guid?)null, name = "Root", path = "/", depth = 0, createdAt = DateTime.UtcNow, createdByUserId = (Guid?)null, variableCount = 3 },
                   new { id = Guid.NewGuid(), parentFolderId = (Guid?)Guid.NewGuid(), name = "Databases", path = "/Databases", depth = 1, createdAt = DateTime.UtcNow, createdByUserId = (Guid?)null, variableCount = 1 },
               }));
        var rows = await _client.ListGlobalVariableFoldersAsync(CancellationToken.None);
        rows.Should().HaveCount(2);
        rows[1].Path.Should().Be("/Databases");
        rows[1].VariableCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateGlobalVariableFolderAsync_PostsAndReturnsCreated()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/global-variable-folders").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
               {
                   id, parentFolderId = (Guid?)null, name = "Databases", path = "/Databases", depth = 1,
                   createdAt = DateTime.UtcNow, createdByUserId = (Guid?)null, variableCount = 0,
               }));
        var f = await _client.CreateGlobalVariableFolderAsync(
            new CreateGlobalVariableFolderRequest(null, "Databases"), CancellationToken.None);
        f.Id.Should().Be(id);
        f.Path.Should().Be("/Databases");
    }

    [Fact]
    public async Task RenameGlobalVariableFolderAsync_PutsAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/global-variable-folders/{id}").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.RenameGlobalVariableFolderAsync(id, new UpdateGlobalVariableFolderRequest("Renamed"), CancellationToken.None);
    }

    [Fact]
    public async Task MoveGlobalVariableFolderAsync_PostsAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/global-variable-folders/{id}/move").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.MoveGlobalVariableFolderAsync(id, new MoveGlobalVariableFolderRequest(null), CancellationToken.None);
    }

    [Fact]
    public async Task DeleteGlobalVariableFolderAsync_DeletesAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/global-variable-folders/{id}").UsingDelete())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.DeleteGlobalVariableFolderAsync(id, CancellationToken.None);
    }

    // ---- Users (Admin) ------------------------------------------------------

    [Fact]
    public async Task ListUsersAsync_ReturnsRows()
    {
        _server.Given(Request.Create().WithPath("/api/users").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
               {
                   new { id = Guid.NewGuid(), username = "alice", role = "Admin", isActive = true, createdAt = DateTime.UtcNow },
               }));
        var rows = await _client.ListUsersAsync(CancellationToken.None);
        rows.Should().ContainSingle();
        rows[0].Username.Should().Be("alice");
    }

    [Fact]
    public async Task CreateUserAsync_BadRole_ServerRejects400()
    {
        _server.Given(Request.Create().WithPath("/api/users").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(400).WithBodyAsJson(new { message = "invalid role" }));
        await Assert.ThrowsAsync<ApiException>(() => _client.CreateUserAsync(
            new CreateUserRequest("alice", "pw12345678", "WrongRole"), CancellationToken.None));
    }

    [Fact]
    public async Task UpdateUserAsync_PutsAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/users/{id}").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.UpdateUserAsync(id, new UpdateUserRequest("Operator", true, null), CancellationToken.None);
    }

    [Fact]
    public async Task DeleteUserAsync_DeletesAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/users/{id}").UsingDelete())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.DeleteUserAsync(id, CancellationToken.None);
    }

    // ---- Workflow versions / force-unlock / scorch / stats -----------------

    [Fact]
    public async Task GetVersionAsync_ReturnsDetail()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/versions/3").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   version = 3, name = "Build", description = (string?)null,
                   definitionJson = "{\"nodes\":[]}",
                   createdAt = DateTime.UtcNow, createdBy = "admin",
                   changeNote = (string?)"hotfix", isCurrent = false,
               }));

        var d = await _client.GetVersionAsync(id, 3, CancellationToken.None);
        d.Version.Should().Be(3);
        d.IsCurrent.Should().BeFalse();
        d.ChangeNote.Should().Be("hotfix");
    }

    [Fact]
    public async Task ForceUnlockWorkflowAsync_ReturnsWorkflow()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/force-unlock").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(SampleWorkflow(id, "Build")));
        var w = await _client.ForceUnlockWorkflowAsync(id, CancellationToken.None);
        w.Id.Should().Be(id);
    }

    [Fact]
    public async Task ForceUnlockWorkflowAsync_NonAdmin_PropagatesForbidden()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}/force-unlock").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(403));
        var ex = await Assert.ThrowsAsync<ApiException>(() => _client.ForceUnlockWorkflowAsync(id, CancellationToken.None));
        ex.IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task ImportScorchAsync_PostsXmlBody()
    {
        _server.Given(Request.Create().WithPath("/api/workflows/import-scorch").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   created = 1,
                   workflows = new[]
                   {
                       new
                       {
                           id = Guid.NewGuid(), name = "Migrated",
                           originalName = (string?)null,
                           activityCount = 5, heuristicCount = 1, fallbackCount = 0,
                       },
                   },
                   variables = Array.Empty<object>(),
                   warnings = Array.Empty<string>(),
                   errors = Array.Empty<string>(),
               }));

        var resp = await _client.ImportScorchAsync("<Policy/>", folderId: null, CancellationToken.None);
        resp.Created.Should().Be(1);
        resp.Workflows[0].Name.Should().Be("Migrated");

        var entry = _server.LogEntries.Should().ContainSingle().Subject;
        entry.RequestMessage.Headers!["Content-Type"].Should().ContainMatch("application/xml*");
    }

    [Fact]
    public async Task GetStepStatsAsync_NoWindow_OmitsQueryString()
    {
        var workflowId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{workflowId}/step-stats").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new Dictionary<string, object>
               {
                   ["step-1"] = new
                   {
                       totalRuns = 10, failedRuns = 1, failureRate = 0.1,
                       avgDurationMs = 200L, p95DurationMs = 500L, lastDurationMs = 180L,
                   },
               }));

        var stats = await _client.GetStepStatsAsync(workflowId, null, CancellationToken.None);
        stats.Should().ContainKey("step-1");
        stats["step-1"].TotalRuns.Should().Be(10);
    }

    [Fact]
    public async Task GetStepStatsAsync_WithWindow_AddsWindowDaysQuery()
    {
        var workflowId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{workflowId}/step-stats").UsingGet()
                .WithParam("windowDays", "7"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new Dictionary<string, object>()));

        var stats = await _client.GetStepStatsAsync(workflowId, 7, CancellationToken.None);
        stats.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStepHealthAsync_ReturnsRecentEntries()
    {
        var workflowId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{workflowId}/step-health").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new Dictionary<string, object>
               {
                   ["step-1"] = new[] { new { status = "Succeeded", startedAt = (DateTime?)DateTime.UtcNow } },
               }));
        var health = await _client.GetStepHealthAsync(workflowId, null, CancellationToken.None);
        health.Should().ContainKey("step-1");
        health["step-1"].Should().ContainSingle();
    }

    // ---- Dashboard / Observability -----------------------------------------

    [Fact]
    public async Task GetDashboardAsync_ReturnsStats()
    {
        _server.Given(Request.Create().WithPath("/api/stats/dashboard").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   workflowsTotal = 12, workflowsEnabled = 9,
                   machinesTotal = 4, machinesReachable = 4,
                   executionsTotal = 9001,
                   last24h = new { total = 80, succeeded = 70, failed = 5, running = 2, cancelled = 3 },
                   last24hBuckets = Array.Empty<object>(),
                   topWorkflows = Array.Empty<object>(),
                   running = Array.Empty<object>(),
                   recent = Array.Empty<object>(),
                   armedTriggers = Array.Empty<object>(),
               }));

        var d = await _client.GetDashboardAsync(CancellationToken.None);
        d.WorkflowsTotal.Should().Be(12);
        d.MachinesReachable.Should().Be(4);
        d.Last24h.Failed.Should().Be(5);
    }

    [Fact]
    public async Task GetObservabilitySummaryAsync_NotConfigured_ReturnsAvailableFalse()
    {
        _server.Given(Request.Create().WithPath("/api/observability/summary").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   available = false, panels = Array.Empty<object>(),
               }));
        var r = await _client.GetObservabilitySummaryAsync(CancellationToken.None);
        r.Available.Should().BeFalse();
        r.Panels.Should().BeEmpty();
    }

    [Fact]
    public async Task GetObservabilitySummaryAsync_Available_ReturnsPanels()
    {
        _server.Given(Request.Create().WithPath("/api/observability/summary").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   available = true,
                   panels = new[]
                   {
                       new { key = "executions_per_minute", title = "Exec/min", unit = "per_min", value = (double?)4.2, error = (string?)null },
                       new { key = "p95_duration_5m", title = "p95", unit = "ms", value = (double?)null, error = (string?)"prom timeout" },
                   },
               }));
        var r = await _client.GetObservabilitySummaryAsync(CancellationToken.None);
        r.Available.Should().BeTrue();
        r.Panels.Should().HaveCount(2);
        r.Panels[1].Error.Should().Be("prom timeout");
    }

    // ---- Debug resume / paused-steps ----------------------------------------

    [Fact]
    public async Task ResumeExecutionAsync_PostsAndAccepts204()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/executions/{id}/resume").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(204));
        await _client.ResumeExecutionAsync(id,
            new ResumeDebugRequest("step-1", "continue", null), CancellationToken.None);
    }

    [Fact]
    public async Task ResumeExecutionAsync_NoPausedStep_PropagatesConflict()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/executions/{id}/resume").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(409).WithBodyAsJson(new { message = "Already resumed" }));
        var ex = await Assert.ThrowsAsync<ApiException>(() => _client.ResumeExecutionAsync(id,
            new ResumeDebugRequest("step-1", "continue", null), CancellationToken.None));
        ex.IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task GetPausedStepsAsync_ReturnsList()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/executions/{id}/paused-steps").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[] { "step-1", "step-2" }));
        var list = await _client.GetPausedStepsAsync(id, CancellationToken.None);
        list.Should().HaveCount(2);
        list.Should().Contain("step-1");
    }

    // ---- Sample bodies ------------------------------------------------------

    private static string SampleWorkflow(Guid id, string name) => $$"""
    {
      "id": "{{id}}", "name": "{{name}}", "description": null,
      "definitionJson": "{}", "version": 1, "isEnabled": false,
      "createdAt": "2026-04-01T00:00:00Z", "updatedAt": "2026-04-01T00:00:00Z",
      "createdBy": null, "updatedBy": null,
      "activityCount": 1, "triggerTypes": [],
      "lastExecution": null, "successCount": 0, "totalCount": 0, "avgDurationMs": null,
      "checkedOutByUserId": null, "checkedOutByUserName": null, "checkedOutAt": null
    }
    """;
}
