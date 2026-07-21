using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Conditions;

namespace NodePilot.Engine.Notifications;

/// <summary>
/// Central alerting rule semantics: event/scope/filter/route matching and dedup-key rendering.
/// This keeps the dispatcher, API previews, and tests crossing the same interface.
/// </summary>
public static class NotificationRuleSemantics
{
    public const int MaxDedupKeyLength = 300;

    private static readonly IReadOnlyDictionary<string, ActivityResult> EmptyResults =
        new Dictionary<string, ActivityResult>();

    private static readonly Regex TemplateTokenRegex =
        new(@"\{\{\s*(?:event\.)?([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    // Event types a CUSTOM rule may react to. Infra/signal types (ServiceStale, MachineUnreachable,
    // BacklogHigh, PendingHigh, CancelRateHigh, CredentialExpiring, ScheduleMissed, WorkflowNoRecentSuccess)
    // were removed here when the legacy gauge path was retired (ADR 0008 — one pipeline): those signals are
    // now system policies over ISystemAlertSources. Their enum values remain (append-only persisted contract),
    // just no longer offered on the custom-rule surface. What stays are the execution-family events custom
    // rules filter over freely.
    public static readonly NotificationEventType[] SupportedEventTypes =
    [
        NotificationEventType.ExecutionFailed,
        NotificationEventType.ExecutionSucceeded,
        NotificationEventType.ExecutionCancelled,
        NotificationEventType.ExecutionRunningLong,
        NotificationEventType.ExecutionQueuedLong,
        NotificationEventType.CredentialFailure,
    ];

    // Global-only signal events. Workflow-scoped signal events (ScheduleMissed,
    // WorkflowNoRecentSuccess) are still collected by gauge providers, but they carry
    // workflow/folder context and therefore must not be blocked from scoped rules.
    public static readonly HashSet<NotificationEventType> GaugeEventTypes =
    [
        NotificationEventType.ServiceStale,
        NotificationEventType.MachineUnreachable,
        NotificationEventType.BacklogHigh,
        NotificationEventType.PendingHigh,
        NotificationEventType.CancelRateHigh,
        NotificationEventType.CredentialExpiring,
    ];

    public static bool EventTypeMatches(NotificationRule rule, NotificationEventType type)
    {
        if (string.IsNullOrWhiteSpace(rule.EventTypes)) return false;
        foreach (var part in rule.EventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (string.Equals(part, type.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool RuleWants(NotificationRule rule, IReadOnlyCollection<string> eventTypeNames)
    {
        if (string.IsNullOrWhiteSpace(rule.EventTypes)) return false;
        foreach (var part in rule.EventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (eventTypeNames.Contains(part)) return true;
        return false;
    }

    public static bool RuleWants(NotificationRule rule, NotificationEventType eventType)
        => EventTypeMatches(rule, eventType);

    public static bool ScopeMatches(NotificationRule rule, NotificationContext ctx) => rule.ScopeKind switch
    {
        NotificationScopeKind.Global => true,
        NotificationScopeKind.Folders => ctx.FolderId is { } f && rule.Targets.Any(t => t.TargetKind == NotificationTargetKind.Folder && t.TargetId == f),
        NotificationScopeKind.Workflows => ctx.WorkflowId is { } w && rule.Targets.Any(t => t.TargetKind == NotificationTargetKind.Workflow && t.TargetId == w),
        _ => false,
    };

    public static bool RuleFilterMatches(NotificationRule rule, NotificationContext ctx)
        => ConditionJsonMatches(rule.FilterExpressionJson, ctx);

    public static bool RouteFilterMatches(NotificationRoute route, NotificationContext ctx)
        => ConditionJsonMatches(route.ConditionExpressionJson, ctx);

    public static bool RuleMatches(NotificationRule rule, NotificationContext ctx)
        => EventTypeMatches(rule, ctx.EventType)
           && ScopeMatches(rule, ctx)
           && RuleFilterMatches(rule, ctx);

    public static IReadOnlyList<NotificationRoute> MatchingRoutes(NotificationRule rule, NotificationContext ctx)
    {
        var matches = new List<NotificationRoute>();
        foreach (var route in rule.Routes.OrderBy(r => r.Order))
            if (RouteFilterMatches(route, ctx))
                matches.Add(route);
        return matches;
    }

    public static string BuildDedupKey(NotificationRule rule, NotificationContext ctx)
    {
        var rendered = string.IsNullOrWhiteSpace(rule.DedupKeyTemplate)
            ? DefaultDedupKey(rule, ctx)
            : RenderTemplate(rule.DedupKeyTemplate!, ctx);

        if (string.IsNullOrWhiteSpace(rendered))
            rendered = DefaultDedupKey(rule, ctx);

        return FitDedupKey(rendered);
    }

    public static string RenderTemplate(string template, NotificationContext ctx)
    {
        var values = TemplateValues(ctx);
        return TemplateTokenRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return values.TryGetValue(key, out var value) ? value : "";
        });
    }

    public static bool TryValidateConditionJson(string? json, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return true;

        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            error = "condition expression is not valid JSON";
            return false;
        }
    }

    public static bool TryValidateDedupTemplate(string? template, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(template)) return true;
        if (template.Length > MaxDedupKeyLength)
        {
            error = $"dedupKeyTemplate must be {MaxDedupKeyLength} characters or less";
            return false;
        }

        // Compile-time sanity check for catastrophic token scans; unknown tokens are allowed and render empty.
        _ = TemplateTokenRegex.Replace(template, static m => m.Value);
        return true;
    }

    private static bool ConditionJsonMatches(string? json, NotificationContext ctx)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ConditionEvaluator.Evaluate(doc.RootElement,
                new ConditionContext(EmptyResults, null, null, null, ctx.ToFieldMap()));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string DefaultDedupKey(NotificationRule rule, NotificationContext ctx) => ctx.ExecutionId.HasValue
        ? $"{rule.Id}:{ctx.WorkflowId}:{ctx.EventType}"
        : $"{rule.Id}:{ctx.SourceKey}:{ctx.EventType}";

    /// <summary>
    /// Truncates an over-long key (e.g. a system-alert EventKey encoding a long instanceKey) to the 300-char
    /// column cap, appending a SHA-256 suffix so distinct keys stay distinct. Same discipline as the dedup-key
    /// fitter — exposed so callers that build their own EventKeys don't overflow the delivery-attempt insert.
    /// </summary>
    public static string FitKey(string key) => FitDedupKey(key);

    private static string FitDedupKey(string key)
    {
        if (key.Length <= MaxDedupKeyLength) return key;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        const int suffixLength = 17; // ":" + 16 hex chars
        var keep = Math.Max(0, MaxDedupKeyLength - suffixLength);
        return $"{key[..keep]}:{hash}";
    }

    private static IReadOnlyDictionary<string, string> TemplateValues(NotificationContext ctx)
    {
        var inv = CultureInfo.InvariantCulture;
        var values = new Dictionary<string, string>(ctx.ToFieldMap(), StringComparer.OrdinalIgnoreCase)
        {
            ["eventKey"] = ctx.EventKey,
            ["workflowId"] = ctx.WorkflowId?.ToString("D") ?? "",
            ["folderId"] = ctx.FolderId?.ToString("D") ?? "",
            ["executionId"] = ctx.ExecutionId?.ToString("D") ?? "",
            ["occurredAt"] = ctx.OccurredAt.ToString("O", inv),
        };
        return values;
    }
}
