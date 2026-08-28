using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Admin-only operations on the secret-protector layer. Currently exposes one endpoint,
/// the bulk re-encrypt sweep, used after rotating <c>Secrets:Provider</c> or the AES-GCM
/// master key. Without it, secrets at rest keep their old ciphertext format until
/// something reads them; the sweep makes the transition deterministic so operators can
/// drop the legacy-provider config afterward.
/// </summary>
[ApiController]
[Route("api/secrets")]
[Authorize(Roles = "Admin")]
public class SecretsController : ControllerBase
{
    private readonly ICredentialStore _credentials;
    private readonly IGlobalVariableStore _globals;
    private readonly NodePilotDbContext _db;
    private readonly WorkflowVersionDefinitionProtector _workflowVersions;
    private readonly IAuditWriter _audit;

    public SecretsController(
        ICredentialStore credentials,
        IGlobalVariableStore globals,
        NodePilotDbContext db,
        WorkflowVersionDefinitionProtector workflowVersions,
        IAuditWriter audit)
    {
        _credentials = credentials;
        _globals = globals;
        _db = db;
        _workflowVersions = workflowVersions;
        _audit = audit;
    }

    /// <summary>
    /// Re-encrypts every credential password and secret-flagged global variable with
    /// the active <see cref="ISecretProtector"/>. Use after rotating the AES-GCM master
    /// key or migrating from DPAPI to AES-GCM (set <c>Secrets:LegacyProvider</c> for the
    /// fallback-read path during the rotation window).
    /// <para>
    /// Returns 200 OK when every row converts cleanly. Returns 207 Multi-Status with
    /// <c>partialSuccess=true</c> when some rows could not be decrypted; the body lists
    /// the affected names and failure reason so the operator can re-enter them by hand.
    /// Rewritten rows are always committed, even on a partial result.
    /// </para>
    /// </summary>
    [HttpPost("reencrypt")]
    public async Task<ActionResult<ReencryptResult>> Reencrypt(CancellationToken ct)
    {
        var creds = await _credentials.ReencryptAllCredentialsAsync(ct);
        var globals = await _globals.ReencryptAllSecretsAsync(ct);
        var versions = await _workflowVersions.ReencryptAllAsync(_db, ct);

        var partial = creds.Skipped > 0 || globals.Skipped > 0 || versions.Skipped > 0;
        var result = new ReencryptResult(
            CredentialsRewritten: creds.Rewritten,
            CredentialsSkipped: creds.Skipped,
            CredentialSkipDetails: creds.SkippedDetails,
            GlobalSecretsRewritten: globals.Rewritten,
            GlobalSecretsSkipped: globals.Skipped,
            GlobalSecretSkipDetails: globals.SkippedDetails,
            WorkflowVersionsRewritten: versions.Rewritten,
            WorkflowVersionsSkipped: versions.Skipped,
            WorkflowVersionSkipDetails: versions.SkippedDetails,
            PartialSuccess: partial);

        await _audit.LogAsync(AuditActions.SecretsReencrypted, "Secrets", null,
            AuditDetails.Json(
                ("credentialsRewritten", creds.Rewritten),
                ("credentialsSkipped", creds.Skipped),
                ("globalsRewritten", globals.Rewritten),
                ("globalsSkipped", globals.Skipped),
                ("workflowVersionsRewritten", versions.Rewritten),
                ("workflowVersionsSkipped", versions.Skipped),
                ("partialSuccess", partial)),
            ct);

        if (partial)
        {
            // 207 signals a partial result instead of 200, so CI/Ansible callers can
            // branch on the status code alone without parsing the response body.
            return StatusCode(StatusCodes.Status207MultiStatus, result);
        }
        return Ok(result);
    }
}
