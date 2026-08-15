using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Auth;
using NodePilot.Mcp.Config;
using NodePilot.Core.Clients;

namespace NodePilot.Mcp.Api;

/// <summary>
/// Keeps a DPAPI-backed MCP bearer credential current. A still-valid token is rotated
/// shortly before its absolute expiry, concurrent tool calls share one refresh, and every
/// request reads the latest profile-bound token before it is sent. Raw environment bearers
/// bypass this handler entirely.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TokenRefreshHandler : DelegatingHandler
{
    private readonly TokenStore _tokens;
    private readonly string _profile;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string? _lastNearExpiryRotation;
    private long _lastNearExpiryRotationAtUnixMs;
    private string? _transientRefreshFailureToken;
    private DateTimeOffset _transientRefreshRetryAfter;

    public TokenRefreshHandler(TokenStore tokens, string profile, TimeProvider? timeProvider = null)
    {
        _tokens = tokens;
        _profile = profile;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath?.EndsWith("/api/auth/refresh", StringComparison.OrdinalIgnoreCase) == true)
            return await base.SendAsync(request, cancellationToken);

        var existing = LoadForRequest(request);
        if (existing is null)
            return await base.SendAsync(request, cancellationToken);

        if (IsExpired(existing))
        {
            existing = await RevalidateExpiredSessionAsync(
                existing.Token, request.RequestUri!, cancellationToken);
            if (existing is null)
                return ReauthenticationRequired(request);
        }

        if (NeedsProactiveRefresh(existing))
        {
            existing = await RefreshSingleFlightAsync(
                existing.Token, request.RequestUri!, proactive: true, cancellationToken);
            if (existing is null)
                return ReauthenticationRequired(request);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", existing.Token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        var recovered = await RefreshSingleFlightAsync(
            existing.Token, request.RequestUri!, proactive: false, cancellationToken);
        if (recovered is null || string.Equals(recovered.Token, existing.Token, StringComparison.Ordinal))
            return response;

        response.Dispose();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", recovered.Token);
        return await base.SendAsync(request, cancellationToken);
    }

    private StoredSession? LoadForRequest(HttpRequestMessage request)
    {
        var session = _tokens.Load(_profile);
        return session is not null
               && SessionContext.HasSameServerOrigin(session.Server, request.RequestUri?.AbsoluteUri)
            ? session
            : null;
    }

    private bool IsExpired(StoredSession session)
        => session.ExpiresAt <= _timeProvider.GetUtcNow();

    private bool NeedsProactiveRefresh(StoredSession session)
    {
        var now = _timeProvider.GetUtcNow();
        return session.ExpiresAt - now <= ClientSessionSecurity.ProactiveRefreshLeadTime
               && !WasRecentlyRotated(session, now);
    }

    private async Task<StoredSession?> RefreshSingleFlightAsync(
        string observedToken,
        Uri requestUri,
        bool proactive,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            using var profileLock = await ClientSessionFileCoordinator.AcquireRefreshLockAsync(
                _tokens.PathFor(_profile), requestUri.AbsoluteUri, cancellationToken);
            var current = _tokens.Load(_profile);
            if (current is null || !SessionContext.HasSameServerOrigin(current.Server, requestUri.AbsoluteUri))
                return null;

            // A CLI or another MCP process already rotated the single-use token while this
            // request waited for the shared profile lease. Always reuse the winner's generation.
            if (!string.Equals(current.Token, observedToken, StringComparison.Ordinal))
            {
                if (IsExpired(current))
                    return ClearRejectedSession(current.Token, requestUri);

                MarkNearExpiryRotation(current);
                return current;
            }
            if (IsExpired(current))
            {
                _tokens.DeleteIfCurrent(_profile, current.Token);
                return null;
            }
            if (proactive)
            {
                if (WasRecentlyRotated(current, _timeProvider.GetUtcNow())
                    || IsInTransientFailureCooldown(current.Token))
                {
                    return current;
                }
            }

            using var refreshMsg = new HttpRequestMessage(
                HttpMethod.Post, new Uri(requestUri, "/api/auth/refresh"));
            refreshMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.Token);
            using var refreshRes = await base.SendAsync(refreshMsg, cancellationToken);
            if (refreshRes.StatusCode == HttpStatusCode.Unauthorized)
                return ClearRejectedSession(current.Token, requestUri);
            if (!refreshRes.IsSuccessStatusCode)
            {
                if (proactive && IsTransientRefreshFailure(refreshRes.StatusCode))
                    StartTransientFailureCooldown(current.Token);
                return LoadUsableSession(requestUri);
            }

            var rotated = await refreshRes.Content.ReadFromJsonAsync<LoginResponse>(
                NodePilotApiClient.JsonOptions, cancellationToken);
            if (rotated is null
                || !ClientSessionSecurity.TryResolveExpiration(
                    rotated.Token, rotated.ExpiresAt, out var rotatedExpiresAt)
                || rotatedExpiresAt <= _timeProvider.GetUtcNow())
            {
                return ClearRejectedSession(current.Token, requestUri);
            }

            var updated = new StoredSession
            {
                Server = current.Server,
                Token = rotated.Token,
                Username = rotated.Username,
                UserId = rotated.UserId,
                Role = rotated.Role,
                ExpiresAt = rotatedExpiresAt,
            };
            if (!_tokens.TrySaveIfCurrent(_profile, current.Token, updated))
                return LoadUsableSession(requestUri);

            MarkNearExpiryRotation(updated);
            return updated;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<StoredSession?> RevalidateExpiredSessionAsync(
        string observedToken,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            using var profileLock = await ClientSessionFileCoordinator.AcquireRefreshLockAsync(
                _tokens.PathFor(_profile), requestUri.AbsoluteUri, cancellationToken);
            var current = _tokens.Load(_profile);
            if (current is null
                || !SessionContext.HasSameServerOrigin(current.Server, requestUri.AbsoluteUri))
            {
                return null;
            }

            if (!IsExpired(current))
            {
                if (!string.Equals(current.Token, observedToken, StringComparison.Ordinal))
                    MarkNearExpiryRotation(current);
                return current;
            }

            _tokens.DeleteIfCurrent(_profile, current.Token);
            return LoadUsableSession(requestUri);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private StoredSession? ClearRejectedSession(string rejectedToken, Uri requestUri)
    {
        var latest = _tokens.Load(_profile);
        if (latest is null
            || !SessionContext.HasSameServerOrigin(latest.Server, requestUri.AbsoluteUri))
        {
            return null;
        }

        if (!string.Equals(latest.Token, rejectedToken, StringComparison.Ordinal)
            && !IsExpired(latest))
        {
            MarkNearExpiryRotation(latest);
            return latest;
        }

        _tokens.DeleteIfCurrent(_profile, latest.Token);
        return LoadUsableSession(requestUri);
    }

    private StoredSession? LoadUsableSession(Uri requestUri)
    {
        var latest = _tokens.Load(_profile);
        if (latest is null
            || !SessionContext.HasSameServerOrigin(latest.Server, requestUri.AbsoluteUri))
        {
            return null;
        }

        if (!IsExpired(latest))
            return latest;

        _tokens.DeleteIfCurrent(_profile, latest.Token);
        return null;
    }

    private void MarkNearExpiryRotation(StoredSession session)
    {
        var now = _timeProvider.GetUtcNow();
        if (session.ExpiresAt - now <= ClientSessionSecurity.ProactiveRefreshLeadTime)
        {
            Volatile.Write(ref _lastNearExpiryRotationAtUnixMs, now.ToUnixTimeMilliseconds());
            Volatile.Write(ref _lastNearExpiryRotation, session.Token);
        }
    }

    private bool WasRecentlyRotated(StoredSession session, DateTimeOffset now)
        => ClientSessionSecurity.WasIssuedRecently(
               session.Token, now, ClientSessionSecurity.SuccessfulRefreshDeduplicationWindow)
           || WasMarkedRecently(session.Token, now);

    private bool WasMarkedRecently(string token, DateTimeOffset now)
    {
        if (!string.Equals(
                Volatile.Read(ref _lastNearExpiryRotation), token, StringComparison.Ordinal))
        {
            return false;
        }

        var ageMilliseconds = now.ToUnixTimeMilliseconds()
                              - Volatile.Read(ref _lastNearExpiryRotationAtUnixMs);
        return ageMilliseconds >= -TimeSpan.FromMinutes(1).TotalMilliseconds
               && ageMilliseconds
               < ClientSessionSecurity.SuccessfulRefreshDeduplicationWindow.TotalMilliseconds;
    }

    private bool IsInTransientFailureCooldown(string token)
    {
        if (!string.Equals(_transientRefreshFailureToken, token, StringComparison.Ordinal))
            return false;
        if (_timeProvider.GetUtcNow() < _transientRefreshRetryAfter)
            return true;

        _transientRefreshFailureToken = null;
        _transientRefreshRetryAfter = default;
        return false;
    }

    private void StartTransientFailureCooldown(string token)
    {
        _transientRefreshFailureToken = token;
        _transientRefreshRetryAfter =
            _timeProvider.GetUtcNow() + ClientSessionSecurity.TransientRefreshFailureCooldown;
    }

    private static bool IsTransientRefreshFailure(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
           || (int)statusCode >= 500;

    private static HttpResponseMessage ReauthenticationRequired(HttpRequestMessage request)
        => new(HttpStatusCode.Unauthorized)
        {
            RequestMessage = request,
            ReasonPhrase = "Authentication session expired",
        };
}
