namespace NodePilot.Mcp.Api.Dtos;

// Mirror of the API's system-alert DTOs. "System-alert policies" (ADR 0008) are the built-in
// infra/health alerts (e.g. backlog too high, a machine unreachable, a credential about to
// expire) — a fixed catalog of sources an admin enables and tunes, as opposed to the free-form
// custom rules in AlertingDtos.cs. Duplicated by convention (MCP is HTTP-only against the REST
// API). Reuses NotificationRouteDto/NotificationRuleTargetDto/TestFireResponse from AlertingDtos.cs.

public sealed record SystemAlertCatalogResponse(List<SystemAlertSourceDto> Sources);

public sealed record SystemAlertSourceDto(
    string SourceId,
    string Category,
    string ScopeCapability,
    string DefaultSeverity,
    List<SystemAlertFieldDto> Fields,
    List<SystemAlertParameterDto> Parameters,
    List<SystemAlertPresetDto> Presets,
    bool Available);

public sealed record SystemAlertFieldDto(
    string Name,
    string Type,
    List<string> Operators,
    string? Unit,
    List<string>? EnumValues);

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
    Dictionary<string, object?>? Parameters);

public sealed record SystemAlertPolicyResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsEnabled,
    string SourceId,
    string? PresetId,
    Dictionary<string, object?>? SourceParameters,
    string? ConditionJson,
    int SustainForSeconds,
    string? SeverityOverride,
    string ScopeKind,
    List<NotificationRuleTargetDto> Targets,
    List<NotificationRouteDto> Routes,
    int CooldownMinutes,
    int MinOccurrences,
    int OccurrenceWindowMinutes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? UpdatedBy,
    DateTime? ActivatedAt);

public sealed record SaveSystemAlertPolicyRequest(
    string Name,
    string? Description,
    bool IsEnabled,
    string SourceId,
    string? PresetId,
    Dictionary<string, object?>? SourceParameters,
    string? ConditionJson,
    int SustainForSeconds,
    string? SeverityOverride,
    string ScopeKind,
    List<NotificationRuleTargetDto>? Targets,
    List<NotificationRouteDto>? Routes,
    int CooldownMinutes,
    int MinOccurrences,
    int OccurrenceWindowMinutes);
