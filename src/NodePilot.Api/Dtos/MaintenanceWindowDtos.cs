namespace NodePilot.Api.Dtos;

/// <summary>
/// API shapes for maintenance windows. Enum-typed fields travel as strings (Mode/ScopeKind/
/// Recurrence/TargetKind) so the React form and the CLI can speak them without sharing the
/// Core enums; the controller parses + validates them.
/// </summary>
public record MaintenanceWindowTargetDto(string TargetKind, Guid TargetId);

public record MaintenanceWindowResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsEnabled,
    string Mode,
    string ScopeKind,
    string Recurrence,
    DateTime? OneTimeStartUtc,
    DateTime? OneTimeEndUtc,
    int WeeklyDaysMask,
    int? WeeklyStartMinuteOfDay,
    int? WeeklyEndMinuteOfDay,
    string? CronExpression,
    int? DurationMinutes,
    string TimeZoneId,
    IReadOnlyList<MaintenanceWindowTargetDto> Targets,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? UpdatedBy);

public record CreateMaintenanceWindowRequest(
    string Name,
    string? Description,
    bool IsEnabled,
    string Mode,
    string ScopeKind,
    string Recurrence,
    DateTime? OneTimeStartUtc,
    DateTime? OneTimeEndUtc,
    int WeeklyDaysMask,
    int? WeeklyStartMinuteOfDay,
    int? WeeklyEndMinuteOfDay,
    string? CronExpression,
    int? DurationMinutes,
    string? TimeZoneId,
    IReadOnlyList<MaintenanceWindowTargetDto>? Targets);

public record UpdateMaintenanceWindowRequest(
    string Name,
    string? Description,
    bool IsEnabled,
    string Mode,
    string ScopeKind,
    string Recurrence,
    DateTime? OneTimeStartUtc,
    DateTime? OneTimeEndUtc,
    int WeeklyDaysMask,
    int? WeeklyStartMinuteOfDay,
    int? WeeklyEndMinuteOfDay,
    string? CronExpression,
    int? DurationMinutes,
    string? TimeZoneId,
    IReadOnlyList<MaintenanceWindowTargetDto>? Targets);

/// <summary>Read-only "which windows affect this workflow" badge entry.</summary>
public record MaintenanceWindowAffectingDto(Guid Id, string Name, string Mode, bool IsEnabled, bool ActiveNow);
