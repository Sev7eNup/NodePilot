using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using NodePilot.Api.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Notifications;
using NodePilot.Scheduler.SystemAlerts;

namespace NodePilot.Api.Controllers;

/// <summary>
/// System-alert policies built on the modular source catalog (ADR 0008): a policy binds a source, a
/// descriptor-validated condition + parameters, a sustain window, scope, severity and routes. Read is
/// Admin/Operator; create/update/delete/enable/disable/test-fire is Admin-only (mirrors
/// <see cref="AlertingController"/>). This surface only ever touches <c>Kind=System</c> rows; the custom-rule
/// endpoints under <c>/api/alerting/rules</c> only ever touch <c>Kind=Custom</c> — neither can mutate the other.
/// </summary>
[ApiController]
[Route("api/alerting/system")]
[Authorize(Roles = "Admin,Operator")]
public class SystemAlertingController : ControllerBase
{
    private const int MaxThrottleMinutes = 43_200; // 30 days — same cap as AlertingController
    private const int PreviewMatchCap = 100;

    private readonly ISystemAlertCatalog _catalog;
    private readonly NodePilotDbContext _db;
    private readonly INotificationRuleStore _store;
    private readonly IAuditWriter _audit;
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationSink> _sinks;
    private readonly ILogger<SystemAlertingController> _logger;

    public SystemAlertingController(
        ISystemAlertCatalog catalog,
        NodePilotDbContext db,
        INotificationRuleStore store,
        IAuditWriter audit,
        IEnumerable<INotificationSink> sinks,
        ILogger<SystemAlertingController> logger)
    {
        _catalog = catalog;
        _db = db;
        _store = store;
        _audit = audit;
        var map = new Dictionary<NotificationChannel, INotificationSink>();
        foreach (var s in sinks) map[s.Channel] = s;
        _sinks = map;
        _logger = logger;
    }

    // ---- Catalog ----

    /// <summary>The registered source descriptors plus a best-effort per-source availability flag.</summary>
    [HttpGet("catalog")]
    public async Task<ActionResult<SystemAlertCatalogResponse>> GetCatalog(CancellationToken ct)
    {
        var sources = new List<SystemAlertSourceDto>(_catalog.Descriptors.Count);
        foreach (var d in _catalog.Descriptors)
        {
            var source = _catalog.Find(d.SourceId);
            var available = source is not null && await ProbeAvailabilityAsync(source, ct);
            sources.Add(new SystemAlertSourceDto(
                d.SourceId,
                d.Category.ToString(),
                d.ScopeCapability.ToString(),
                d.DefaultSeverity.ToString(),
                d.Fields.Select(f => new SystemAlertFieldDto(f.Name, f.Type.ToString(), f.Operators, f.Unit, f.EnumValues)).ToList(),
                d.Parameters.Select(p => new SystemAlertParameterDto(p.Name, p.Type.ToString(), p.Default, p.Required, p.Unit, p.Min, p.Max)).ToList(),
                d.Presets.Select(pr => new SystemAlertPresetDto(pr.PresetId, pr.Severity.ToString(), pr.SustainForSeconds, pr.ConditionJson, pr.Parameters)).ToList(),
                available));
        }
        return Ok(new SystemAlertCatalogResponse(sources));
    }

    // ---- Policy CRUD ----

    [HttpGet("policies")]
    public async Task<ActionResult<List<SystemAlertPolicyResponse>>> GetAll(CancellationToken ct)
        => Ok((await _store.GetAllByKindAsync(NotificationRuleKind.System, ct)).Select(Project).ToList());

    [HttpGet("policies/{id:guid}")]
    public async Task<ActionResult<SystemAlertPolicyResponse>> Get(Guid id, CancellationToken ct)
    {
        var p = await _store.GetByKindAsync(id, NotificationRuleKind.System, ct);
        return p is null ? NotFound() : Ok(Project(p));
    }

