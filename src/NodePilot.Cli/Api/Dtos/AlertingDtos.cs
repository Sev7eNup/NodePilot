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

public sealed record NotificationDeliveryDto(
    Guid Id, Guid RuleId, string? RuleName, Guid RouteId, string? Channel, string? Target,
    string EventKey, string Status, int Attempt, DateTime CreatedAt, DateTime? SentAt,
    string? Error, bool IsTest, string? Summary);
