namespace NodePilot.Api.Configuration.Validators;

/// <summary>
/// Validates <c>Logging:Format</c> at boot. <c>LogFormatters.Create</c> returns null for any
/// unrecognised value, and the logging setup silently falls back to plain text output. On a
/// deployment expecting ECS-JSON, a typo would break SIEM ingestion with no other signal — this
/// turns that into a loud boot error instead.
/// </summary>
public sealed class LoggingFormatBootValidator : IBootValidator
{
    /// <summary>
    /// Mirror of the keys recognised by <c>LogFormatters.Create</c>. The empty string
    /// and missing config map to the default plain-text output.
    /// </summary>
    public static readonly string[] KnownFormats =
    [
        "text",
        "cmtrace",
        "json",
        "ecs-json",
    ];

    public string Name => "LoggingFormat";

    public void Validate(IConfiguration configuration, IList<BootValidationIssue> issues)
    {
        var format = configuration["Logging:Format"];
        if (string.IsNullOrWhiteSpace(format)) return;  // null or empty is the plain text default, fine.

        var normalized = format.Trim().ToLowerInvariant();
        if (Array.Exists(KnownFormats, k => k == normalized)) return;

        issues.Add(new BootValidationIssue(
            Name, BootValidationSeverity.Error, "Logging:Format",
            $"'{format}' is not a known format. Allowed: {string.Join(", ", KnownFormats)} (or empty for plain text). " +
            "Unrecognised values would silently fall back to plain text, which breaks structured-log ingestion in SIEMs."));
    }
}
