using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

public sealed class SupportingAndTelemetryToolsTests
{
    [Fact]
    public async Task ListCredentials_NeverExposesPassword()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/credentials").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { id = Guid.NewGuid(), name = "Svc", username = "DOMAIN\\svc", domain = "DOMAIN" },
            }));

        var tools = new SupportingDataTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ListCredentials());
        json.Should().Contain("DOMAIN\\\\svc");
        json.Should().NotContain("password");
    }

    [Fact]
    public async Task CreateCredential_SendsPassword_ReturnsIdWithoutPassword()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/credentials").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id, name = "Svc", username = "svc", domain = (string?)null }));

        var tools = new SupportingDataTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.CreateCredential("Svc", "svc", "hunter2pw"));

        json.Should().Contain(id.ToString());
        json.Should().NotContain("hunter2pw"); // password not echoed back to the agent
        // ...but it WAS sent to the API.
        api.Server.LogEntries.Last().RequestMessage.Body.Should().Contain("hunter2pw");
    }

    [Fact]
    public async Task CreateCredential_WithExpiresAt_SendsIt()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/credentials").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id, name = "Svc", username = "svc", domain = (string?)null, expiresAt = "2026-12-31T00:00:00Z" }));

        var tools = new SupportingDataTools(api.Client());
        await tools.CreateCredential("Svc", "svc", "hunter2pw", expiresAt: new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));

        api.Server.LogEntries.Last().RequestMessage.Body.Should().Contain("2026-12-31");
    }

    [Fact]
    public async Task UpdateCredential_ExpiryMerge_KeepsClearsAndReplaces()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/credentials/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id, name = "Svc", username = "svc", domain = (string?)null, expiresAt = "2026-12-31T00:00:00Z" }));
        api.Server.Given(Request.Create().WithPath($"/api/credentials/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new SupportingDataTools(api.Client());

        // Omitted expiresAt keeps the stored one (read-modify-write).
        await tools.UpdateCredential(id.ToString(), name: "Svc-2");
        api.Server.LogEntries.Last().RequestMessage.Body.Should().Contain("2026-12-31");

        // clearExpiresAt wins over the stored value.
        await tools.UpdateCredential(id.ToString(), clearExpiresAt: true);
        api.Server.LogEntries.Last().RequestMessage.Body.Should().Contain("\"expiresAt\":null");

        // Explicit expiresAt replaces the stored one.
        await tools.UpdateCredential(id.ToString(), expiresAt: new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        api.Server.LogEntries.Last().RequestMessage.Body.Should().Contain("2027-06-01");
    }

    [Fact]
    public async Task ListGlobalVariables_PassesThroughServerMaskedSecret()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { id = Guid.NewGuid(), name = "API_TOKEN", value = "***", isSecret = true, description = (string?)null,
                      createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = "admin" },
            }));

        var tools = new SupportingDataTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ListGlobalVariables());
        json.Should().Contain("API_TOKEN");
        json.Should().Contain("***");
    }

    [Fact]
    public async Task ListGlobalVariableFolders_ReturnsTree()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/global-variable-folders").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { id = Guid.NewGuid(), parentFolderId = (Guid?)null, name = "Root", path = "/", depth = 0, createdAt = DateTime.UtcNow, createdByUserId = (Guid?)null, variableCount = 2 },
                new { id = Guid.NewGuid(), parentFolderId = (Guid?)Guid.NewGuid(), name = "Databases", path = "/Databases", depth = 1, createdAt = DateTime.UtcNow, createdByUserId = (Guid?)null, variableCount = 1 },
            }));

        var tools = new SupportingDataTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ListGlobalVariableFolders());
        json.Should().Contain("/Databases");
        json.Should().Contain("\"count\":2");
    }

    [Fact]
    public async Task CreateGlobalVariableFolder_PostsAndReturnsPath()
    {
        using var api = new TestApi();
        var id = Guid.NewGuid();
        api.Server.Given(Request.Create().WithPath("/api/global-variable-folders").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
            {
                id, parentFolderId = (Guid?)null, name = "Databases", path = "/Databases", depth = 1,
                createdAt = DateTime.UtcNow, createdByUserId = (Guid?)null, variableCount = 0,
            }));

        var tools = new SupportingDataTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.CreateGlobalVariableFolder("Databases"));
        json.Should().Contain("\"created\":true");
        json.Should().Contain("/Databases");
    }

    [Fact]
    public async Task MoveGlobalVariableToFolder_Posts()
    {
        using var api = new TestApi();
        var id = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        api.Server.Given(Request.Create().WithPath($"/api/global-variables/{id}/move-folder").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new SupportingDataTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.MoveGlobalVariableToFolder(id.ToString(), folderId.ToString()));
        json.Should().Contain("\"moved\":true");
    }

    [Fact]
    public async Task GetDashboardStats_ProjectsScalarSummary()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/stats/dashboard").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                workflowsTotal = 12, workflowsEnabled = 9, machinesTotal = 4, machinesReachable = 3,
                executionsTotal = 1234, last24h = new { total = 50, succeeded = 45, failed = 3, running = 2, cancelled = 0 },
                pendingCount = 1, runningCount = 2, longRunningCount = 0,
                failingWorkflows = Array.Empty<object>(), editLocks = Array.Empty<object>(),
                databaseProvider = "postgres", clusterRole = "Leader",
            }));

        var tools = new TelemetryTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.GetDashboardStats());
        json.Should().Contain("\"workflowsTotal\":12");
        json.Should().Contain("\"machinesReachable\":3");
        json.Should().Contain("postgres");
        json.Should().Contain("Leader");
    }

    [Fact]
    public async Task GetOperationsGraph_ReturnsNodesEdgesRunning()
    {
        using var api = new TestApi();
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        api.Server.Given(Request.Create().WithPath("/api/operations/graph").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                nodes = new[]
                {
                    new { workflowId = parent, name = "Parent", folderId = Guid.Empty, folderPath = "/", isEnabled = true, runningCount = 1, lastStatus = "Running", callFrequency = 3 },
                },
                edges = new[]
                {
                    new { id = "e1", source = parent, target = (Guid?)child, kind = "startWorkflow", refStatus = "Resolved", rawRef = child.ToString(), callCount = 1 },
                },
                running = new[] { new { executionId = Guid.NewGuid(), workflowId = parent, status = "Running", startedAt = DateTime.UtcNow } },
                recent = new[] { new { executionId = Guid.NewGuid(), workflowId = parent, status = "Failed", startedAt = DateTime.UtcNow.AddMinutes(-9), completedAt = DateTime.UtcNow.AddMinutes(-8) } },
                capabilities = new { canCancel = true },
            }));

        var tools = new TelemetryTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.GetOperationsGraph());
        json.Should().Contain("\"name\":\"Parent\"");
        json.Should().Contain("\"refStatus\":\"Resolved\"");
        json.Should().Contain("\"status\":\"Failed\"");
        json.Should().Contain("\"canCancel\":true");
    }

    [Fact]
    public async Task QueryAuditLog_TruncatesDetailsAndReturnsCursor()
    {
        var big = new string('d', 9000);
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/audit").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                items = new[]
                {
                    new { id = Guid.NewGuid(), timestamp = DateTime.UtcNow, userId = (Guid?)null, username = "admin",
                          action = "WORKFLOW_UPDATED", resourceType = "Workflow", resourceId = (Guid?)Guid.NewGuid(),
                          details = big, ipAddress = "127.0.0.1" },
                },
                nextCursor = new { timestamp = DateTime.UtcNow, id = Guid.NewGuid() },
            }));

        var tools = new TelemetryTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.QueryAuditLog(action: "WORKFLOW_UPDATED"));
        json.Should().Contain("WORKFLOW_UPDATED");
        json.Should().Contain("truncated");
        json.Should().Contain("nextCursor");
        json.Length.Should().BeLessThan(big.Length);
    }

    [Fact]
    public async Task UpdateGlobalVariable_ChangingValue_KeepsSecretFlag_NoSilentDemotion()
    {
        var id = Guid.NewGuid();
        using var api = new TestApi();
        // Current global is a SECRET.
        api.Server.Given(Request.Create().WithPath("/api/global-variables").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { id, name = "API_TOKEN", value = "***", isSecret = true, description = "tok",
                      createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, updatedBy = "admin" },
            }));
        api.Server.Given(Request.Create().WithPath($"/api/global-variables/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new SupportingDataTools(api.Client());
        // Change only the value; do NOT pass isSecret → must stay secret.
        await tools.UpdateGlobalVariable(id.ToString(), value: "new-token-value");

        var body = api.Server.LogEntries.Last(e => e.RequestMessage.Method == "PUT").RequestMessage.Body ?? "";
        body.Should().MatchRegex("\"[iI]sSecret\":true");   // secret flag preserved
        body.Should().Contain("new-token-value");
        body.Should().Contain("API_TOKEN");                 // name backfilled from current
    }

    [Fact]
    public async Task UpdateMachine_PartialUpdate_KeepsHostnameCredentialAndTags()
    {
        var id = Guid.NewGuid();
        var cred = Guid.NewGuid();
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath($"/api/machines/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id, name = "WEB01", hostname = "web01.corp", winRmPort = 5986, useSsl = true,
                defaultCredentialId = (Guid?)cred, tags = "web,prod", lastConnectivityCheck = (DateTime?)null,
                isReachable = true, usedByWorkflowCount = 0, recentStepCount = 0, recentFailedStepCount = 0, activeRunCount = 0,
            }));
        api.Server.Given(Request.Create().WithPath($"/api/machines/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var tools = new SupportingDataTools(api.Client());
        // Rename only — everything else must be preserved (not blanked).
        await tools.UpdateMachine(id.ToString(), name: "WEB01-renamed");

        var body = api.Server.LogEntries.Last(e => e.RequestMessage.Method == "PUT").RequestMessage.Body ?? "";
        body.Should().Contain("WEB01-renamed");
        body.Should().Contain("web01.corp");      // hostname preserved
        body.Should().Contain(cred.ToString());    // credential preserved
        body.Should().Contain("web,prod");         // tags preserved
        body.Should().Contain("5986");             // port preserved
    }

    [Fact]
    public async Task GetSupportDiagnostics_TruncatesLargeStrings()
    {
        var big = new string('L', 9000);
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/diagnostics/support-log").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                file = "support.log", lineCount = 1, lines = new[] { big },
            }));

        var tools = new TelemetryTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.GetSupportDiagnostics("log"));
        json.Should().Contain("truncated");
        json.Length.Should().BeLessThan(big.Length);
    }

    [Fact]
    public async Task ListMachines_ProjectsReachabilityAndUsage()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/machines").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { id = Guid.NewGuid(), name = "WEB01", hostname = "web01", winRmPort = 5985, useSsl = false,
                      defaultCredentialId = (Guid?)null, tags = "web", lastConnectivityCheck = (DateTime?)DateTime.UtcNow,
                      isReachable = true, usedByWorkflowCount = 3, recentStepCount = 18, recentFailedStepCount = 1, activeRunCount = 0 },
            }));

        var tools = new SupportingDataTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ListMachines());
        json.Should().Contain("WEB01");
        json.Should().Contain("\"isReachable\":true");
        json.Should().Contain("\"usedByWorkflowCount\":3");
    }
}
