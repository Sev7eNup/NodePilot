namespace NodePilot.Api.Dtos;

/// <summary>
/// Login payload. The <c>ToString</c> override masks the password so that any accidental
/// structured-logging call that destructures this record (e.g. <c>_logger.Log("req={Req}", req)</c>,
/// or a future <c>UseSerilogRequestLogging</c> middleware that captures bodies) cannot leak it.
/// </summary>
public record LoginRequest(string Username, string Password)
{
    public override string ToString() => $"LoginRequest {{ Username = {Username}, Password = *** }}";
}

/// <summary>
/// Programmatic-caller login response — carries the JWT in the body so CLI / API / load-test
/// clients can use it as a <c>Authorization: Bearer</c> credential. Only ever returned to a
/// caller that proved it is non-browser (see <c>AuthController</c>: an explicit opt-in header
/// on the password-gated login paths, or a real Bearer header on refresh). Browser flows get
/// <see cref="AuthIdentityResponse"/> instead.
/// </summary>
public record LoginResponse(string Token, Guid UserId, string Username, string Role);

/// <summary>
/// Browser-facing auth response (audit H-5 completion). Carries only the caller's identity —
/// never the JWT. For browser flows the token lives exclusively in the httpOnly <c>np_auth</c>
/// cookie, so a future XSS has no endpoint that hands it a portable, off-host bearer token.
/// Serialises to <c>{ userId, username, role }</c> — the exact fields the SPA auth store reads.
/// </summary>
public record AuthIdentityResponse(Guid UserId, string Username, string Role);

/// <summary>
/// Anonymous /api/auth/methods response — tells the LoginPage which auth tiles to render.
/// Local reflects <c>LocalLoginMode</c>; LDAP, Windows and OIDC reflect the immutable
/// process-start configuration. The password form remains visible when LDAP is active.
/// </summary>
public record AuthMethodsResponse(
    bool Local,
    bool Ldap,
    bool Windows,
    string? WindowsEndpoint,
    bool Oidc = false,
    string? OidcEndpoint = null,
    string? OidcDisplayName = null);