    [HttpPost("policies")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<SystemAlertPolicyResponse>> Create(SaveSystemAlertPolicyRequest request, CancellationToken ct)
    {
        if (!TryBuildDraft(request, out var draft, out var problem)) return problem!;
        var created = await _store.CreateAsync(draft, this.GetCurrentUsername(), ct);
        await _audit.LogAsync(AuditActions.SystemAlertPolicyCreated, "SystemAlertPolicy", created.Id,
            AuditDetails.Json(("name", created.Name), ("sourceId", created.SystemSourceId ?? ""), ("enabled", created.IsEnabled)), ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, Project(created));
    }

    [HttpPut("policies/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, SaveSystemAlertPolicyRequest request, CancellationToken ct)
    {
        if (await _store.GetByKindAsync(id, NotificationRuleKind.System, ct) is null) return NotFound();
        if (!TryBuildDraft(request, out var draft, out var problem)) return problem!;
        await _store.UpdateAsync(id, draft, this.GetCurrentUsername(), ct);
        await _audit.LogAsync(AuditActions.SystemAlertPolicyUpdated, "SystemAlertPolicy", id,
            AuditDetails.Json(("name", draft.Name), ("sourceId", draft.SystemSourceId ?? "")), ct);
        return NoContent();
    }

    [HttpDelete("policies/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (await _store.GetByKindAsync(id, NotificationRuleKind.System, ct) is null) return NotFound();
        await _store.DeleteAsync(id, ct);
        await _audit.LogAsync(AuditActions.SystemAlertPolicyDeleted, "SystemAlertPolicy", id, null, ct);
        return NoContent();
    }

    [HttpPost("policies/{id:guid}/enable")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct)
    {
        var policy = await _store.GetByKindAsync(id, NotificationRuleKind.System, ct);
        if (policy is null) return NotFound();
        if (policy.Routes.Count == 0)
            return BadRequest(new { message = "A policy needs at least one route before it can be enabled." });

        await _store.SetEnabledAsync(id, true, this.GetCurrentUsername(), ct);
        await _audit.LogAsync(AuditActions.SystemAlertPolicyEnabled, "SystemAlertPolicy", id,
            AuditDetails.Json(("name", policy.Name)), ct);
        return NoContent();
    }

    [HttpPost("policies/{id:guid}/disable")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct)
    {
        var policy = await _store.GetByKindAsync(id, NotificationRuleKind.System, ct);
        if (policy is null) return NotFound();
        await _store.SetEnabledAsync(id, false, this.GetCurrentUsername(), ct);
        await _audit.LogAsync(AuditActions.SystemAlertPolicyDisabled, "SystemAlertPolicy", id,
            AuditDetails.Json(("name", policy.Name)), ct);
        return NoContent();
    }

    // ---- Preview (stateless) + test-fire (route delivery) ----

    /// <summary>
    /// Stateless preview: sample the source now and report which current instances the condition matches.
    /// No policy state, no delivery attempts — purely a "what does this catch right now?" check.
    /// </summary>
    [HttpPost("preview")]
    [EnableRateLimiting("alerting-heavy")]
    public async Task<ActionResult<SystemAlertPreviewResponse>> Preview(SystemAlertPreviewRequest request, CancellationToken ct)
    {
        var source = _catalog.Find(request.SourceId);
        if (source is null) return BadRequest(new { message = $"Unknown source '{request.SourceId}'." });
        var descriptor = source.Describe();

        if (!TryBuildSourceParameters(descriptor, request.SourceParameters, out var paramsJson, out var paramError))
            return BadRequest(new { message = paramError });
        if (!SystemAlertConditionValidator.TryValidate(request.ConditionJson, descriptor.Fields, out var condErrors))
            return ConditionValidationProblem(condErrors);

        if (!await ProbeAvailabilityAsync(source, ct))
            return Ok(new SystemAlertPreviewResponse(false, []));

        var query = SystemAlertParameters.ToQuery(paramsJson);
        IReadOnlyList<SystemAlertObservation> observations;
        try { observations = await source.ObserveAsync(_db, query, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Preview sampling of source {SourceId} failed.", request.SourceId);
            return Ok(new SystemAlertPreviewResponse(false, []));
        }

        var matches = observations.Take(PreviewMatchCap).Select(obs =>
        {
            var fields = SystemAlertEvaluator.FieldMap(obs);
            return new SystemAlertPreviewMatch(obs.InstanceKey, obs.Title, obs.Summary, fields,
                SystemAlertEvaluator.Matches(request.ConditionJson, fields));
        }).ToList();

        return Ok(new SystemAlertPreviewResponse(true, matches));
    }

    /// <summary>Sends a synthetic notification through every route of the policy so an operator can confirm the channel works.</summary>
    [HttpPost("policies/{id:guid}/test-fire")]
    [Authorize(Roles = "Admin")]
    [EnableRateLimiting("alerting-heavy")]
    public async Task<ActionResult<TestFireResponse>> TestFire(Guid id, CancellationToken ct)
    {
        var policy = await _store.GetByKindAsync(id, NotificationRuleKind.System, ct);
        if (policy is null) return NotFound();

        var ctx = BuildSampleContext(policy);
        var now = DateTime.UtcNow;
        var results = new List<TestFireRouteResult>();

        foreach (var route in policy.Routes)
        {
            var attempt = new NotificationDeliveryAttempt
            {
                Id = Guid.NewGuid(),
                NotificationRuleId = policy.Id,
                NotificationRouteId = route.Id,
                EventKey = $"test:{Guid.NewGuid():N}",
                DedupKey = $"test:{policy.Id}",
                IsTest = true,
                Attempt = 1,
                CreatedAt = now,
                SentAt = now,
            };

            NotificationSendResult result;
            if (!_sinks.TryGetValue(route.Channel, out var sink))
                result = NotificationSendResult.Fail($"no sink registered for channel {route.Channel}");
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

        await _audit.LogAsync(AuditActions.SystemAlertPolicyTestFired, "SystemAlertPolicy", policy.Id,
            AuditDetails.Json(("name", policy.Name), ("routes", policy.Routes.Count), ("allSucceeded", results.All(r => r.Success))), ct);

        return Ok(new TestFireResponse(results.All(r => r.Success), results));
    }

    // ---- helpers ----

    private bool TryBuildDraft(SaveSystemAlertPolicyRequest request, out NotificationRule draft, out ActionResult? problem)
    {
        draft = null!;
        problem = null;

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
            return Bad("Name is required and must be 100 characters or less", out problem);
        if (request.Description is { Length: > 500 })
            return Bad("Description must be 500 characters or less", out problem);

        var source = _catalog.Find(request.SourceId);
        if (source is null) return Bad($"Unknown source '{request.SourceId}'.", out problem);
        var descriptor = source.Describe();

        if (!Enum.TryParse<NotificationScopeKind>(request.ScopeKind, ignoreCase: true, out var scope))
            return Bad($"Invalid scopeKind '{request.ScopeKind}' (expected Global, Folders or Workflows)", out problem);
        if (descriptor.ScopeCapability == SystemAlertScopeCapability.GlobalOnly && scope != NotificationScopeKind.Global)
            return Bad($"Source '{descriptor.SourceId}' supports Global scope only.", out problem);

        if (!TryBuildSourceParameters(descriptor, request.SourceParameters, out var paramsJson, out var paramError))
            return Bad(paramError!, out problem);

        if (!SystemAlertConditionValidator.TryValidate(request.ConditionJson, descriptor.Fields, out var condErrors))
        { problem = ConditionValidationProblem(condErrors); return false; }

        NotificationSeverity? severityOverride = null;
        if (!string.IsNullOrWhiteSpace(request.SeverityOverride))
        {
            if (!Enum.TryParse<NotificationSeverity>(request.SeverityOverride, ignoreCase: true, out var sev))
                return Bad($"Invalid severity '{request.SeverityOverride}'", out problem);
            severityOverride = sev;
        }

        if (request.SustainForSeconds < 0)
            return Bad("sustainForSeconds must not be negative", out problem);
        if (request.CooldownMinutes < 0 || request.OccurrenceWindowMinutes < 0)
            return Bad("Cooldown and occurrence-window minutes must not be negative", out problem);
        if (request.CooldownMinutes > MaxThrottleMinutes || request.OccurrenceWindowMinutes > MaxThrottleMinutes)
            return Bad($"Cooldown and occurrence-window minutes must be {MaxThrottleMinutes} (30 days) or less", out problem);
        if (request.MinOccurrences < 1)
            return Bad("minOccurrences must be at least 1", out problem);

        // Routes: required only to enable (a draft policy may be saved disabled with no route).
        var mappedRoutes = new List<NotificationRoute>();
        if (request.IsEnabled && (request.Routes is null || request.Routes.Count == 0))
            return Bad("An enabled policy requires at least one route.", out problem);
        var order = 0;
        foreach (var rt in request.Routes ?? [])
        {
            if (!Enum.TryParse<NotificationChannel>(rt.Channel, ignoreCase: true, out var channel))
                return Bad($"Invalid channel '{rt.Channel}'", out problem);
            if (!_sinks.ContainsKey(channel))
                return Bad($"No delivery sink is registered for channel '{rt.Channel}' (available: {string.Join(", ", _sinks.Keys)})", out problem);
            if (string.IsNullOrWhiteSpace(rt.Target))
                return Bad("Each route requires a target", out problem);
            if (!NotificationRuleSemantics.TryValidateConditionJson(rt.ConditionExpressionJson, out var routeConditionError))
                return Bad($"route conditionExpressionJson {routeConditionError}", out problem);
            mappedRoutes.Add(new NotificationRoute
            {
                Id = rt.Id ?? Guid.Empty,
                Channel = channel,
                Target = rt.Target.Trim(),
                Secret = rt.Secret,
                ConditionExpressionJson = string.IsNullOrWhiteSpace(rt.ConditionExpressionJson) ? null : rt.ConditionExpressionJson,
                Order = order++,
            });
        }

        var mappedTargets = new List<NotificationRuleTarget>();
        if (scope != NotificationScopeKind.Global)
        {
            var expectedKind = scope == NotificationScopeKind.Folders ? NotificationTargetKind.Folder : NotificationTargetKind.Workflow;
            if (request.Targets is null || request.Targets.Count == 0)
                return Bad($"{scope} policies require at least one target", out problem);
            foreach (var t in request.Targets)
            {
                if (!Enum.TryParse<NotificationTargetKind>(t.TargetKind, ignoreCase: true, out var kind))
                    return Bad($"Invalid target kind '{t.TargetKind}'", out problem);
                if (kind != expectedKind)
                    return Bad($"{scope} policies may only contain {expectedKind} targets", out problem);
                if (t.TargetId == Guid.Empty)
                    return Bad("Target id must not be empty", out problem);
                mappedTargets.Add(new NotificationRuleTarget { TargetKind = kind, TargetId = t.TargetId });
            }
        }

        draft = new NotificationRule
        {
            Name = request.Name.Trim(),
            Description = request.Description,
            IsEnabled = request.IsEnabled,
            Kind = NotificationRuleKind.System,
            EventTypes = NotificationEventType.SystemAlert.ToString(),
            SystemSourceId = descriptor.SourceId,
            SystemPresetId = string.IsNullOrWhiteSpace(request.PresetId) ? null : request.PresetId,
            SourceParametersJson = paramsJson,
            SustainForSeconds = request.SustainForSeconds,
            SeverityOverride = severityOverride,
            FilterExpressionJson = string.IsNullOrWhiteSpace(request.ConditionJson) ? null : request.ConditionJson,
            ScopeKind = scope,
            CooldownMinutes = request.CooldownMinutes,
            MinOccurrences = request.MinOccurrences,
            OccurrenceWindowMinutes = request.OccurrenceWindowMinutes,
            ActivatedAt = request.IsEnabled ? DateTime.UtcNow : null,
            Routes = mappedRoutes,
            Targets = mappedTargets,
        };
        return true;
    }

    // Validates provided source parameters against the descriptor: only declared params, required present,
    // numeric within [Min,Max]. Returns the canonical JSON (declared keys only) to persist.
    private static bool TryBuildSourceParameters(
        NodePilot.Core.Models.SystemAlertSourceDescriptor descriptor,
        IReadOnlyDictionary<string, object?>? provided, out string? json, out string? error)
    {
        json = null;
        error = null;
        var byName = descriptor.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var result = new Dictionary<string, object?>();

        foreach (var (name, value) in provided ?? new Dictionary<string, object?>())
        {
            if (!byName.TryGetValue(name, out var spec))
            { error = $"Unknown source parameter '{name}'."; return false; }
            if (value is null) continue;
            if (spec.Type is SystemAlertFieldType.Number or SystemAlertFieldType.Duration)
            {
                if (!TryToDouble(value, out var num))
                { error = $"Parameter '{name}' must be a number."; return false; }
                if (spec.Min is { } min && num < min) { error = $"Parameter '{name}' must be >= {min}."; return false; }
                if (spec.Max is { } max && num > max) { error = $"Parameter '{name}' must be <= {max}."; return false; }
            }
            result[name] = value;
        }

        foreach (var spec in descriptor.Parameters.Where(p => p.Required))
            if (!result.ContainsKey(spec.Name))
            { error = $"Required source parameter '{spec.Name}' is missing."; return false; }

        json = result.Count == 0 ? null : JsonSerializer.Serialize(result);
        return true;
    }

    private static bool TryToDouble(object value, out double result)
    {
        switch (value)
        {
            case JsonElement je when je.ValueKind == JsonValueKind.Number: result = je.GetDouble(); return true;
            case JsonElement je when je.ValueKind == JsonValueKind.String: return double.TryParse(je.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
            default: return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
        }
    }

    private SystemAlertPolicyResponse Project(NotificationRule p) => new(
        p.Id, p.Name, p.Description, p.IsEnabled,
        p.SystemSourceId ?? "", p.SystemPresetId,
        SystemAlertParameters.FromJson(p.SourceParametersJson),
        p.FilterExpressionJson,
        p.SustainForSeconds,
        p.SeverityOverride?.ToString(),
        p.ScopeKind.ToString(),
        p.Targets.Select(t => new NotificationRuleTargetDto(t.TargetKind.ToString(), t.TargetId)).ToList(),
        p.Routes.OrderBy(r => r.Order).Select(r => new NotificationRouteDto(
            r.Id, r.Channel.ToString(), r.Target,
            string.IsNullOrEmpty(r.Secret) ? null : NotificationRuleStore.UnchangedSecret, r.Order, r.ConditionExpressionJson)).ToList(),
        p.CooldownMinutes, p.MinOccurrences, p.OccurrenceWindowMinutes,
        p.CreatedAt, p.UpdatedAt, p.UpdatedBy, p.ActivatedAt);

    private static NotificationContext BuildSampleContext(NotificationRule policy) => new(
        NotificationEventType.SystemAlert, NotificationSeverity.Warning,
        EventKey: $"test:{Guid.NewGuid():N}", WorkflowId: null, WorkflowName: null, FolderId: null, FolderPath: null,
        ExecutionId: null, Status: null, ErrorMessage: null, DurationMs: null, OccurredAt: DateTime.UtcNow,
        TriggeredBy: null, CallDepth: 0, IsSubWorkflow: false, TargetMachine: null,
        SourceKey: policy.SystemSourceId,
        Title: $"[Test] NodePilot system alert: {policy.Name}",
        Summary: "Test-fire from the system-alert policy editor.",
        DeepLinkPath: "/alerts", SourceId: policy.SystemSourceId);

    private ActionResult ConditionValidationProblem(IReadOnlyList<SystemAlertValidationError> errors)
    {
        var ms = new ModelStateDictionary();
        foreach (var e in errors) ms.AddModelError(e.Path, e.Message);
        return ValidationProblem(ms);
    }

    private static bool Bad(string message, out ActionResult? problem)
    {
        problem = new BadRequestObjectResult(new { message });
        return false;
    }

    private async Task<bool> ProbeAvailabilityAsync(ISystemAlertSource source, CancellationToken ct)
    {
        try { return await source.IsAvailableAsync(_db, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Availability probe for system-alert source {SourceId} failed; reporting unavailable.", source.SourceId);
            return false;
        }
    }
}
