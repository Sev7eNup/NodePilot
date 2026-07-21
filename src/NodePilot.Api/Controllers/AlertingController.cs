using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Conditions;
using NodePilot.Engine.Notifications;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Admin/Operator-managed alerting rules: "when an event of these types matches this filter, deliver
/// via these routes". Read is Admin/Operator; create/update/delete + test-fire is Admin-only — mirrors
/// <see cref="MaintenanceWindowsController"/>. Route secrets are write-or-keep (responses redact to the
/// unchanged-sentinel, never the cipher). The dispatcher (<c>NotificationDispatcher</c>) picks up rule
/// changes on its next pass — no inline snapshot needed (it reads enabled rules each pass).
/// </summary>
[ApiController]
[Route("api/alerting")]
[Authorize(Roles = "Admin,Operator")]
public class AlertingController : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, ActivityResult> EmptyResults = new Dictionary<string, ActivityResult>();

    // Upper bound on throttle windows (30 days). Kept well under the notification retention floor so a
    // stale-suppression prune can never wipe an still-active cooldown row (see NotificationRetentionService).
    private const int MaxThrottleMinutes = 43_200;

    private readonly INotificationRuleStore _store;
    private readonly NodePilotDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationSink> _sinks;

    public AlertingController(
        INotificationRuleStore store,
        NodePilotDbContext db,
        IAuditWriter audit,
        IEnumerable<INotificationSink> sinks)
    {
        _store = store;
        _db = db;
        _audit = audit;
        var map = new Dictionary<NotificationChannel, INotificationSink>();
        foreach (var s in sinks) map[s.Channel] = s;
        _sinks = map;
    }

    [HttpGet("rules")]
    public async Task<ActionResult<List<NotificationRuleResponse>>> GetAll(CancellationToken ct)
        // Custom rules only — system policies live under /api/alerting/system and must never surface here (ADR 0008).
        => Ok((await _store.GetAllByKindAsync(NotificationRuleKind.Custom, ct)).Select(Project).ToList());

    [HttpGet("catalog")]
    public ActionResult<AlertingCatalogResponse> GetCatalog()
    {
        var eventTypes = NotificationRuleSemantics.SupportedEventTypes.Select(t =>
            new AlertingCatalogEventTypeDto(
                t.ToString(),
                NotificationRuleSemantics.GaugeEventTypes.Contains(t) ? "gauge" : "execution",
                !NotificationRuleSemantics.GaugeEventTypes.Contains(t))).ToList();

        var fields = new List<AlertingCatalogFieldDto>
        {
            new("eventType", "both", "enum", eventTypes.Select(e => e.Name).ToList()),
            new("severity", "both", "enum", Enum.GetNames<NotificationSeverity>()),
            new("workflowName", "execution", "string"),
            new("folderPath", "execution", "string"),
            new("status", "execution", "enum", ["Succeeded", "Failed", "Cancelled", "Running", "Pending"]),
            new("errorMessage", "execution", "string"),
            new("durationMs", "execution", "number"),
            new("triggeredBy", "execution", "string"),
            new("callDepth", "execution", "number"),
            new("isSubWorkflow", "execution", "boolean", ["true", "false"]),
            new("cancelledBy", "execution", "enum", ["user", "cancelAll", "failover", "reconciler", "dispatch", "system"]),
            new("sourceKey", "gauge", "string"),
            new("targetMachine", "gauge", "string"),
            new("signalValue", "gauge", "number"),
        };

        var channels = _sinks.Keys.OrderBy(x => x.ToString()).Select(x => x.ToString()).ToList();
        return Ok(new AlertingCatalogResponse(
            eventTypes,
            fields,
            channels,
            ["eventType", "severity", "workflowId", "workflowName", "folderId", "folderPath", "executionId", "sourceKey", "targetMachine"]));
    }

    [HttpGet("rules/{id:guid}")]
    public async Task<ActionResult<NotificationRuleResponse>> Get(Guid id, CancellationToken ct)
    {
        var r = await _store.GetByKindAsync(id, NotificationRuleKind.Custom, ct);
        return r is null ? NotFound() : Ok(Project(r));
    }

    /// <summary>
    /// Read-only delivery ledger: recent <see cref="NotificationDeliveryAttempt"/> rows (newest first),
    /// optionally filtered by rule and/or status. No secrets — only channel + target are surfaced.
    /// </summary>
    [HttpGet("deliveries")]
    public async Task<ActionResult<List<NotificationDeliveryDto>>> GetDeliveries(
        [FromQuery] Guid? ruleId, [FromQuery] string? status, [FromQuery] int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);

        var query = _db.NotificationDeliveryAttempts.AsNoTracking();
        if (ruleId is { } rid) query = query.Where(a => a.NotificationRuleId == rid);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<NotificationDeliveryStatus>(status, ignoreCase: true, out var st))
                return BadRequest(new { message = $"Invalid status '{status}'" });
            query = query.Where(a => a.Status == st);
        }

        var attempts = await query.OrderByDescending(a => a.CreatedAt).Take(take).ToListAsync(ct);
        if (attempts.Count == 0) return Ok(new List<NotificationDeliveryDto>());

        // Resolve rule names + route channel/target via batched lookups (soft refs — no navigation).
        var ruleIds = attempts.Select(a => a.NotificationRuleId).Distinct().ToList();
        var routeIds = attempts.Select(a => a.NotificationRouteId).Distinct().ToList();
        var ruleNames = await _db.NotificationRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Name }).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var routes = (await _db.NotificationRoutes.AsNoTracking().Where(rt => routeIds.Contains(rt.Id))
            .Select(rt => new { rt.Id, rt.Channel, rt.Target }).ToListAsync(ct))
            .ToDictionary(rt => rt.Id);

        return Ok(attempts.Select(a =>
        {
            routes.TryGetValue(a.NotificationRouteId, out var route);
            return new NotificationDeliveryDto(
                a.Id, a.NotificationRuleId, ruleNames.GetValueOrDefault(a.NotificationRuleId),
                a.NotificationRouteId, route?.Channel.ToString(), route?.Target,
                a.EventKey, a.Status.ToString(), a.Attempt, a.CreatedAt, a.SentAt, a.Error, a.IsTest, a.Summary);
        }).ToList());
    }

    [HttpPost("rules")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<NotificationRuleResponse>> Create(CreateNotificationRuleRequest request, CancellationToken ct)
    {
        if (!TryBuildDraft(request.Name, request.Description, request.IsEnabled, request.EventTypes,
                request.FilterExpressionJson, request.ScopeKind, request.CooldownMinutes, request.MinOccurrences,
                request.OccurrenceWindowMinutes, request.Routes, request.Targets, request.DedupKeyTemplate, out var draft, out var error))
            return BadRequest(new { message = error });

        var created = await _store.CreateAsync(draft, this.GetCurrentUsername(), ct);
        await _audit.LogAsync(AuditActions.AlertRuleCreated, "NotificationRule", created.Id,
            AuditDetails.Json(("name", created.Name), ("scopeKind", created.ScopeKind.ToString()),
                ("enabled", created.IsEnabled), ("routes", created.Routes.Count)), ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, Project(created));
    }

    [HttpPut("rules/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, UpdateNotificationRuleRequest request, CancellationToken ct)
    {
        if (!TryBuildDraft(request.Name, request.Description, request.IsEnabled, request.EventTypes,
                request.FilterExpressionJson, request.ScopeKind, request.CooldownMinutes, request.MinOccurrences,
                request.OccurrenceWindowMinutes, request.Routes, request.Targets, request.DedupKeyTemplate, out var draft, out var error))
            return BadRequest(new { message = error });

        // Custom endpoint: refuse to touch a system policy (treated as not-found for this surface).
        if (await _store.GetByKindAsync(id, NotificationRuleKind.Custom, ct) is null) return NotFound();

        try { await _store.UpdateAsync(id, draft, this.GetCurrentUsername(), ct); }
        catch (KeyNotFoundException) { return NotFound(); }

        await _audit.LogAsync(AuditActions.AlertRuleUpdated, "NotificationRule", id,
            AuditDetails.Json(("name", draft.Name), ("scopeKind", draft.ScopeKind.ToString()),
                ("enabled", draft.IsEnabled), ("routes", draft.Routes.Count)), ct);
        return NoContent();
    }

    [HttpDelete("rules/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (await _store.GetByKindAsync(id, NotificationRuleKind.Custom, ct) is null) return NotFound();
        try { await _store.DeleteAsync(id, ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        await _audit.LogAsync(AuditActions.AlertRuleDeleted, "NotificationRule", id, null, ct);
        return NoContent();
    }

    /// <summary>
    /// Flips a custom rule's enabled state without re-submitting the full draft (the per-row toggle
    /// in the Alerting UI). Kind-scoped like Update/Delete so this surface can never touch a system
    /// policy. The dispatcher picks up the change on its next pass. Admin-only.
    /// </summary>
    [HttpPost("rules/{id:guid}/enable")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct)
    {
        var rule = await _store.GetByKindAsync(id, NotificationRuleKind.Custom, ct);
        if (rule is null) return NotFound();
        await _store.SetEnabledAsync(id, true, this.GetCurrentUsername(), ct);
        await _audit.LogAsync(AuditActions.AlertRuleEnabled, "NotificationRule", id,
            AuditDetails.Json(("name", rule.Name)), ct);
        return NoContent();
    }

    [HttpPost("rules/{id:guid}/disable")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct)
    {
        var rule = await _store.GetByKindAsync(id, NotificationRuleKind.Custom, ct);
        if (rule is null) return NotFound();
        await _store.SetEnabledAsync(id, false, this.GetCurrentUsername(), ct);
        await _audit.LogAsync(AuditActions.AlertRuleDisabled, "NotificationRule", id,
            AuditDetails.Json(("name", rule.Name)), ct);
        return NoContent();
    }

    /// <summary>
    /// Sends a synthetic notification through every route of the rule right now, so an operator can
    /// confirm the channel works end-to-end. Records IsTest delivery attempts (which never touch
    /// suppression/cooldown state). Admin-only.
    /// </summary>
    [HttpPost("rules/{id:guid}/test-fire")]
    [Authorize(Roles = "Admin")]
    [EnableRateLimiting("alerting-heavy")]
    public async Task<ActionResult<TestFireResponse>> TestFire(Guid id, CancellationToken ct)
    {
        var rule = await _store.GetByKindAsync(id, NotificationRuleKind.Custom, ct);
        if (rule is null) return NotFound();

        var user = this.GetCurrentUsername();
        var ctx = BuildSampleContext(rule.Name, user);
        var now = DateTime.UtcNow;
        var results = new List<TestFireRouteResult>();

        foreach (var route in rule.Routes)
        {
            var attempt = new NotificationDeliveryAttempt
            {
                Id = Guid.NewGuid(),
                NotificationRuleId = rule.Id,
                NotificationRouteId = route.Id,
                EventKey = $"test:{Guid.NewGuid():N}",
                DedupKey = $"test:{rule.Id}",
                IsTest = true,
                Attempt = 1,
                CreatedAt = now,
                SentAt = now,
            };

            NotificationSendResult result;
            if (!_sinks.TryGetValue(route.Channel, out var sink))
            {
                result = NotificationSendResult.Fail($"no sink registered for channel {route.Channel}");
            }
            else
            {
                var secret = string.IsNullOrEmpty(route.Secret) ? null : await _store.GetRouteSecretAsync(route.Id, ct);
                result = await sink.SendAsync(ctx, route.Target, secret, ct);
            }

            attempt.Status = result.Success ? NotificationDeliveryStatus.Sent : NotificationDeliveryStatus.Failed;
            attempt.Error = result.Error;
            attempt.Summary = $"[test] {route.Channel}:{route.Target}";
            _db.NotificationDeliveryAttempts.Add(attempt);
            results.Add(new TestFireRouteResult(route.Channel.ToString(), route.Target, result.Success, result.Error));
        }
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.AlertRuleTestFired, "NotificationRule", rule.Id,
            AuditDetails.Json(("name", rule.Name), ("routes", rule.Routes.Count),
                ("allSucceeded", results.All(r => r.Success))), ct);

        return Ok(new TestFireResponse(results.All(r => r.Success), results));
    }

    /// <summary>Stateless dry-run: does this filter expression match the supplied sample fields?</summary>
    [HttpPost("preview-filter")]
    public ActionResult<PreviewFilterResponse> PreviewFilter(PreviewFilterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FilterExpressionJson))
            return Ok(new PreviewFilterResponse(true, null)); // no filter → matches everything
        try
        {
            using var doc = JsonDocument.Parse(request.FilterExpressionJson);
            var fields = request.EventFields ?? new Dictionary<string, string>();
            var matches = ConditionEvaluator.Evaluate(doc.RootElement,
                new ConditionContext(EmptyResults, null, null, null, fields));
            return Ok(new PreviewFilterResponse(matches, null));
        }
        catch (JsonException ex)
        {
            return Ok(new PreviewFilterResponse(false, $"invalid filter JSON: {ex.Message}"));
        }
    }

    [HttpPost("preview-rule")]
    public ActionResult<PreviewRuleResponse> PreviewRule(PreviewRuleRequest request)
    {
        if (!TryBuildDraft("__preview__", null, true, request.EventTypes,
                request.FilterExpressionJson, request.ScopeKind, 0, 1, 0,
                request.Routes, request.Targets, request.DedupKeyTemplate, out var draft, out var error))
            return BadRequest(new { message = error });

        var ctx = BuildPreviewContext(request.EventTypes, request.EventFields);
        var reasons = new List<string>();
        var eventTypeMatches = NotificationRuleSemantics.EventTypeMatches(draft, ctx.EventType);
        var scopeMatches = NotificationRuleSemantics.ScopeMatches(draft, ctx);
        var filterMatches = NotificationRuleSemantics.RuleFilterMatches(draft, ctx);

        if (!eventTypeMatches) reasons.Add("event type did not match");
        if (!scopeMatches) reasons.Add("scope did not match");
        if (!filterMatches) reasons.Add("rule filter did not match");

        var ruleMatches = eventTypeMatches && scopeMatches && filterMatches;
        var routeResults = draft.Routes.OrderBy(r => r.Order)
            .Select(r => new PreviewRouteResult(
                r.Channel.ToString(),
                r.Target,
                ruleMatches && NotificationRuleSemantics.RouteFilterMatches(r, ctx)))
            .ToList();

        if (ruleMatches && routeResults.All(r => !r.Matches)) reasons.Add("no route condition matched");

        return Ok(new PreviewRuleResponse(
            ruleMatches,
            ruleMatches ? NotificationRuleSemantics.BuildDedupKey(draft, ctx) : null,
            routeResults,
            reasons));
    }

    private bool TryBuildDraft(
        string name, string? description, bool isEnabled, IReadOnlyList<string>? eventTypesRaw,
        string? filterJson, string scopeRaw, int cooldownMinutes, int minOccurrences, int occurrenceWindowMinutes,
        IReadOnlyList<NotificationRouteDto>? routes, IReadOnlyList<NotificationRuleTargetDto>? targets, string? dedupKeyTemplate,
        out NotificationRule draft, out string error)
    {
        draft = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            return Fail("Name is required and must be 100 characters or less", out error);
        if (description is { Length: > 500 })
            return Fail("Description must be 500 characters or less", out error);

        if (eventTypesRaw is null || eventTypesRaw.Count == 0)
            return Fail("At least one event type is required", out error);
        var eventTypes = new List<string>();
        var hasGaugeType = false;
        foreach (var et in eventTypesRaw)
        {
            if (!Enum.TryParse<NotificationEventType>(et, ignoreCase: true, out var parsed))
                return Fail($"Invalid event type '{et}'", out error);
            if (!NotificationRuleSemantics.SupportedEventTypes.Contains(parsed))
                return Fail($"Event type '{et}' is not deliverable yet (supported: {string.Join(", ", NotificationRuleSemantics.SupportedEventTypes)})", out error);
            if (NotificationRuleSemantics.GaugeEventTypes.Contains(parsed)) hasGaugeType = true;
            eventTypes.Add(parsed.ToString());
        }

        if (!Enum.TryParse<NotificationScopeKind>(scopeRaw, ignoreCase: true, out var scope))
            return Fail($"Invalid scopeKind '{scopeRaw}' (expected Global, Folders or Workflows)", out error);

        // Gauge events aren't workflow-scoped — a folder/workflow-scoped gauge rule could never match.
        if (hasGaugeType && scope != NotificationScopeKind.Global)
            return Fail($"Gauge event types ({string.Join("/", NotificationRuleSemantics.GaugeEventTypes)}) support Global scope only", out error);

        if (!NotificationRuleSemantics.TryValidateConditionJson(filterJson, out var conditionError))
            return Fail($"filterExpressionJson {conditionError}", out error);
        if (!NotificationRuleSemantics.TryValidateDedupTemplate(dedupKeyTemplate, out var dedupError))
            return Fail(dedupError, out error);

        if (cooldownMinutes < 0 || occurrenceWindowMinutes < 0)
            return Fail("Cooldown and occurrence-window minutes must not be negative", out error);
        // Cap throttle windows well below the notification retention floor (default 90d) so the
        // retention sweep's stale-suppression prune can never delete a row whose cooldown/flap window
        // is still active. 30 days is far beyond any realistic alert throttle.
        if (cooldownMinutes > MaxThrottleMinutes || occurrenceWindowMinutes > MaxThrottleMinutes)
            return Fail($"Cooldown and occurrence-window minutes must be {MaxThrottleMinutes} (30 days) or less", out error);
        if (minOccurrences < 1)
            return Fail("minOccurrences must be at least 1", out error);

        // Routes
        if (routes is null || routes.Count == 0)
            return Fail("At least one route is required", out error);
        var mappedRoutes = new List<NotificationRoute>();
        var order = 0;
        foreach (var rt in routes)
        {
            if (!Enum.TryParse<NotificationChannel>(rt.Channel, ignoreCase: true, out var channel))
                return Fail($"Invalid channel '{rt.Channel}'", out error);
            if (!_sinks.ContainsKey(channel))
                return Fail($"No delivery sink is registered for channel '{rt.Channel}' (available: {string.Join(", ", _sinks.Keys)})", out error);
            if (string.IsNullOrWhiteSpace(rt.Target))
                return Fail("Each route requires a target", out error);
            if (!NotificationRuleSemantics.TryValidateConditionJson(rt.ConditionExpressionJson, out var routeConditionError))
                return Fail($"route conditionExpressionJson {routeConditionError}", out error);
            mappedRoutes.Add(new NotificationRoute
            {
                Id = rt.Id ?? Guid.Empty,
                Channel = channel,
                Target = rt.Target.Trim(),
                Secret = rt.Secret, // store resolves unchanged-sentinel / encrypts plaintext
                ConditionExpressionJson = string.IsNullOrWhiteSpace(rt.ConditionExpressionJson) ? null : rt.ConditionExpressionJson,
                Order = order++,
            });
        }

        // Scope targets
        var mappedTargets = new List<NotificationRuleTarget>();
        if (scope != NotificationScopeKind.Global)
        {
            var expectedKind = scope == NotificationScopeKind.Folders
                ? NotificationTargetKind.Folder
                : NotificationTargetKind.Workflow;
            if (targets is null || targets.Count == 0)
                return Fail($"{scope} rules require at least one target", out error);
            foreach (var t in targets)
            {
                if (!Enum.TryParse<NotificationTargetKind>(t.TargetKind, ignoreCase: true, out var kind))
                    return Fail($"Invalid target kind '{t.TargetKind}'", out error);
                if (kind != expectedKind)
                    return Fail($"{scope} rules may only contain {expectedKind} targets", out error);
                if (t.TargetId == Guid.Empty)
                    return Fail("Target id must not be empty", out error);
                mappedTargets.Add(new NotificationRuleTarget { TargetKind = kind, TargetId = t.TargetId });
            }
        }

        draft = new NotificationRule
        {
            Name = name.Trim(),
            Description = description,
            IsEnabled = isEnabled,
            EventTypes = string.Join(',', eventTypes),
            FilterExpressionJson = string.IsNullOrWhiteSpace(filterJson) ? null : filterJson,
            ScopeKind = scope,
            CooldownMinutes = cooldownMinutes,
            DedupKeyTemplate = string.IsNullOrWhiteSpace(dedupKeyTemplate) ? null : dedupKeyTemplate,
            MinOccurrences = minOccurrences,
            OccurrenceWindowMinutes = occurrenceWindowMinutes,
            Routes = mappedRoutes,
            Targets = mappedTargets,
        };
        return true;
    }

    private static bool Fail(string message, out string error) { error = message; return false; }

    private static NotificationContext BuildSampleContext(string ruleName, string? user) => new(
        EventType: NotificationEventType.ExecutionFailed,
        Severity: NotificationSeverity.Warning,
        EventKey: $"test:{Guid.NewGuid():N}",
        WorkflowId: null,
        WorkflowName: "Sample Workflow",
        FolderId: null,
        FolderPath: "/",
        ExecutionId: null,
        Status: "Failed",
        ErrorMessage: "This is a NodePilot alerting test notification.",
        DurationMs: 1234,
        OccurredAt: DateTime.UtcNow,
        TriggeredBy: user,
        CallDepth: 0,
        IsSubWorkflow: false,
        TargetMachine: null,
        SourceKey: null,
        Title: $"[Test] NodePilot alert: {ruleName}",
        Summary: "Test-fire from the alerting rule editor.",
        DeepLinkPath: "/alerting");

    private static NotificationContext BuildPreviewContext(IReadOnlyList<string>? requestedEventTypes, IReadOnlyDictionary<string, string>? fields)
    {
        fields ??= new Dictionary<string, string>();
        var eventTypeRaw = fields.TryGetValue("eventType", out var et) ? et : requestedEventTypes?.FirstOrDefault();
        if (!Enum.TryParse<NotificationEventType>(eventTypeRaw, ignoreCase: true, out var eventType))
            eventType = NotificationEventType.ExecutionFailed;
        if (!Enum.TryParse<NotificationSeverity>(fields.GetValueOrDefault("severity"), ignoreCase: true, out var severity))
            severity = eventType == NotificationEventType.ExecutionFailed ? NotificationSeverity.Warning : NotificationSeverity.Info;

        static Guid? GuidField(IReadOnlyDictionary<string, string> source, string key)
            => source.TryGetValue(key, out var raw) && Guid.TryParse(raw, out var value) ? value : null;
        static long? LongField(IReadOnlyDictionary<string, string> source, string key)
            => source.TryGetValue(key, out var raw) && long.TryParse(raw, out var value) ? value : null;
        static int IntField(IReadOnlyDictionary<string, string> source, string key)
            => source.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : 0;
        static bool BoolField(IReadOnlyDictionary<string, string> source, string key)
            => source.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value) && value;

        return new NotificationContext(
            EventType: eventType,
            Severity: severity,
            EventKey: "preview",
            WorkflowId: GuidField(fields, "workflowId"),
            WorkflowName: fields.GetValueOrDefault("workflowName"),
            FolderId: GuidField(fields, "folderId"),
            FolderPath: fields.GetValueOrDefault("folderPath"),
            ExecutionId: GuidField(fields, "executionId"),
            Status: fields.GetValueOrDefault("status"),
            ErrorMessage: fields.GetValueOrDefault("errorMessage"),
            DurationMs: LongField(fields, "durationMs"),
            OccurredAt: DateTime.UtcNow,
            TriggeredBy: fields.GetValueOrDefault("triggeredBy"),
            CallDepth: IntField(fields, "callDepth"),
            IsSubWorkflow: BoolField(fields, "isSubWorkflow"),
            TargetMachine: fields.GetValueOrDefault("targetMachine"),
            SourceKey: fields.GetValueOrDefault("sourceKey"),
            Title: null,
            Summary: null,
            DeepLinkPath: null,
            SignalValue: LongField(fields, "signalValue"),
            CancelledBy: fields.GetValueOrDefault("cancelledBy"));
    }

    private static NotificationRuleResponse Project(NotificationRule r) => new(
        r.Id, r.Name, r.Description, r.IsEnabled,
        string.IsNullOrWhiteSpace(r.EventTypes)
            ? []
            : r.EventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        r.FilterExpressionJson,
        r.ScopeKind.ToString(),
        r.CooldownMinutes, r.MinOccurrences, r.OccurrenceWindowMinutes,
        r.Routes.OrderBy(x => x.Order).Select(x => new NotificationRouteDto(
            x.Id, x.Channel.ToString(), x.Target,
            string.IsNullOrEmpty(x.Secret) ? null : NotificationRuleStore.UnchangedSecret, x.Order,
            x.ConditionExpressionJson)).ToList(),
        r.Targets.Select(t => new NotificationRuleTargetDto(t.TargetKind.ToString(), t.TargetId)).ToList(),
        r.CreatedAt, r.UpdatedAt, r.UpdatedBy, r.DedupKeyTemplate);
}
