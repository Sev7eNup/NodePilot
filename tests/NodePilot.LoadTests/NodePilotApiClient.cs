using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NodePilot.LoadTests;

/// <summary>
/// Thin wrapper over the NodePilot REST API for seeding workflows, triggering executions,
/// and polling for terminal status. Only covers what the load tests need.
/// </summary>
public class NodePilotApiClient
{
    private readonly HttpClient _http;
    private string? _token;

    public NodePilotApiClient(string baseUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    public HttpClient Http => _http;
    public string? Token => _token;

    public async Task LoginAsync(string username, string password, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = JsonContent.Create(new { username, password }),
        };
        // Bearer client: opt in to receiving the JWT in the response body (the browser SPA
        // does not, and reads it from the httpOnly cookie instead).
        msg.Headers.Add("X-Auth-Token-Response", "true");
        var resp = await _http.SendAsync(msg, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<LoginBody>(cancellationToken: ct)
                   ?? throw new InvalidOperationException("Empty login response");
        _token = body.Token;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    public async Task<Guid> CreateWorkflowAsync(string name, string definitionJson, CancellationToken ct = default)
    {
        // Mirror of CreateWorkflowRequest(Name, Description, DefinitionJson)
        var req = new { name, description = (string?)null, definitionJson };
        var resp = await _http.PostAsJsonAsync("api/workflows", req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Triggers a workflow execution. Returns the execution id extracted from the 202
    /// response body. The engine runs the workflow asynchronously — caller must poll
    /// <see cref="PollTerminalAsync"/> to learn the outcome.
    /// </summary>
    public async Task<Guid> ExecuteAsync(Guid workflowId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"api/workflows/{workflowId}/execute", new { parameters = (object?)null }, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Polls /api/executions/{id} until the status is terminal (Succeeded/Failed/Cancelled)
    /// or the deadline elapses. Returns the final status string or "Timeout" on deadline.
    /// </summary>
    public async Task<string> PollTerminalAsync(Guid executionId, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var delay = TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var resp = await _http.GetAsync($"api/executions/{executionId}", ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var status = doc.RootElement.GetProperty("status").GetString() ?? "Unknown";
                if (status is "Succeeded" or "Failed" or "Cancelled")
                    return status;
            }
            await Task.Delay(delay, ct);
            if (delay < TimeSpan.FromSeconds(2)) delay += TimeSpan.FromMilliseconds(100);
        }
        return "Timeout";
    }

    public async Task<int> CountRunningExecutionsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("api/executions", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        int count = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var status = e.GetProperty("status").GetString();
            if (status == "Running") count++;
        }
        return count;
    }

    private sealed record LoginBody(string Token, string Username, string Role);
}
