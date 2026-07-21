using NodePilot.Mcp.Api;
using NodePilot.Mcp.Auth;
using NodePilot.Mcp.Config;
using WireMock.Server;

namespace NodePilot.Mcp.Tests.Infra;

/// <summary>
/// Spins up a WireMock server that mocks the NodePilot REST API and hands out a
/// <see cref="NodePilotApiClient"/> pointed at it. Direct tool-method tests use this to
/// exercise our logic without a real backend (and without spawning the MCP process).
/// </summary>
public sealed class TestApi : IDisposable
{
    public WireMockServer Server { get; } = WireMockServer.Start();

    public string Url => Server.Url!;

    public NodePilotApiClient Client(string? token = "test-token")
    {
        var http = new HttpClient { BaseAddress = new Uri(Url.EndsWith('/') ? Url : Url + "/") };
        var client = new NodePilotApiClient(http) { Session = new SessionContext(Url, "default", token, false) };
        if (token is not null) client.BearerToken = token;
        return client;
    }

    /// <summary>An <see cref="McpServerConfig"/> backed by a throwaway config dir (no real %APPDATA%).</summary>
    public static McpServerConfig Config()
    {
        var dir = Path.Combine(Path.GetTempPath(), "np-mcp-test-" + Guid.NewGuid().ToString("N"));
        return new McpServerConfig(new ConfigStore(dir), new TokenStore(dir));
    }

    /// <summary>A full WorkflowResponse-shaped body for WireMock stubs.</summary>
    public static object WorkflowResponse(Guid id, string name, string definitionJson, bool enabled = true) => new
    {
        id, name, description = (string?)null, definitionJson, version = 1, isEnabled = enabled,
        createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, createdBy = "admin", updatedBy = (string?)null,
        activityCount = 1, triggerTypes = new[] { "manualTrigger" }, lastExecution = (object?)null,
        successCount = 0, totalCount = 0, avgDurationMs = (double?)null,
        checkedOutByUserId = (Guid?)null, checkedOutByUserName = (string?)null, checkedOutAt = (DateTime?)null,
    };

    public void Dispose()
    {
        Server.Stop();
        Server.Dispose();
    }
}
