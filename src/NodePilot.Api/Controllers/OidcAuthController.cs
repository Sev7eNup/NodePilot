using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NodePilot.Api.Hosting;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Audit;

namespace NodePilot.Api.Controllers;

/// <summary>Enterprise OIDC Authorization Code + PKCE browser flow.</summary>
[ApiController]
[Route("api/auth/oidc")]
public sealed class OidcAuthController(
    IOptions<EnterpriseOidcOptions> options,
    ActiveAuthenticationConfiguration activeAuthentication,
    OidcIdentityMapper mapper,
    IAuthSessionIssuer sessionIssuer,
    IAuditWriter audit) : ControllerBase
{
    private const string ReturnUrlItem = "nodepilot:return_url";

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login([FromQuery] string? returnUrl = null)
    {
        if (!activeAuthentication.OidcEnabled || !options.Value.Enabled) return NotFound();

        var safeReturnUrl = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : "/";
        var properties = new AuthenticationProperties
        {
            RedirectUri = Url.ActionLink(nameof(Callback)) ?? "/api/auth/oidc/callback",
        };
        properties.Items[ReturnUrlItem] = safeReturnUrl;
        return Challenge(properties, AuthenticationSetup.OidcChallengeSchemeName);
    }

    /// <summary>
    /// Post-middleware landing endpoint. The OpenIdConnect handler has already validated
    /// authorization code, state, nonce, issuer, audience and signature before creating the
    /// short-lived external cookie consumed here.
    /// </summary>
    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(CancellationToken ct)
    {
        if (!activeAuthentication.OidcEnabled || !options.Value.Enabled) return NotFound();

        var external = await HttpContext.AuthenticateAsync(AuthenticationSetup.OidcExternalSchemeName);
        if (!external.Succeeded || external.Principal is null)
        {
            await audit.LogAsync(
                AuditActions.LoginFailed,
                "User",
                null,
                AuditDetails.Json(
                    ("source", "Oidc"),
                    ("reason", "oidc_external_ticket_invalid")),
                ct);
            return RedirectFailure("authentication_failed");
        }

        var mapped = await mapper.MapAsync(external.Principal, ct);
        if (!mapped.Succeeded)
        {
            await audit.LogAsync(
                NodePilot.Core.Audit.AuditActions.LoginFailed,
                "User",
                null,
                AuditDetails.Json(("source", "Oidc"), ("reason", mapped.AuditReason)),
                ct);
            await HttpContext.SignOutAsync(AuthenticationSetup.OidcExternalSchemeName);
            return RedirectFailure(mapped.Failure == OidcMapFailure.AccessNotAssigned
                ? "access_not_assigned"
                : "authentication_failed");
        }

        await sessionIssuer.IssueAsync(mapped.User!, AuthSource.Oidc, HttpContext, ct);
        var returnUrl = external.Properties?.Items.TryGetValue(ReturnUrlItem, out var requested) == true
                        && !string.IsNullOrEmpty(requested)
                        && Url.IsLocalUrl(requested)
            ? requested
            : "/";
        await HttpContext.SignOutAsync(AuthenticationSetup.OidcExternalSchemeName);
        return LocalRedirect(returnUrl);
    }

    private RedirectResult RedirectFailure(string code)
        => Redirect($"/login?oidcError={Uri.EscapeDataString(code)}");
}
