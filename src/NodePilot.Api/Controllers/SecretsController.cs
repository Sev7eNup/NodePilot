using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Admin-only operations on the secret-protector layer. Today exposes a single endpoint
/// — the bulk re-encrypt sweep — used after rotating <c>Secrets:Provider</c> or the
/// AES-GCM master key. Without this sweep, ruhende secrets keep their old-format
/// ciphertexts until the next time something happens to read them; with the sweep, the
/// transition is deterministic and operators can drop the legacy-provider config in the
/// follow-up restart.
/// </summary>
[ApiController]
[Route("api/secrets")]
[Authorize(Roles = "Admin")]
public class SecretsController : ControllerBase
{
    private readonly ICredentialStore _credentials;
    private readonly IGlobalVariableStore _globals;
    private readonly IAuditWriter _audit;

    public SecretsController(
        ICredentialStore credentials,
        IGlobalVariableStore globals,
        IAuditWriter audit)
    {
        _credentials = credentials;
        _globals = globals;
        _audit = audit;
    }

    /// <summary>
    /// Re-encrypts every credential password and secret-flagged global variable with
    /// the currently active <see cref="ISecretProtector"/>. Use after rotating the
    /// AES-GCM master key or migrating from DPAPI to AES-GCM (with <c>Secrets:LegacyProvider</c>
    /// configured for the fallback-read path during the rotation window).
    /// <para>
    /// Returns 200 OK with <c>partialSuccess=false</c> when every row converted cleanly.
    /// Returns 207 Multi-Status with <c>partialSuccess=true</c> when at least one row
    /// could not be decrypted — the response body lists the affected names + the failure
    /// reason class so the operator can re-enter them by hand. The rewritten rows are
    /// committed regardless: a partial sweep still moves the deployment forward.
    /// </para>
    /// </summary>
    [HttpPost("reencrypt")]
    public async Task<ActionResult<ReencryptResult>> Reencrypt(CancellationToken ct)
    {
        var creds = await _credentials.ReencryptAllCredentialsAsync(ct);
        var globals = await _globals.ReencryptAllSecretsAsync(ct);

        var partial = creds.Skipped > 0 || globals.Skipped > 0;
        var result = new ReencryptResult(
            CredentialsRewritten: creds.Rewritten,
            CredentialsSkipped: creds.Skipped,
            CredentialSkipDetails: creds.SkippedDetails,
            GlobalSecretsRewritten: globals.Rewritten,
            GlobalSecretsSkipped: globals.Skipped,
            GlobalSecretSkipDetails: globals.SkippedDetails,
            PartialSuccess: partial);

        await _audit.LogAsync(AuditActions.SecretsReencrypted, "Secrets", null,
            AuditDetails.Json(
                ("credentialsRewritten", creds.Rewritten),
                ("credentialsSkipped", creds.Skipped),
                ("globalsRewritten", globals.Rewritten),
                ("globalsSkipped", globals.Skipped),
                ("partialSuccess", partial)),
            ct);

        if (partial)
        {
            // 207 Multi-Status communicates "we did some of what you asked, here's what
            // didn't happen" — a deliberate choice over 200 (which suggests fully clean).
            // Operators reading the status line in CI / Ansible see the difference and
            // can branch on it without parsing the body.
            return StatusCode(StatusCodes.Status207MultiStatus, result);
        }
        return Ok(result);
    }
}
