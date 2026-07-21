using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;

namespace NodePilot.Cli.Api;

/// <summary>
/// DelegatingHandler that intercepts 401 responses, attempts a single
/// <c>POST /api/auth/refresh</c> with the current bearer token, persists the rotated
/// token to <see cref="TokenStore"/> and replays the original request once. Any second
/// 401 surfaces as a normal <see cref="ApiException"/> so the command can prompt the
/// user to re-login.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TokenRefreshHandler : DelegatingHandler
{
    private readonly TokenStore _tokens;
    private readonly string _profile;
    private readonly Action<string>? _onTokenRefreshed;
    private bool _refreshAttempted;

    public TokenRefreshHandler(TokenStore tokens, string profile, Action<string>? onTokenRefreshed = null)
    {
        _tokens = tokens;
        _profile = profile;
        _onTokenRefreshed = onTokenRefreshed;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || _refreshAttempted)
            return response;

        // The refresh endpoint itself returning 401 must not loop.
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

        // Build a refresh request reusing the bearer header from the original request.
        using var refreshMsg = new HttpRequestMessage(HttpMethod.Post,
            new Uri(request.RequestUri!, "/api/auth/refresh"));
        refreshMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", existing.Token);
        using var refreshRes = await base.SendAsync(refreshMsg, cancellationToken);
        if (!refreshRes.IsSuccessStatusCode) return await base.SendAsync(request, cancellationToken);

        var rotated = await refreshRes.Content.ReadFromJsonAsync<LoginResponse>(NodePilotApiClient.JsonOptions, cancellationToken);
        if (rotated is null) return await base.SendAsync(request, cancellationToken);

        var updated = new StoredSession
        {
            Server = existing.Server,
            Token = rotated.Token,
            Username = rotated.Username,
            UserId = rotated.UserId,
            Role = rotated.Role,
            ExpiresAt = DateTime.UtcNow.AddHours(12),
        };
        _tokens.Save(_profile, updated);
        _onTokenRefreshed?.Invoke(rotated.Token);

        // Swap bearer header on the original request and replay.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rotated.Token);
        return await base.SendAsync(request, cancellationToken);
    }
}
