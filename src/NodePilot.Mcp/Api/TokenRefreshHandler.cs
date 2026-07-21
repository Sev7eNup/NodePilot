using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Auth;
using NodePilot.Mcp.Config;

namespace NodePilot.Mcp.Api;

/// <summary>
/// DelegatingHandler that intercepts 401s, attempts a single <c>POST /api/auth/refresh</c>
/// with the current bearer, persists the rotated token back to <see cref="TokenStore"/> and
/// replays the original request once. Copied/adapted from the CLI. Only wired up when the
/// token came from the DPAPI store (a raw env bearer is not refreshable).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TokenRefreshHandler : DelegatingHandler
{
    private readonly TokenStore _tokens;
    private readonly string _profile;
    private bool _refreshAttempted;

    public TokenRefreshHandler(TokenStore tokens, string profile)
    {
        _tokens = tokens;
        _profile = profile;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || _refreshAttempted)
            return response;

        if (request.RequestUri?.AbsolutePath?.EndsWith("/api/auth/refresh", StringComparison.OrdinalIgnoreCase) == true)
            return response;

        var existing = _tokens.Load(_profile);
        if (existing is null
            || !SessionContext.HasSameServerOrigin(existing.Server, request.RequestUri?.AbsoluteUri))
        {
            // The store may have been changed after this client was created. Never use a
            // freshly loaded token unless it is still bound to the request origin.
            return response;
        }

        _refreshAttempted = true;
        response.Dispose();

        using var refreshMsg = new HttpRequestMessage(HttpMethod.Post, new Uri(request.RequestUri!, "/api/auth/refresh"));
        refreshMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", existing.Token);
        // The CLI login opts into a body token via this header; refresh honours it the same way.
        refreshMsg.Headers.Add("X-Auth-Token-Response", "true");
        using var refreshRes = await base.SendAsync(refreshMsg, cancellationToken);
        if (!refreshRes.IsSuccessStatusCode) return await base.SendAsync(request, cancellationToken);

        var rotated = await refreshRes.Content.ReadFromJsonAsync<LoginResponse>(NodePilotApiClient.JsonOptions, cancellationToken);
        if (rotated is null) return await base.SendAsync(request, cancellationToken);

        _tokens.Save(_profile, new StoredSession
        {
            Server = existing.Server,
            Token = rotated.Token,
            Username = rotated.Username,
            UserId = rotated.UserId,
            Role = rotated.Role,
            ExpiresAt = DateTime.UtcNow.AddHours(12),
        });

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rotated.Token);
        return await base.SendAsync(request, cancellationToken);
    }
}
