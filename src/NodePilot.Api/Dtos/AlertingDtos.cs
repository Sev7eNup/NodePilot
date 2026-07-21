namespace NodePilot.Api.Dtos;

/// <summary>
/// One delivery route of a rule. <see cref="Secret"/> is write-or-keep: responses return the
/// unchanged-sentinel when a secret is stored (never the cipher), and a request echoing the sentinel
/// keeps the stored secret. <see cref="Id"/> round-trips so an edit preserves the route's stored
/// secret without the client re-sending it.
/// </summary>
public record NotificationRouteDto(Guid? Id, string Channel, string Target, string? Secret, int Order, string? ConditionExpressionJson = null);

public record NotificationRuleTargetDto(string TargetKind, Guid TargetId);

public record NotificationRuleResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsEnabled,
    IReadOnlyList<string> EventTypes,
    string? FilterExpressionJson,
    string ScopeKind,
    int CooldownMinutes,
    int MinOccurrences,
    int OccurrenceWindowMinutes,
    IReadOnlyList<NotificationRouteDto> Routes,
    IReadOnlyList<NotificationRuleTargetDto> Targets,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? UpdatedBy,
    string? DedupKeyTemplate = null);

public record CreateNotificationRuleRequest(
    string Name,
    string? Description,
    bool IsEnabled,
    IReadOnlyList<string> EventTypes,
    string? FilterExpressionJson,
    string ScopeKind,
    int CooldownMinutes,
    int MinOccurrences,
    int OccurrenceWindowMinutes,
    IReadOnlyList<NotificationRouteDto>? Routes,
    IReadOnlyList<NotificationRuleTargetDto>? Targets,
    string? DedupKeyTemplate = null);

public record UpdateNotificationRuleRequest(
    string Name,
    string? Description,
    bool IsEnabled,
    IReadOnlyList<string> EventTypes,
    string? FilterExpressionJson,
    string ScopeKind,
    int CooldownMinutes,
    int MinOccurrences,
    int OccurrenceWindowMinutes,
    IReadOnlyList<NotificationRouteDto>? Routes,
    IReadOnlyList<NotificationRuleTargetDto>? Targets,
    string? DedupKeyTemplate = null);

/// <summary>Per-route outcome of a test-fire.</summary>
public record TestFireRouteResult(string Channel, string Target, bool Success, string? Error);

public record TestFireResponse(bool AllSucceeded, IReadOnlyList<TestFireRouteResult> Results);

/// <summary>One row of the delivery ledger (read-only). No secrets — only channel + target.</summary>
public record NotificationDeliveryDto(
    Guid Id,
    Guid RuleId,
    string? RuleName,
    Guid RouteId,
    string? Channel,
    string? Target,
    string EventKey,
    string Status,
    int Attempt,
    DateTime CreatedAt,
    DateTime? SentAt,
    string? Error,
    bool IsTest,
    string? Summary);

/// <summary>Stateless dry-run of a filter expression against a sample event-field map.</summary>
public record PreviewFilterRequest(string? FilterExpressionJson, IReadOnlyDictionary<string, string>? EventFields);

public record PreviewFilterResponse(bool Matches, string? Error);

public record AlertingCatalogFieldDto(string Name, string Applies, string Type, IReadOnlyList<string>? Values = null);

public record AlertingCatalogEventTypeDto(string Name, string Category, bool Scopeable);

public record AlertingCatalogResponse(
    IReadOnlyList<AlertingCatalogEventTypeDto> EventTypes,
    IReadOnlyList<AlertingCatalogFieldDto> EventFields,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> DedupTemplateFields);

public record PreviewRuleRequest(
    IReadOnlyList<string> EventTypes,
    string? FilterExpressionJson,
    string ScopeKind,
    IReadOnlyList<NotificationRouteDto>? Routes,
    IReadOnlyList<NotificationRuleTargetDto>? Targets,
    string? DedupKeyTemplate,
    IReadOnlyDictionary<string, string>? EventFields);

public record PreviewRouteResult(string Channel, string Target, bool Matches);

public record PreviewRuleResponse(
    bool MatchesRule,
    string? DedupKey,
    IReadOnlyList<PreviewRouteResult> Routes,
    IReadOnlyList<string> Reasons);
