using NodePilot.Core.Models;

namespace NodePilot.Api.Security;

/// <summary>
/// Issues a NodePilot session for an already-authenticated user — independent of
/// <i>how</i> the user authenticated (local BCrypt, LDAP-bind, Windows-Negotiate). Mints
/// the JWT, sets the auth + CSRF cookies on the current response, and writes the
/// LOGIN_SUCCESS audit row with the originating <see cref="AuthSource"/>.
/// <para>
/// Extracted from the previously-private <c>AuthController.SetAuthCookies</c> +
/// <c>AuthController.GenerateJwtToken</c> pair so LDAP and Windows-SSO endpoints can
/// reuse the exact same session-establishment logic instead of re-implementing
/// JWT-mint + cookie-shape + audit-write three times.
/// </para>
/// </summary>
public interface IAuthSessionIssuer
{
    Task<IssuedSession> IssueAsync(
        User user,
        AuthSource source,
        HttpContext httpContext,
        CancellationToken ct);

    /// <summary>
    /// Token rotation on <c>POST /api/auth/refresh</c>: mint a fresh JWT (carrying the
    /// same compact baseline claims as <see cref="IssueAsync"/>) and set the
    /// <c>np_auth</c> + <c>np_csrf</c> cookies. Directory groups remain server-side and
    /// are never copied into the browser cookie. Does
    /// <b>not</b> write a <c>LOGIN_SUCCESS</c> audit row — refresh is a session-rotation,
    /// not a fresh login; the controller writes its own rotation metric.
    /// </summary>
    Task<IssuedSession> RefreshAsync(
        User user,
        HttpContext httpContext,
        CancellationToken ct);
}

/// <summary>How the user authenticated. Surfaced in the LOGIN_SUCCESS audit detail
/// so investigators can tell apart "local password" from "AD-bind" from "Kerberos".</summary>
public enum AuthSource
{
    /// <summary>BCrypt-verified local password.</summary>
    Local = 0,
    /// <summary>LDAP simple-bind against a configured DC.</summary>
    Ldap = 1,
    /// <summary>Negotiate / Kerberos via the explicit <c>/api/auth/windows</c> endpoint.</summary>
    Windows = 2,
    /// <summary>OIDC Authorization Code flow via an enterprise identity provider.</summary>
    Oidc = 3,
}

/// <summary>Result of a successful session issue. The token is also persisted into the
/// httpOnly cookie on the response; callers that need the bearer-string for tests or
/// API responses can read it from <see cref="Token"/>.</summary>
public sealed record IssuedSession(string Token, Guid UserId, DateTimeOffset ExpiresAt);
