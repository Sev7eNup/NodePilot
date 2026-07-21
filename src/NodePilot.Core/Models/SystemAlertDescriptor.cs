using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

/// <summary>
/// Canonical operator sets per <see cref="SystemAlertFieldType"/>. These mirror the comparison operators the
/// workflow-edge <c>ConditionEvaluator</c> understands, so a source's declared field operators, the strict
/// alerting AST validator (added in a later phase), and the UI all agree on what is offered for a field.
/// </summary>
public static class SystemAlertOperators
{
    public static readonly IReadOnlyList<string> Numeric = ["==", "!=", "<", ">", "<=", ">="];
    public static readonly IReadOnlyList<string> Text = ["==", "!=", "contains", "startsWith", "endsWith", "matches", "isEmpty", "isNotEmpty"];
    public static readonly IReadOnlyList<string> Boolean = ["isTrue", "isFalse", "==", "!="];
    public static readonly IReadOnlyList<string> Enumeration = ["==", "!=", "isEmpty", "isNotEmpty"];

    /// <summary>The default operator set for a field of the given type.</summary>
    public static IReadOnlyList<string> For(SystemAlertFieldType type) => type switch
    {
        SystemAlertFieldType.Number => Numeric,
        SystemAlertFieldType.Duration => Numeric,
        SystemAlertFieldType.Boolean => Boolean,
        SystemAlertFieldType.Enum => Enumeration,
        _ => Text,
    };
}

/// <summary>
/// One filterable/comparable field a system-alert source exposes on its observations. <see cref="Name"/> is
/// the stable key a policy's condition AST references (<c>source:"event"</c> operand) and the UI i18n key
/// suffix — never localized text.
/// </summary>
public sealed record SystemAlertField(
    string Name,
    SystemAlertFieldType Type,
    IReadOnlyList<string> Operators,
    string? Unit = null,
    IReadOnlyList<string>? EnumValues = null)
{
    /// <summary>Convenience factory: a field whose operator set is the canonical set for its type.</summary>
    public static SystemAlertField Of(string name, SystemAlertFieldType type, string? unit = null, IReadOnlyList<string>? enumValues = null)
        => new(name, type, SystemAlertOperators.For(type), unit, enumValues);
}

/// <summary>
/// A source-specific query parameter (e.g. a cancel-rate lookback window). Persisted per policy in
/// <c>SourceParametersJson</c> and descriptor-validated on save. Distinct from the condition filter: a
/// parameter shapes *what the source measures*, the filter decides *whether the measurement alerts*.
/// </summary>
public sealed record SystemAlertParameter(
    string Name,
    SystemAlertFieldType Type,
    object? Default = null,
    bool Required = false,
    string? Unit = null,
    double? Min = null,
    double? Max = null);

/// <summary>
/// A named default policy shape a source ships as a starting point when an operator configures a policy.
/// Never auto-activated — a preset only pre-fills the editor.
/// </summary>
public sealed record SystemAlertPreset(
    string PresetId,
    NotificationSeverity Severity,
    int SustainForSeconds,
    string? ConditionJson = null,
    IReadOnlyDictionary<string, object?>? Parameters = null);

/// <summary>
/// Pure metadata describing one system-alert source — the pluggable design from ADR 0008 that replaced the
/// old hard-coded metric/gauge checks: its stable id, category, scope capability, default severity, the field
/// schema its observations satisfy, its query parameters, and default presets. Carries no display text — the
/// UI localizes via i18n keys derived from <see cref="SourceId"/> and field names, keeping DE/EN parity a
/// frontend concern.
/// </summary>
public sealed record SystemAlertSourceDescriptor(
    string SourceId,
    SystemAlertCategory Category,
    SystemAlertScopeCapability ScopeCapability,
    NotificationSeverity DefaultSeverity,
    IReadOnlyList<SystemAlertField> Fields,
    IReadOnlyList<SystemAlertParameter> Parameters,
    IReadOnlyList<SystemAlertPreset> Presets);
