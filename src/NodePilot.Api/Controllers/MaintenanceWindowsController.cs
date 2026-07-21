using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Admin/Operator-managed maintenance windows: time-scoped rules that block (or restrict) when
/// the workflows they target may start new runs. Read is Admin/Operator (operational
/// visibility); create/update/delete is Admin-only — mirrors <c>GlobalVariablesController</c>.
///
/// <para>A window owns its targeting (<c>ScopeKind</c> = Global | Folders | Workflows), so a
/// single window can cover everything, a folder subtree, or an explicit workflow list. After
/// any mutation the in-memory evaluator snapshot is refreshed inline so the change takes effect
/// immediately rather than on the next background tick.</para>
/// </summary>
[ApiController]
[Route("api/maintenance-windows")]
[Authorize(Roles = "Admin,Operator")]
public class MaintenanceWindowsController : ControllerBase
{
    /// <summary>Sanity cap for a Cron window's open duration: one full week.</summary>
    private const int MaxCronDurationMinutes = 7 * 24 * 60;

    private readonly IMaintenanceWindowStore _store;
    private readonly IMaintenanceWindowEvaluator _evaluator;
    private readonly NodePilotDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IResourceAuthorizationService _authz;

    public MaintenanceWindowsController(
        IMaintenanceWindowStore store,
        IMaintenanceWindowEvaluator evaluator,
        NodePilotDbContext db,
        IAuditWriter audit,
        IResourceAuthorizationService authz)
    {
        _store = store;
        _evaluator = evaluator;
        _db = db;
        _audit = audit;
        _authz = authz;
    }

