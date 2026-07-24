using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Audit;
using NodePilot.Data;

namespace NodePilot.Api.Audit;

/// <summary>
/// HTTP-flow <see cref="IAuditWriter"/>. Captures the current user + remote IP from
/// <see cref="IHttpContextAccessor"/> at write time, delegates entry construction
/// (redaction + 4 KiB cap) to <see cref="IAuditStager"/>, persists via the scoped
/// DbContext, and emits the ECS-shaped structured-log line that SIEM forwarders consume.
/// Swallows-and-logs any write failure so the caller's mutation is never blocked by an
/// audit problem.
///
/// <para>
/// Non-HTTP callers (CredentialStore, TriggerOrchestrator, DbAdminController) skip this
/// type and consume <see cref="IAuditStager"/> directly — they own their own DbContext
/// lifetime (background scopes, in-transaction commits) but still flow through the same
/// stager so redaction + cap apply uniformly.
/// </para>
/// </summary>
public class AuditWriter : IAuditWriter
{
    /// <summary>
    /// Allowlist of audit actions that are additionally mirrored into the support log.
    /// Extended by an "outcome=failure" fallthrough rule — every failed audit entry lands
    /// in the support log regardless of whether it's on the allowlist. Deliberately left
    /// out: <c>CREDENTIAL_DECRYPTED</c> (fires N times per workflow run; only its failures
    /// arrive via the outcome fallthrough), <c>TOKEN_REFRESHED</c> (fires every 12h per
    /// user), <c>WORKFLOW_CREATED/UPDATED/DUPLICATED/LOCKED/UNLOCKED/MOVED</c> (routine
    /// editor activity), <c>EXECUTION_*</c> (reported separately by the WorkflowEngine
    /// lifecycle helper, which includes duration + step counts instead of just a resource id).
    /// </summary>
    private static readonly HashSet<string> SupportLogActions = new(StringComparer.Ordinal)
    {
        // Auth
        "LOGIN_SUCCESS", "BREAK_GLASS_LOGIN_SUCCESS", "LOGIN_FAILED", "LOGIN_LOCKED", "LOGOUT",
        // User-Mgmt
        "USER_CREATED", "USER_CREATED_BOOTSTRAP", "USER_DELETED", "USER_ROLE_CHANGED", "USER_BREAK_GLASS_CHANGED",
        "USER_PASSWORD_RESET", "USER_ACTIVATED", "USER_DEACTIVATED",
        "USER_DIRECTORY_ACCESS_REFUSED", "USER_AUTHORIZATION_STALE",
        "USER_EXTERNAL_IDENTITY_RESOLVED",
        "USER_SCIM_PROVISIONED", "USER_SCIM_UPDATED", "USER_SCIM_DEPROVISIONED",
        "SCIM_GROUP_PROVISIONED", "SCIM_GROUP_UPDATED", "SCIM_GROUP_DEPROVISIONED",
        "SCIM_GROUP_REACTIVATED",
        // Workflow Productive-Events
        "WORKFLOW_PUBLISHED", "WORKFLOW_DELETED", "WORKFLOW_FORCE_UNLOCKED",
        // Trigger-Events
        "EXTERNAL_TRIGGER_FIRED", "WEBHOOK_TRIGGERED", "TRIGGER_FIRE_SUPPRESSED",
        // Secrets
        "SECRETS_REENCRYPTED",
    };

    private readonly NodePilotDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuditWriter> _logger;
    private readonly IAuditStager _stager;

