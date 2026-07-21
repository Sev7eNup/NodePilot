namespace NodePilot.Cli.Api.Dtos;

// Mirror of the API's system-alert-policy DTOs (NodePilot.Api.Dtos; see ADR 0008, which replaced
// the old built-in gauge alerts with these configurable system alert policies). Duplicated by
// convention — the CLI takes no ProjectReference on the API. camelCase over the wire via
// JsonSerializerDefaults.Web. Routes/targets reuse NotificationRouteDto/NotificationRuleTargetDto
// from AlertingDtos.cs (identical shapes).

public sealed record SystemAlertPolicyResponse(
    Guid Id, string Name, string? Description, bool IsEnabled,
    string SourceId, string? PresetId, Dictionary<string, object?>? SourceParameters,
    string? ConditionJson, int SustainForSeconds, string? SeverityOverride, string ScopeKind,
    List<NotificationRuleTargetDto> Targets, List<NotificationRouteDto> Routes,
    int CooldownMinutes, int MinOccurrences, int OccurrenceWindowMinutes,
    DateTime CreatedAt, DateTime UpdatedAt, string? UpdatedBy, DateTime? ActivatedAt);

// One request shape serves both create + update (the server endpoints take identical bodies).
public sealed record SaveSystemAlertPolicyRequest(
    string Name, string? Description, bool IsEnabled,
    string SourceId, string? PresetId, Dictionary<string, object?>? SourceParameters,
    string? ConditionJson, int SustainForSeconds, string? SeverityOverride, string ScopeKind,
    List<NotificationRuleTargetDto>? Targets, List<NotificationRouteDto>? Routes,
    int CooldownMinutes, int MinOccurrences, int OccurrenceWindowMinutes);

// ---- Catalog (GET /api/alerting/system/catalog) -----------------------------

public sealed record SystemAlertCatalogResponse(List<SystemAlertSourceDto> Sources);

public sealed record SystemAlertSourceDto(
    string SourceId, string Category, string ScopeCapability, string? DefaultSeverity,
    List<SystemAlertFieldDto> Fields, List<SystemAlertParameterDto> Parameters,
    List<SystemAlertPresetDto> Presets, bool Available);

public sealed record SystemAlertFieldDto(
    string Name, string Type, List<string> Operators, string? Unit, List<string>? EnumValues);

public sealed record SystemAlertParameterDto(
    string Name, string Type, object? Default, bool Required, string? Unit, double? Min, double? Max);

public sealed record SystemAlertPresetDto(
    string PresetId, string? Severity, int SustainForSeconds, string? ConditionJson,
    Dictionary<string, object?>? Parameters);
