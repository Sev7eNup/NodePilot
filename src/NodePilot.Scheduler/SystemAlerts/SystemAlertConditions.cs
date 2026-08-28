using System.Text.Json;

namespace NodePilot.Scheduler.SystemAlerts;

/// <summary>
/// Small helper for building the preset condition AST JSON strings a source ships. Emits the exact
/// shape
/// the workflow-edge <c>ConditionEvaluator</c> reads (a <c>comparison</c> node with a
/// <c>source:"event"</c>
/// left operand and a literal right operand), so presets stay valid against the same evaluator the
/// alerting filters run on.
/// </summary>
public static class SystemAlertConditions
{
    /// <summary>A binary comparison of an event field against a literal, e.g. <c>Compare("depth",
    /// "&gt;", "500")</c>.</summary>
    public static string Compare(string field, string op, string literal) => JsonSerializer.Serialize(new
    {
        type = "comparison",
        left = new { kind = "variable", source = "event", name = field },
        op,
        right = new { kind = "literal", value = literal },
    });

    /// <summary>A unary comparison of an event field, e.g. <c>Unary("reachable",
    /// "isFalse")</c>.</summary>
    public static string Unary(string field, string op) => JsonSerializer.Serialize(new
    {
        type = "comparison",
        left = new { kind = "variable", source = "event", name = field },
        op,
    });

    /// <summary>
    /// An OR-group of equality comparisons of one event field against each literal, e.g.
    /// <c>AnyOf("action", "USER_ROLE_CHANGED", "USER_DELETED")</c> — the <c>group</c> node shape
    /// the
    /// evaluator and the strict alerting validator both read.
    /// </summary>
    public static string AnyOf(string field, params string[] literals) => JsonSerializer.Serialize(new
    {
        type = "group",
        op = "OR",
        children = literals.Select(literal => new
        {
            type = "comparison",
            left = new { kind = "variable", source = "event", name = field },
            op = "==",
            right = new { kind = "literal", value = literal },
        }).ToArray(),
    });
}
