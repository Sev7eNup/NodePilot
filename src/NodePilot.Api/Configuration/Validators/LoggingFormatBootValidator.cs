namespace NodePilot.Api.Configuration.Validators;

/// <summary>
/// Catches the silent-fallback trap in <c>Logging:Format</c>. The formatter factory
/// in <c>LogFormatters.Create</c> returns null for any unrecognised value, which the
/// logging setup then treats as "fall back to plain text output template". On a
/// production deployment expecting ECS-JSON, a typo like <c>"ecs-jsom"</c> would
/// quietly downgrade the rolling file to plain text and break SIEM ingestion without
/// any other signal. Validating the value at boot turns that into a loud error.
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
        if (string.IsNullOrWhiteSpace(format)) return;  // null/empty → plain text default, fine.

        var normalized = format.Trim().ToLowerInvariant();
        if (Array.Exists(KnownFormats, k => k == normalized)) return;

        issues.Add(new BootValidationIssue(
            Name, BootValidationSeverity.Error, "Logging:Format",
            $"'{format}' is not a known format. Allowed: {string.Join(", ", KnownFormats)} (or empty for plain text). " +
            "Unrecognised values would silently fall back to plain text, which breaks structured-log ingestion in SIEMs."));
    }
}