    public AuditWriter(
        NodePilotDbContext db,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuditWriter> logger,
        IAuditStager? stager = null)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        // Test ergonomics: callers that instantiate AuditWriter directly (the existing
        // AuditWriterTests) can omit the stager and we fall back to a redaction-less one.
        // Production wiring always supplies a real stager from DI.
        _stager = stager ?? new AuditStager();
    }

    public async Task LogAsync(
        string action,
        string? resourceType = null,
        Guid? resourceId = null,
        string? details = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var actor = ResolveActor();
            var entry = _stager.Build(action, actor, resourceType, resourceId, details);

            _db.AuditLog.Add(entry);
            await _db.SaveChangesAsync(ct);
            NodePilot.Engine.EngineMetrics.AuditWrites.Add(1,
                new KeyValuePair<string, object?>("result", "success"));

            // SIEM forward: emit the audit row as a structured log line so a SIEM that
            // tails the JSON log file sees mutations in real time without polling the DB.
            // The properties below use ECS-standard names (event.action, event.category,
            // user.id, source.ip, audit.id) so out-of-the-box SIEM detection rules
            // (Sigma, Sentinel analytics, Elastic-detection) match without custom field
            // mapping. The redacted details JSON is also forwarded so investigators can
            // pivot from "WORKFLOW_PUBLISHED" to the named workflow without joining back
            // to the AuditLog table.
            var outcome = AuditEventClassification.Outcome(action, entry.Details);
            // Support-log mirror: on the allowlist (Auth/User-Mgmt/Publish/Trigger/Secrets)
            // OR any failure outcome — this way brute-force attempts, failed decryptions,
            // and other `_FAILED/_SUPPRESSED/_REJECTED` actions land in the support log
            // automatically without the allowlist needing to know about them individually.
            var supportLog = SupportLogActions.Contains(action) || outcome == "failure";

            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["support.event_type"] = "AUDIT",
                ["support.message"] = $"{action} user={actor.Username ?? "-"} resource={resourceType ?? "-"}/{resourceId?.ToString() ?? "-"} ip={actor.IpAddress ?? "-"}",
                ["event.action"]   = action,
                ["event.category"] = AuditEventClassification.Category(action),
                ["event.kind"]     = "event",
                ["event.outcome"]  = outcome,
                ["event.dataset"]  = "nodepilot.audit",
                ["event.id"]       = entry.Id.ToString(),
                ["event.original"] = entry.Details,
                ["user.id"]        = actor.UserId?.ToString(),
                ["user.name"]      = actor.Username,
                ["source.ip"]      = actor.IpAddress,
                ["AuditResourceType"] = resourceType,
                ["AuditResourceId"]   = resourceId?.ToString(),
                ["SupportLog"]     = supportLog,
            }))
            {
                if (supportLog)
                {
                    _logger.LogInformation(
                        "AUDIT {Action} user={UserName} resource={ResourceType}/{ResourceId} ip={RemoteIp}",
                        action, actor.Username ?? "-", resourceType ?? "-", resourceId?.ToString() ?? "-", actor.IpAddress ?? "-");
                }
                else
                {
                    _logger.LogInformation(
                        "audit.{Action} resource={ResourceType}/{ResourceId} actor={UserId} ip={RemoteIp}",
                        action, resourceType, resourceId, actor.UserId, actor.IpAddress);
                }
            }
        }
        catch (Exception ex)
        {
            // Never let an audit write failure abort the triggering mutation. A missing row
            // shows up in the AuditLog gap — a lost POST/PUT would be worse. The metric is
            // the only operational signal that the audit write silently dropped.
            NodePilot.Engine.EngineMetrics.AuditWrites.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("error_class", ex.GetType().Name));
            _logger.LogError(ex, "Audit write failed (action={Action} resource={ResourceType}/{ResourceId})",
                action, resourceType, resourceId);
        }
        finally
        {
            NodePilot.Engine.EngineMetrics.AuditWriteDuration.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Resolves the current HTTP actor, or the system actor outside a request.</summary>
    private AuditActor ResolveActor()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null) return AuditActor.System;

        Guid? userId = null;
        if (Guid.TryParse(ctx.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed))
            userId = parsed;

        var username = ctx.User?.FindFirstValue(ClaimTypes.Name);
        var remoteIp = ctx.Connection?.RemoteIpAddress?.ToString();
        return new AuditActor(userId, username, remoteIp);
    }
}
