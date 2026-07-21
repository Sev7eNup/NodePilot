namespace NodePilot.Api.Dtos;

/// <summary>
/// The server-owned system-alert catalog (ADR 0008): the single source of truth for source metadata the
/// alerting UI renders from (fields, units, operators, parameters, presets, scope, availability). Enum-typed
/// backend values are projected to their string names so the wire contract is stable and human-readable.
/// </summary>
public sealed record SystemAlertCatalogResponse(IReadOnlyList<SystemAlertSourceDto> Sources);

/// <summary>One catalog source card. <c>Available</c> reflects a best-effort availability probe at read time.</summary>
public sealed record SystemAlertSourceDto(
    string SourceId,
    string Category,
    string ScopeCapability,
    string DefaultSeverity,
    IReadOnlyList<SystemAlertFieldDto> Fields,
    IReadOnlyList<SystemAlertParameterDto> Parameters,
    IReadOnlyList<SystemAlertPresetDto> Presets,
    bool Available);

public sealed record SystemAlertFieldDto(
    string Name,
    string Type,
    IReadOnlyList<string> Operators,
    string? Unit,
    IReadOnlyList<string>? EnumValues);

public sealed record SystemAlertParameterDto(
    string Name,
    string Type,
    object? Default,
    bool Required,
    string? Unit,
    double? Min,
    double? Max);

public sealed record SystemAlertPresetDto(
    string PresetId,
    string Severity,
    int SustainForSeconds,
    string? ConditionJson,
    IReadOnlyDictionary<string, object?>? Parameters);

/// <summary>A configured system-alert policy (ADR 0008). Reuses the shared route/target DTOs.</summary>
public sealed record SystemAlertPolicyResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsEnabled,
    string SourceId,
    string? PresetId,
    IReadOnlyDictionary<string, object?>? SourceParameters,
    string? ConditionJson,
    int SustainForSeconds,
    string? SeverityOverride,
    string ScopeKind,
    IReadOnlyList<NotificationRuleTargetDto> Targets,
    IReadOnlyList<NotificationRouteDto> Routes,
    int CooldownMinutes,
    int MinOccurrences,
    int OccurrenceWindowMinutes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? UpdatedBy,
    DateTime? ActivatedAt);

/// <summary>Create/update payload for a system-alert policy (one shape serves both).</summary>
public sealed record SaveSystemAlertPolicyRequest(
    string Name,
    string? Description,
    bool IsEnabled,
    string SourceId,
    string? PresetId,
    IReadOnlyDictionary<string, object?>? SourceParameters,
    string? ConditionJson,
    int SustainForSeconds,
    string? SeverityOverride,
    string ScopeKind,
    IReadOnlyList<NotificationRuleTargetDto>? Targets,
    IReadOnlyList<NotificationRouteDto>? Routes,
    int CooldownMinutes,
    int MinOccurrences,
    int OccurrenceWindowMinutes);

/// <summary>Stateless preview: sample the source now and report which current instances match the condition.</summary>
public sealed record SystemAlertPreviewRequest(
    string SourceId,
    IReadOnlyDictionary<string, object?>? SourceParameters,
    string? ConditionJson);

public sealed record SystemAlertPreviewMatch(
    string InstanceKey,
    string? Title,
    string? Summary,
    IReadOnlyDictionary<string, string> Fields,
    bool Matched);

public sealed record SystemAlertPreviewResponse(bool Available, IReadOnlyList<SystemAlertPreviewMatch> Matches);
