using System.ComponentModel.DataAnnotations;

namespace NodePilot.Api.Dtos.Settings;

/// <summary>
/// Retention section DTO. Mirrors <see cref="NodePilot.Scheduler.Options.RetentionOptions"/>
/// in flat shape — each of the three sweepers (Executions / AuditLog / WorkflowVersions)
/// is a sub-object on this DTO. No secret fields, so the whole shape is readable
/// without sentinel handling.
/// </summary>
public sealed class RetentionSettingsDto : IValidatableObject
{
    [Required]
    public ExecutionsRetentionDto Executions { get; set; } = new();
    [Required]
    public AuditLogRetentionDto AuditLog { get; set; } = new();
    [Required]
    public WorkflowVersionsRetentionDto WorkflowVersions { get; set; } = new();

    /// <summary>
    /// <c>Validator.TryValidateObject</c> does NOT recurse into nested object
    /// properties by default — Range/Required attributes on the three sub-DTOs would
    /// otherwise be silently ignored at the controller's pre-flight check. Delegate
    /// recursive validation explicitly so a single Save with an out-of-range MaxAgeDays
    /// produces a 400 instead of being persisted.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var r in ValidateChild(Executions,       nameof(Executions))) yield return r;
        foreach (var r in ValidateChild(AuditLog,         nameof(AuditLog))) yield return r;
        foreach (var r in ValidateChild(WorkflowVersions, nameof(WorkflowVersions))) yield return r;
    }

    private static IEnumerable<ValidationResult> ValidateChild(object child, string prefix)
    {
        var ctx = new ValidationContext(child);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(child, ctx, results, validateAllProperties: true);
        foreach (var r in results)
        {
            yield return new ValidationResult(
                r.ErrorMessage,
                r.MemberNames.Select(m => $"{prefix}.{m}"));
        }
    }
}

public sealed class ExecutionsRetentionDto
{
    public bool Enabled { get; set; } = true;

    /// <summary>Older executions are pruned. 1 day–10 years bounds the practical range; anything outside
    /// that is almost certainly a typo or a misunderstood unit.</summary>
    [Range(1, 3650)]
    public int MaxAgeDays { get; set; } = 30;

    /// <summary>Sweep interval. 1 minute – 24 hours.</summary>
    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Rows deleted per sweep iteration. 1 – 10 000 covers both small dev DBs and high-volume prod.</summary>
    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 500;

    /// <summary>Optional directory for NDJSON archival of deleted rows. Empty/null = no archive.</summary>
    [StringLength(1024)]
    public string? ArchivePath { get; set; }
}

public sealed class AuditLogRetentionDto
{
    public bool Enabled { get; set; } = true;

    [Range(1, 3650)]
    public int MaxAgeDays { get; set; } = 365;

    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 720;

    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 1000;

    [StringLength(1024)]
    public string? ArchivePath { get; set; }
}

public sealed class WorkflowVersionsRetentionDto
{
    public bool Enabled { get; set; } = true;

    [Range(1, 10_000)]
    public int MaxVersionsPerWorkflow { get; set; } = 50;

    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 1440;

    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 500;
}
