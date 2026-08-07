using System.Security.Claims;
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
            // Delegated to AuditEventForwarder — the ONE implementation of the ECS scope
            // shape and the support-log allowlist, shared with all background/atomic audit
            // call sites (this class previously carried a diverging inline copy of both).
            AuditEventForwarder.ForwardCommitted(_logger, entry);
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
