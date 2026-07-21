using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Conditions;
using NodePilot.Engine.Notifications;

namespace NodePilot.Scheduler.SystemAlerts;

/// <summary>One policy that should deliver for a given observation this pass.</summary>
public sealed record SystemAlertFire(NotificationRule Policy, NotificationContext Context);

/// <summary>
/// The system-alert policy evaluator — decides whether a built-in alert source's current reading should
/// actually fire a notification (this replaced a set of hard-coded per-source thresholds; see ADR 0008 for
/// the rationale). Each pass it groups the enabled System policies by (source, normalized parameters),
/// samples every distinct source once, and runs each policy's condition + sustain window against every
/// applicable observation — maintaining per-(policy, source, instance) match state in
/// <c>SystemAlertPolicyStates</c>. When a policy's condition holds continuously for its sustain window an
/// episode opens; while the episode is open the evaluator emits a fire every pass (the dispatcher's
/// (rule, route, eventKey) exactly-once guard dedups these), the same "keep re-emitting while true"
/// crash-safety approach the old gauge-based alerting system used.
///
/// <para>Sources decide nothing — the evaluator owns health. It stages state mutations on the tracked
/// context; the dispatcher's single SaveChanges flushes them together with the Pending attempts (the match
/// state and the delivery record are written together, before anything is actually sent). Recovery
/// (condition stops holding) is silent (no "resolved" alert in v1).</para>
/// </summary>
public sealed class SystemAlertEvaluator
{
    private static readonly IReadOnlyDictionary<string, ActivityResult> EmptyResults = new Dictionary<string, ActivityResult>();

    private readonly ISystemAlertCatalog _catalog;

    public SystemAlertEvaluator(ISystemAlertCatalog catalog) => _catalog = catalog;

    /// <summary>
    /// Evaluate every enabled System policy against current observations, staging match-state changes on
    /// <paramref name="db"/> and returning the deliveries that should fire this pass. Does not save.
    /// </summary>
    public async Task<IReadOnlyList<SystemAlertFire>> EvaluateAsync(
        NodePilotDbContext db, IReadOnlyList<NotificationRule> systemPolicies, DateTime now, CancellationToken ct)
    {
        var fires = new List<SystemAlertFire>();
        if (systemPolicies.Count == 0) return fires;

        // Group by source + normalized parameters so each distinct query is sampled once per pass.
        var groups = systemPolicies
            .Where(p => !string.IsNullOrWhiteSpace(p.SystemSourceId))
            .GroupBy(p => (SourceId: p.SystemSourceId!, ParamsKey: NormalizeParams(p.SourceParametersJson)));

        foreach (var group in groups)
        {
            var source = _catalog.Find(group.Key.SourceId);
            if (source is null) continue; // misconfigured policy (source removed) — skip, no crash

            if (!await source.IsAvailableAsync(db, ct)) continue; // unavailable → no alert, no recovery

            var query = BuildQuery(group.Key.ParamsKey);
            IReadOnlyList<SystemAlertObservation> observations;
            try { observations = await source.ObserveAsync(db, query, ct); }
            catch (OperationCanceledException) { throw; }
            catch { continue; } // a flaky source sample must not sink the whole pass

            foreach (var policy in group)
                fires.AddRange(await EvaluatePolicyAsync(db, policy, source.SourceId, observations, now, ct));
        }

        return fires;
    }

