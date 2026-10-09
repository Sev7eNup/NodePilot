using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Dtos;
using NodePilot.Api.Configuration;
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
    private readonly ISecretProtector _protector;
    private readonly RuntimeOverridesWriter _runtimeSettings;

    public SecretsController(
        ICredentialStore credentials,
        IGlobalVariableStore globals,
        NodePilotDbContext db,
        WorkflowVersionDefinitionProtector workflowVersions,
        IAuditWriter audit,
        ISecretProtector protector,
        RuntimeOverridesWriter runtimeSettings)
    {
        _credentials = credentials;
        _globals = globals;
        _db = db;
        _workflowVersions = workflowVersions;
        _audit = audit;
        _protector = protector;
        _runtimeSettings = runtimeSettings;
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
        var mcp = await DatabaseSecretRotation.ReencryptAgentMcpAsync(_db, _protector, ct);
        var routes = await DatabaseSecretRotation.ReencryptNotificationRoutesAsync(_db, _protector, ct);
        var parameters = await DatabaseSecretRotation.ReencryptDispatchParametersAsync(_db, _protector, ct);
        var runtime = _runtimeSettings.ReencryptSecrets(_protector, ct);

        var partial = creds.Skipped > 0 || globals.Skipped > 0 || versions.Skipped > 0 || mcp.Skipped > 0
            || routes.Skipped > 0 || parameters.Skipped > 0 || runtime.Skipped > 0;
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
            PartialSuccess: partial)
        {
            AgentMcpSecretsRewritten = mcp.Rewritten, AgentMcpSecretsSkipped = mcp.Skipped, AgentMcpSecretSkipDetails = mcp.SkippedDetails,
            NotificationRoutesRewritten = routes.Rewritten, NotificationRoutesSkipped = routes.Skipped, NotificationRouteSkipDetails = routes.SkippedDetails,
            DispatchParametersRewritten = parameters.Rewritten, DispatchParametersSkipped = parameters.Skipped, DispatchParameterSkipDetails = parameters.SkippedDetails,
            RuntimeSettingsFilesRewritten = runtime.Rewritten, RuntimeSettingsFilesSkipped = runtime.Skipped, RuntimeSettingsFileSkipDetails = runtime.SkippedDetails,
        };

        await _audit.LogAsync(AuditActions.SecretsReencrypted, "Secrets", null,
            AuditDetails.Json(
                ("credentialsRewritten", creds.Rewritten),
                ("credentialsSkipped", creds.Skipped),
                ("globalsRewritten", globals.Rewritten),
                ("globalsSkipped", globals.Skipped),
                ("workflowVersionsRewritten", versions.Rewritten),
                ("workflowVersionsSkipped", versions.Skipped),
                ("agentMcpSecretsRewritten", mcp.Rewritten),
                ("agentMcpSecretsSkipped", mcp.Skipped),
                ("notificationRoutesRewritten", routes.Rewritten),
                ("notificationRoutesSkipped", routes.Skipped),
                ("dispatchParametersRewritten", parameters.Rewritten),
                ("dispatchParametersSkipped", parameters.Skipped),
                ("runtimeSettingsFilesRewritten", runtime.Rewritten),
                ("runtimeSettingsFilesSkipped", runtime.Skipped),
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
