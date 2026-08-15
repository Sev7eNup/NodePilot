namespace NodePilot.Cli.Api.Dtos;

// Mirror of the API's alerting DTOs (NodePilot.Api.Dtos.AlertingDtos). Duplicated by convention —
// the CLI takes no ProjectReference on the API. camelCase over the wire via JsonSerializerDefaults.Web.

public sealed record NotificationRouteDto(Guid? Id, string Channel, string Target, string? Secret, int Order, string? ConditionExpressionJson = null);

public sealed record NotificationRuleTargetDto(string TargetKind, Guid TargetId);

public sealed record NotificationRuleResponse(
    Guid Id, string Name, string? Description, bool IsEnabled,
    List<string> EventTypes, string? FilterExpressionJson, string ScopeKind,
    int CooldownMinutes, int MinOccurrences, int OccurrenceWindowMinutes,
    List<NotificationRouteDto> Routes, List<NotificationRuleTargetDto> Targets,
    DateTime CreatedAt, DateTime UpdatedAt, string? UpdatedBy, string? DedupKeyTemplate = null);

// One request shape serves both create + update (the server endpoints take identical bodies).
public sealed record SaveNotificationRuleRequest(
    string Name, string? Description, bool IsEnabled,
    List<string> EventTypes, string? FilterExpressionJson, string ScopeKind,
    int CooldownMinutes, int MinOccurrences, int OccurrenceWindowMinutes,
    List<NotificationRouteDto>? Routes, List<NotificationRuleTargetDto>? Targets,
    string? DedupKeyTemplate = null);

public sealed record TestFireRouteResult(string Channel, string Target, bool Success, string? Error);

public sealed record TestFireResponse(bool AllSucceeded, List<TestFireRouteResult> Results);

// The rule-authoring catalog: which event types exist, which fields a filter may reference, and
// which channels this installation can actually deliver on. `np alerting create` takes a rule as
// JSON, so without this the field names had to be read out of the web UI or the source.
public sealed record AlertingCatalogFieldDto(string Name, string Applies, string Type, IReadOnlyList<string>? Values = null);

public sealed record AlertingCatalogEventTypeDto(string Name, string Category, bool Scopeable);

public sealed record AlertingCatalogResponse(
    IReadOnlyList<AlertingCatalogEventTypeDto> EventTypes,
    IReadOnlyList<AlertingCatalogFieldDto> EventFields,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> DedupTemplateFields);

public sealed record NotificationDeliveryDto(
    Guid Id, Guid RuleId, string? RuleName, Guid RouteId, string? Channel, string? Target,
    string EventKey, string Status, int Attempt, DateTime CreatedAt, DateTime? SentAt,
    string? Error, bool IsTest, string? Summary);