    private async Task<IReadOnlyList<SystemAlertFire>> EvaluatePolicyAsync(
        NodePilotDbContext db, NotificationRule policy, string sourceId,
        IReadOnlyList<SystemAlertObservation> observations, DateTime now, CancellationToken ct)
    {
        var fires = new List<SystemAlertFire>();

        var states = await db.SystemAlertPolicyStates
            .Where(s => s.NotificationRuleId == policy.Id && s.SourceId == sourceId)
            .ToListAsync(ct);
        var byInstance = states.ToDictionary(s => s.InstanceKey, StringComparer.Ordinal);

        foreach (var obs in observations)
        {
            if (!ScopeAllows(policy, obs)) continue;

            // Activation watermark (event sources): never back-alert an event that happened before the
            // policy was (re-)activated — even if a sibling policy already advanced the shared source cursor.
            if (obs.OccurredAt is { } occurred && policy.ActivatedAt is { } activated && occurred < activated)
                continue;

            var fieldMap = FieldMap(obs);
            var holds = Matches(policy.FilterExpressionJson, fieldMap);

            if (!byInstance.TryGetValue(obs.InstanceKey, out var state))
            {
                state = new SystemAlertPolicyState
                {
                    Id = Guid.NewGuid(),
                    NotificationRuleId = policy.Id,
                    SourceId = sourceId,
                    InstanceKey = obs.InstanceKey,
                };
                db.SystemAlertPolicyStates.Add(state);
                byInstance[obs.InstanceKey] = state;
            }
            state.LastObservedAt = now;

            if (holds)
            {
                if (!state.IsMatching)
                {
                    state.IsMatching = true;
                    state.MatchStartedAt = now;
                    state.EpisodeStartedAt = null;
                }

                var sustainOver = state.MatchStartedAt is { } started
                    && now >= started.AddSeconds(Math.Max(0, policy.SustainForSeconds));
                if (sustainOver)
                {
                    state.EpisodeStartedAt ??= now;
                    // Emit every pass while the episode is open; the delivery ledger's (rule,route,eventKey)
                    // guard makes this idempotent and recovers a persist-before-send crash on the next pass.
                    fires.Add(new SystemAlertFire(policy,
                        BuildContext(policy, obs, fieldMap, state.EpisodeStartedAt.Value, now)));
                }
            }
            else
            {
                state.IsMatching = false;
                state.MatchStartedAt = null;
                state.EpisodeStartedAt = null; // episode ends silently (no resolved alert in v1)
            }
        }

        return fires;
    }

    /// <summary>
    /// Best-effort rebuild of a system context from a crash-orphaned attempt's EventKey: re-sample the source
    /// and match the instance. Returns null when the key isn't ours, the source is gone/unavailable, or the
    /// instance no longer appears (the dispatcher then fails the attempt out).
    /// </summary>
    public async Task<NotificationContext?> TryReconstructContextAsync(NodePilotDbContext db, string eventKey, CancellationToken ct)
    {
        if (!TryParseEventKey(eventKey, out var sourceId, out var instanceKey, out var episodeStart))
            return null;

        var source = _catalog.Find(sourceId);
        if (source is null || !await source.IsAvailableAsync(db, ct)) return null;

        IReadOnlyList<SystemAlertObservation> observations;
        try { observations = await source.ObserveAsync(db, SystemAlertQuery.Empty, ct); }
        catch { return null; }

        var obs = observations.FirstOrDefault(o => string.Equals(o.InstanceKey, instanceKey, StringComparison.Ordinal));
        if (obs is null) return null;

        // The owning policy id is encoded in the key but the observation carries no severity override — a
        // reconstructed send uses the observation's suggested severity, which is acceptable for recovery.
        var reconstructed = BuildContext(policyId: ParsePolicyId(eventKey), obs, FieldMap(obs), episodeStart, DateTime.UtcNow);
        return reconstructed with { EventKey = eventKey };
    }

    /// <summary>Delete transient state for policies that are no longer enabled System policies (disable/delete reset).</summary>
    public async Task PruneOrphanedStateAsync(NodePilotDbContext db, IReadOnlyCollection<Guid> enabledSystemPolicyIds, CancellationToken ct)
    {
        await db.SystemAlertPolicyStates
            .Where(s => !enabledSystemPolicyIds.Contains(s.NotificationRuleId))
            .ExecuteDeleteAsync(ct);
    }

    // ---- helpers ----

    private static bool ScopeAllows(NotificationRule policy, SystemAlertObservation obs) => policy.ScopeKind switch
    {
        NotificationScopeKind.Global => true,
        NotificationScopeKind.Workflows => obs.WorkflowId is { } w
            && policy.Targets.Any(t => t.TargetKind == NotificationTargetKind.Workflow && t.TargetId == w),
        NotificationScopeKind.Folders => obs.FolderId is { } f
            && policy.Targets.Any(t => t.TargetKind == NotificationTargetKind.Folder && t.TargetId == f),
        _ => false,
    };