    [HttpGet]
    public async Task<ActionResult<List<MaintenanceWindowResponse>>> GetAll(CancellationToken ct)
    {
        var rows = await _store.GetAllAsync(ct);
        return Ok(rows.Select(Project).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MaintenanceWindowResponse>> Get(Guid id, CancellationToken ct)
    {
        var w = await _store.GetAsync(id, ct);
        return w is null ? NotFound() : Ok(Project(w));
    }

    /// <summary>
    /// Read-only "which maintenance windows affect this workflow" badge. Resolves the workflow's
    /// folder ancestry through the evaluator snapshot — no second write path, single source of truth.
    /// </summary>
    [HttpGet("affecting/{workflowId:guid}")]
    public async Task<ActionResult<List<MaintenanceWindowAffectingDto>>> Affecting(Guid workflowId, CancellationToken ct)
    {
        var wf = await _db.Workflows.AsNoTracking()
            .Where(w => w.Id == workflowId)
            .Select(w => new { w.Id, w.FolderId })
            .FirstOrDefaultAsync(ct);
        if (wf is null) return NotFound();
        if (!await _authz.CanAccessWorkflowAsync(User, wf.FolderId, ResourceOp.Read, ct))
            return NotFound();

        var matches = _evaluator.GetWindowsAffecting(wf.Id, wf.FolderId, DateTime.UtcNow);
        return Ok(matches
            .Select(m => new MaintenanceWindowAffectingDto(m.Id, m.Name, m.Mode.ToString(), m.IsEnabled, m.ActiveNow))
            .ToList());
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<MaintenanceWindowResponse>> Create(
        CreateMaintenanceWindowRequest request, CancellationToken ct)
    {
        if (!TryBuildDraft(request.Name, request.Description, request.IsEnabled, request.Mode, request.ScopeKind,
                request.Recurrence, request.OneTimeStartUtc, request.OneTimeEndUtc, request.WeeklyDaysMask,
                request.WeeklyStartMinuteOfDay, request.WeeklyEndMinuteOfDay, request.CronExpression,
                request.DurationMinutes, request.TimeZoneId, request.Targets, out var draft, out var error))
            return BadRequest(new { message = error });

        var created = await _store.CreateAsync(draft, this.GetCurrentUsername(), ct);
        await _evaluator.RefreshAsync(ct);

        await _audit.LogAsync(AuditActions.MaintenanceWindowCreated, "MaintenanceWindow", created.Id,
            AuditDetails.Json(("name", created.Name), ("mode", created.Mode.ToString()),
                ("scopeKind", created.ScopeKind.ToString()), ("enabled", created.IsEnabled)), ct);
        ApiMetrics.MaintenanceWindowOperations.Add(1, new("operation", "create"), new("result", "success"));

        return CreatedAtAction(nameof(Get), new { id = created.Id }, Project(created));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, UpdateMaintenanceWindowRequest request, CancellationToken ct)
    {
        if (!TryBuildDraft(request.Name, request.Description, request.IsEnabled, request.Mode, request.ScopeKind,
                request.Recurrence, request.OneTimeStartUtc, request.OneTimeEndUtc, request.WeeklyDaysMask,
                request.WeeklyStartMinuteOfDay, request.WeeklyEndMinuteOfDay, request.CronExpression,
                request.DurationMinutes, request.TimeZoneId, request.Targets, out var draft, out var error))
            return BadRequest(new { message = error });

        try { await _store.UpdateAsync(id, draft, this.GetCurrentUsername(), ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        await _evaluator.RefreshAsync(ct);

        await _audit.LogAsync(AuditActions.MaintenanceWindowUpdated, "MaintenanceWindow", id,
            AuditDetails.Json(("name", draft.Name), ("mode", draft.Mode.ToString()),
                ("scopeKind", draft.ScopeKind.ToString()), ("enabled", draft.IsEnabled)), ct);
        ApiMetrics.MaintenanceWindowOperations.Add(1, new("operation", "update"), new("result", "success"));

        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try { await _store.DeleteAsync(id, ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        await _evaluator.RefreshAsync(ct);

        await _audit.LogAsync(AuditActions.MaintenanceWindowDeleted, "MaintenanceWindow", id, null, ct);
        ApiMetrics.MaintenanceWindowOperations.Add(1, new("operation", "delete"), new("result", "success"));

        return NoContent();
    }

    // Validates + maps a request into a MaintenanceWindow draft. Returns false + a message on
    // any user-input error so the controller can surface a clean 400.
    private static bool TryBuildDraft(
        string name, string? description, bool isEnabled, string modeRaw, string scopeRaw, string recurrenceRaw,
        DateTime? oneTimeStart, DateTime? oneTimeEnd, int weeklyDaysMask, int? weeklyStart, int? weeklyEnd,
        string? cron, int? durationMinutes, string? timeZoneId, IReadOnlyList<MaintenanceWindowTargetDto>? targets,
        out MaintenanceWindow draft, out string error)
    {
        draft = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            return Fail("Name is required and must be 100 characters or less", out error);
        if (description is { Length: > 500 })
            return Fail("Description must be 500 characters or less", out error);

        if (!Enum.TryParse<MaintenanceMode>(modeRaw, ignoreCase: true, out var mode))
            return Fail($"Invalid mode '{modeRaw}' (expected Blackout or AllowOnly)", out error);
        if (!Enum.TryParse<MaintenanceScopeKind>(scopeRaw, ignoreCase: true, out var scope))
            return Fail($"Invalid scopeKind '{scopeRaw}' (expected Global, Folders or Workflows)", out error);
        if (!Enum.TryParse<MaintenanceRecurrenceKind>(recurrenceRaw, ignoreCase: true, out var recurrence))
            return Fail($"Invalid recurrence '{recurrenceRaw}' (expected OneTime, Weekly or Cron)", out error);

        // Accept IANA (Europe/Berlin) and Windows (W. Europe Standard Time) ids interchangeably —
        // the UI defaults to the browser's IANA zone, which raw FindSystemTimeZoneById may reject
        // on a Windows host.
        var tz = string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId.Trim();
        if (!NodePilot.Core.Time.TimeZoneResolver.TryResolve(tz, out _))
            return Fail($"Unknown time zone '{tz}'", out error);

        if (recurrence == MaintenanceRecurrenceKind.OneTime)
        {
            if (oneTimeStart is null || oneTimeEnd is null)
                return Fail("OneTime windows require oneTimeStartUtc and oneTimeEndUtc", out error);
            if (oneTimeEnd <= oneTimeStart)
                return Fail("oneTimeEndUtc must be after oneTimeStartUtc", out error);
        }
        else if (recurrence == MaintenanceRecurrenceKind.Weekly)
        {
            if (weeklyStart is null || weeklyEnd is null)
                return Fail("Weekly windows require weeklyStartMinuteOfDay and weeklyEndMinuteOfDay", out error);
            if (weeklyStart is < 0 or > 1439 || weeklyEnd is < 0 or > 1439)
                return Fail("Weekly minute-of-day values must be between 0 and 1439", out error);
            // start == end is allowed and means a full 24h window for the selected weekdays.
            if ((weeklyDaysMask & 0b111_1111) == 0)
                return Fail("Weekly windows require at least one weekday in weeklyDaysMask", out error);
        }
        else if (recurrence == MaintenanceRecurrenceKind.Cron)
        {
            if (string.IsNullOrWhiteSpace(cron))
                return Fail("Cron windows require a cronExpression", out error);
            if (!Quartz.CronExpression.IsValidExpression(cron.Trim()))
                return Fail($"Invalid Quartz cron expression '{cron.Trim()}'", out error);
            if (durationMinutes is null or <= 0)
                return Fail("Cron windows require durationMinutes greater than 0", out error);
            if (durationMinutes > MaxCronDurationMinutes)
                return Fail($"durationMinutes must be at most {MaxCronDurationMinutes} (7 days)", out error);
        }

        var mappedTargets = new List<MaintenanceWindowTarget>();
        if (scope != MaintenanceScopeKind.Global)
        {
            var expectedKind = scope == MaintenanceScopeKind.Folders
                ? MaintenanceTargetKind.Folder
                : MaintenanceTargetKind.Workflow;
            if (targets is null || targets.Count == 0)
                return Fail($"{scope} windows require at least one target", out error);
            foreach (var t in targets)
            {
                if (!Enum.TryParse<MaintenanceTargetKind>(t.TargetKind, ignoreCase: true, out var kind))
                    return Fail($"Invalid target kind '{t.TargetKind}'", out error);
                if (kind != expectedKind)
                    return Fail($"{scope} windows may only contain {expectedKind} targets", out error);
                if (t.TargetId == Guid.Empty)
                    return Fail("Target id must not be empty", out error);
                mappedTargets.Add(new MaintenanceWindowTarget { TargetKind = kind, TargetId = t.TargetId });
            }
        }

        draft = new MaintenanceWindow
        {
            Name = name.Trim(),
            Description = description,
            IsEnabled = isEnabled,
            Mode = mode,
            ScopeKind = scope,
            Recurrence = recurrence,
            OneTimeStartUtc = recurrence == MaintenanceRecurrenceKind.OneTime ? oneTimeStart : null,
            OneTimeEndUtc = recurrence == MaintenanceRecurrenceKind.OneTime ? oneTimeEnd : null,
            WeeklyDaysMask = recurrence == MaintenanceRecurrenceKind.Weekly ? (weeklyDaysMask & 0b111_1111) : 0,
            WeeklyStartMinuteOfDay = recurrence == MaintenanceRecurrenceKind.Weekly ? weeklyStart : null,
            WeeklyEndMinuteOfDay = recurrence == MaintenanceRecurrenceKind.Weekly ? weeklyEnd : null,
            CronExpression = recurrence == MaintenanceRecurrenceKind.Cron ? cron!.Trim() : null,
            DurationMinutes = recurrence == MaintenanceRecurrenceKind.Cron ? durationMinutes : null,
            TimeZoneId = tz,
            Targets = mappedTargets,
        };
        return true;
    }

    private static bool Fail(string message, out string error) { error = message; return false; }

    private static MaintenanceWindowResponse Project(MaintenanceWindow w) => new(
        w.Id, w.Name, w.Description, w.IsEnabled,
        w.Mode.ToString(), w.ScopeKind.ToString(), w.Recurrence.ToString(),
        w.OneTimeStartUtc, w.OneTimeEndUtc,
        w.WeeklyDaysMask, w.WeeklyStartMinuteOfDay, w.WeeklyEndMinuteOfDay,
        w.CronExpression, w.DurationMinutes, w.TimeZoneId,
        w.Targets.Select(t => new MaintenanceWindowTargetDto(t.TargetKind.ToString(), t.TargetId)).ToList(),
        w.CreatedAt, w.UpdatedAt, w.UpdatedBy);
}
