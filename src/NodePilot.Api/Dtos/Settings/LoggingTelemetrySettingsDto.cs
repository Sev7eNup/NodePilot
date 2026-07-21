using System.ComponentModel.DataAnnotations;

namespace NodePilot.Api.Dtos.Settings;

// Three DTOs live in this file because the UI groups them under a single "Logging &
// Telemetry" tab even though they're separate top-level config roots (Logging,
// OpenTelemetry, Stats). Splitting into three Schema sections + three save buttons is
// intentional — each block is independently validated and the operator can save one
// without re-touching the others.

public sealed class LoggingSettingsDto : IValidatableObject
{
    /// <summary>Rolling-file format. Allowed: <c>text</c>, <c>cmtrace</c>, <c>json</c>, <c>ecs-json</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Format { get; set; } = "text";

    [Required]
    public LogLevelsDto LogLevel { get; set; } = new();

    [Required]
    public StepDetailDto StepDetail { get; set; } = new();

    [Required]
    public FileSinkDto File { get; set; } = new();

    [Required]
    public RedactionDto Redaction { get; set; } = new();

    [Required]
    public SupportLogDto SupportLog { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var r in ValidateChild(LogLevel, nameof(LogLevel))) yield return r;
        foreach (var r in ValidateChild(StepDetail, nameof(StepDetail))) yield return r;
        foreach (var r in ValidateChild(File, nameof(File))) yield return r;
        foreach (var r in ValidateChild(Redaction, nameof(Redaction))) yield return r;
        foreach (var r in ValidateChild(SupportLog, nameof(SupportLog))) yield return r;
    }

    internal static IEnumerable<ValidationResult> ValidateChild(object child, string prefix)
    {
        var ctx = new ValidationContext(child);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(child, ctx, results, validateAllProperties: true);
        foreach (var r in results)
            yield return new ValidationResult(r.ErrorMessage, r.MemberNames.Select(m => $"{prefix}.{m}"));
    }
}

public sealed class LogLevelsDto
{
    [Required] public string Default { get; set; } = "Warning";
    [Required] public string AspNetCore { get; set; } = "Warning";
    [Required] public string EfCoreCommand { get; set; } = "Warning";
    [Required] public string EfCoreConnection { get; set; } = "Warning";
    [Required] public string EfCoreInfrastructure { get; set; } = "Warning";
}

public sealed class StepDetailDto
{
    public bool Enabled { get; set; }

    [Range(100, 1_000_000)]
    public int MaxOutputChars { get; set; } = 10_000;
}

public sealed class FileSinkDto
{
    [Range(1, 365)]
    public int RetainedFileCountLimit { get; set; } = 7;

    [Range(1024L * 1024, 10L * 1024 * 1024 * 1024)]   // 1 MiB – 10 GiB
    public long FileSizeLimitBytes { get; set; } = 100L * 1024 * 1024;

    public bool Async { get; set; } = true;
}

public sealed class RedactionDto
{
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Configuration for the second Serilog sink (the "support log"). Writes a lean
/// plain-text file containing only the events relevant to support staff
/// (user log-in activity, workflow lifecycle, auth audits, system boot). The filter
/// is the LogContext property <c>SupportLog=true</c>.
/// </summary>
public sealed class SupportLogDto
{
    /// <summary>When false, the second sink is not registered. Boot-only — changes require an API restart.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional absolute or relative path. Empty → <c>{ContentRoot}/logs/nodepilot-support-.log</c>.</summary>
    [StringLength(2048)]
    public string Path { get; set; } = "";

    [Range(1, 365)]
    public int RetainedFileCountLimit { get; set; } = 90;

    [Range(1024L * 1024, 1024L * 1024 * 1024)]   // 1 MiB – 1 GiB
    public long FileSizeLimitBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>When false, the DB-projection sink is not registered.
    /// The plain-text file is unaffected — it has its own toggle, <see cref="Enabled"/>.</summary>
    public bool DbProjectionEnabled { get; set; } = true;
}

public sealed class OpenTelemetrySettingsDto : IValidatableObject
{
    public bool Enabled { get; set; }

    [Required(AllowEmptyStrings = false)]
    [StringLength(255)]
    public string ServiceName { get; set; } = "nodepilot-api";

    [Required(AllowEmptyStrings = false)]
    [StringLength(64)]
    public string Environment { get; set; } = "dev";

    public bool RedactHostnames { get; set; } = true;

    [Range(1, 3600)]
    public int MetricExportIntervalSeconds { get; set; } = 30;

    [Required] public OtlpSettingsDto Otlp { get; set; } = new();
    [Required] public SamplingSettingsDto Sampling { get; set; } = new();
    [Required] public ExportersSettingsDto Exporters { get; set; } = new();
    [Required] public TraceUiSettingsDto TraceUi { get; set; } = new();
    [Required] public PrometheusSettingsDto Prometheus { get; set; } = new();

    [Url]
    [StringLength(2048)]
    public string GrafanaBaseUrl { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var r in LoggingSettingsDto.ValidateChild(Otlp, nameof(Otlp))) yield return r;
        foreach (var r in LoggingSettingsDto.ValidateChild(Sampling, nameof(Sampling))) yield return r;
        foreach (var r in LoggingSettingsDto.ValidateChild(Exporters, nameof(Exporters))) yield return r;
        foreach (var r in LoggingSettingsDto.ValidateChild(TraceUi, nameof(TraceUi))) yield return r;
        foreach (var r in LoggingSettingsDto.ValidateChild(Prometheus, nameof(Prometheus))) yield return r;
    }
}

public sealed class OtlpSettingsDto
{
    [Required(AllowEmptyStrings = false)]
    [Url]
    [StringLength(2048)]
    public string Endpoint { get; set; } = "http://localhost:4317";

    /// <summary><c>grpc</c> or <c>http/protobuf</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Protocol { get; set; } = "grpc";

    [StringLength(2048)]
    public string Headers { get; set; } = "";

    [StringLength(2048)]
    public string BrowserEndpoint { get; set; } = "";
}

public sealed class SamplingSettingsDto
{
    [Required(AllowEmptyStrings = false)]
    public string Mode { get; set; } = "ParentBasedTraceIdRatio";

    [Range(0.0, 1.0)]
    public double Ratio { get; set; } = 1.0;
}

public sealed class ExportersSettingsDto
{
    public bool Traces { get; set; } = true;
    public bool Metrics { get; set; } = true;
    public bool Logs { get; set; } = true;
    public bool PrometheusScrape { get; set; }
    public bool PrometheusScrapeAllowAnonymous { get; set; }
}

public sealed class TraceUiSettingsDto
{
    [StringLength(2048)]
    public string UrlTemplate { get; set; } = "";

    [StringLength(64)]
    public string BackendName { get; set; } = "Tempo";
}

public sealed class PrometheusSettingsDto
{
    [StringLength(2048)]
    public string QueryEndpoint { get; set; } = "";

    [StringLength(255)]
    public string Username { get; set; } = "";

    /// <summary>Read: <c>"********"</c> or null. Write: <c>"__unchanged__"</c> keeps, plaintext rotates, null clears.</summary>
    public string? Password { get; set; }

    /// <summary>Same secret-field semantics as <see cref="Password"/>.</summary>
    public string? BearerToken { get; set; }

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class StatsSettingsDto
{
    [Range(1, 1440)]
    public int RefreshIntervalMinutes { get; set; } = 5;

    [Range(1, 365)]
    public int WindowDays { get; set; } = 7;
}