    /// <summary>
    /// Whether a policy's condition holds against an observation's fields. Empty condition matches everything.
    /// Shared with the stateless <c>preview</c> endpoint so what an operator sees previewed is exactly what
    /// the evaluator will decide.
    /// </summary>
    public static bool Matches(string? filterJson, IReadOnlyDictionary<string, string> fields)
    {
        if (string.IsNullOrWhiteSpace(filterJson)) return true;
        try
        {
            using var doc = JsonDocument.Parse(filterJson);
            return ConditionEvaluator.Evaluate(doc.RootElement, new ConditionContext(EmptyResults, null, null, null, fields));
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Flattens an observation's fields (plus its <c>sourceId</c>) to the string map conditions match against.</summary>
    public static Dictionary<string, string> FieldMap(SystemAlertObservation obs)
    {
        var inv = CultureInfo.InvariantCulture;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Exposed to policy conditions / route filters (e.g. sourceId == "backlog"). It travels in the
            // generic ExtraFields bag rather than as one of NotificationContext's built-in named fields,
            // since "sourceId" is only meaningful for system-alert policies, not custom rules.
            ["sourceId"] = obs.SourceId,
        };
        foreach (var (k, v) in obs.Fields)
            map[k] = v switch
            {
                null => "",
                bool b => b ? "true" : "false",
                IFormattable f => f.ToString(null, inv),
                _ => v.ToString() ?? "",
            };
        return map;
    }

    private static NotificationContext BuildContext(
        Guid policyId, SystemAlertObservation obs, IReadOnlyDictionary<string, string> fieldMap,
        DateTime episodeStart, DateTime now)
        => new(
            NotificationEventType.SystemAlert,
            NotificationSeverity.Warning, // overridden below when the policy/observation says otherwise
            EventKey: NotificationRuleSemantics.FitKey($"system:{policyId:N}:{obs.SourceId}:{obs.InstanceKey}:{episodeStart.Ticks}"),
            WorkflowId: obs.WorkflowId,
            WorkflowName: obs.WorkflowName,
            FolderId: obs.FolderId,
            FolderPath: obs.FolderPath,
            ExecutionId: null,
            Status: null,
            ErrorMessage: null,
            DurationMs: null,
            OccurredAt: now,
            TriggeredBy: null,
            CallDepth: 0,
            IsSubWorkflow: false,
            TargetMachine: obs.TargetMachine,
            SourceKey: obs.InstanceKey,
            Title: obs.Title,
            Summary: obs.Summary,
            DeepLinkPath: obs.DeepLinkPath,
            SignalValue: obs.SignalValue,
            CancelledBy: null,
            SourceId: obs.SourceId,
            ExtraFields: fieldMap);

    private static NotificationContext BuildContext(
        NotificationRule policy, SystemAlertObservation obs, IReadOnlyDictionary<string, string> fieldMap,
        DateTime episodeStart, DateTime now)
        => BuildContext(policy.Id, obs, fieldMap, episodeStart, now) with
        {
            Severity = policy.SeverityOverride ?? obs.SeveritySuggestion,
        };

    private static string NormalizeParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            // Re-serialize with sorted keys so semantically-equal parameter sets group together.
            var sorted = doc.RootElement.EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(p => p.Name, p => p.Value.ToString());
            return JsonSerializer.Serialize(sorted);
        }
        catch (JsonException) { return json; }
    }

    private static SystemAlertQuery BuildQuery(string paramsKey)
    {
        if (string.IsNullOrWhiteSpace(paramsKey)) return SystemAlertQuery.Empty;
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(paramsKey);
            return values is null ? SystemAlertQuery.Empty : new SystemAlertQuery(values);
        }
        catch (JsonException) { return SystemAlertQuery.Empty; }
    }

    private static bool TryParseEventKey(string eventKey, out string sourceId, out string instanceKey, out DateTime episodeStart)
    {
        sourceId = ""; instanceKey = ""; episodeStart = default;
        const string prefix = "system:";
        if (!eventKey.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = eventKey[prefix.Length..];
        if (rest.Length < 33 || rest[32] != ':') return false; // 32-hex ruleId + ':'
        rest = rest[33..];

        var firstColon = rest.IndexOf(':');
        var lastColon = rest.LastIndexOf(':');
        if (firstColon < 0 || lastColon <= firstColon) return false;

        sourceId = rest[..firstColon];
        instanceKey = rest[(firstColon + 1)..lastColon];
        return long.TryParse(rest[(lastColon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            && TryTicks(ticks, out episodeStart);
    }

    private static bool TryTicks(long ticks, out DateTime dt)
    {
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks) { dt = default; return false; }
        dt = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private static Guid ParsePolicyId(string eventKey)
    {
        var rest = eventKey["system:".Length..];
        return Guid.TryParseExact(rest[..32], "N", out var id) ? id : Guid.Empty;
    }
}
