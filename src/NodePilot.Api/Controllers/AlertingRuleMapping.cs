using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Notifications;

namespace NodePilot.Api.Controllers;

/// <summary>
/// The parts <see cref="AlertingController"/> (custom rules) and <see cref="SystemAlertingController"/>
/// (system policies) run identically over the same <see cref="NotificationRule"/> graph: route mapping,
/// scope-target mapping and the test-fire delivery loop. Only the noun inside two target-validation
/// messages differs between the surfaces ("rules" vs "policies"), so it is a parameter; every other
/// message, the route order and the ledger rows stay byte-identical.
/// </summary>
internal static class AlertingRuleMapping
{
    /// <summary>
    /// Validates and maps request routes onto <see cref="NotificationRoute"/> entities. Returns false
    /// with <paramref name="error"/> set on the first problem; the caller wraps that in its own 400
    /// shape. An absent/empty route list maps to an empty result — whether that is legal is the
    /// caller's rule (custom rules always need one, a disabled system policy does not).
    /// </summary>
    public static bool TryMapRoutes(
        IReadOnlyList<NotificationRouteDto>? routes,
        IReadOnlyDictionary<NotificationChannel, INotificationSink> sinks,
        out List<NotificationRoute> mapped,
        out string? error)
    {
        mapped = new List<NotificationRoute>();
        error = null;
        var order = 0;
        foreach (var rt in routes ?? [])
        {
            if (!Enum.TryParse<NotificationChannel>(rt.Channel, ignoreCase: true, out var channel))
            {
                error = $"Invalid channel '{rt.Channel}'";
                return false;
            }
            if (!sinks.ContainsKey(channel))
            {
                error = $"No delivery sink is registered for channel '{rt.Channel}' (available: {string.Join(", ", sinks.Keys)})";
                return false;
            }
            if (string.IsNullOrWhiteSpace(rt.Target))
            {
                error = "Each route requires a target";
                return false;
            }
            if (!NotificationRuleSemantics.TryValidateConditionJson(rt.ConditionExpressionJson, out var routeConditionError))
            {
                error = $"route conditionExpressionJson {routeConditionError}";
                return false;
            }
            mapped.Add(new NotificationRoute
            {
                Id = rt.Id ?? Guid.Empty,
                Channel = channel,
                Target = rt.Target.Trim(),
                Secret = rt.Secret, // store resolves unchanged-sentinel / encrypts plaintext
                ConditionExpressionJson = string.IsNullOrWhiteSpace(rt.ConditionExpressionJson) ? null : rt.ConditionExpressionJson,
                Order = order++,
            });
        }
        return true;
    }

    /// <summary>
    /// Maps the scope targets. Global scope carries none. <paramref name="noun"/> is the plural the
    /// caller's surface uses in its validation messages ("rules" / "policies") — the wording is part
    /// of the API contract, so it must not converge.
    /// </summary>
    public static bool TryMapScopeTargets(
        NotificationScopeKind scope,
        IReadOnlyList<NotificationRuleTargetDto>? targets,
        string noun,
        out List<NotificationRuleTarget> mapped,
        out string? error)
    {
        mapped = new List<NotificationRuleTarget>();
        error = null;
        if (scope == NotificationScopeKind.Global) return true;

        var expectedKind = scope == NotificationScopeKind.Folders
            ? NotificationTargetKind.Folder
            : NotificationTargetKind.Workflow;
        if (targets is null || targets.Count == 0)
        {
            error = $"{scope} {noun} require at least one target";
            return false;
        }
        foreach (var t in targets)
        {
            if (!Enum.TryParse<NotificationTargetKind>(t.TargetKind, ignoreCase: true, out var kind))
            {
                error = $"Invalid target kind '{t.TargetKind}'";
                return false;
            }
            if (kind != expectedKind)
            {
                error = $"{scope} {noun} may only contain {expectedKind} targets";
                return false;
            }
            if (t.TargetId == Guid.Empty)
            {
                error = "Target id must not be empty";
                return false;
            }
            mapped.Add(new NotificationRuleTarget { TargetKind = kind, TargetId = t.TargetId });
        }
        return true;
    }

    /// <summary>
    /// Sends the synthetic notification through every route of <paramref name="rule"/> and stages one
    /// <c>IsTest</c> delivery-ledger row per route. The caller keeps SaveChanges and the audit entry —
    /// those are the only parts that differ between the two test-fire endpoints.
    /// </summary>
    public static async Task<List<TestFireRouteResult>> DeliverTestFireAsync(
        NodePilotDbContext db,
        INotificationRuleStore store,
        IReadOnlyDictionary<NotificationChannel, INotificationSink> sinks,
        NotificationRule rule,
        NotificationContext ctx,
        CancellationToken ct)
    {
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
            if (!sinks.TryGetValue(route.Channel, out var sink))
            {
                result = NotificationSendResult.Fail($"no sink registered for channel {route.Channel}");
            }
            else
            {
                var secret = string.IsNullOrEmpty(route.Secret) ? null : await store.GetRouteSecretAsync(route.Id, ct);
                result = await sink.SendAsync(ctx, route.Target, secret, ct);
            }

            attempt.Status = result.Success ? NotificationDeliveryStatus.Sent : NotificationDeliveryStatus.Failed;
            attempt.Error = result.Error;
            attempt.Summary = $"[test] {route.Channel}:{route.Target}";
            db.NotificationDeliveryAttempts.Add(attempt);
            results.Add(new TestFireRouteResult(route.Channel.ToString(), route.Target, result.Success, result.Error));
        }
        return results;
    }
}
