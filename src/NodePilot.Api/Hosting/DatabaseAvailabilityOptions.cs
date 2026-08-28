using System.Globalization;
using NodePilot.Api.Configuration;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Strongly typed runtime limits for the application-database availability probe and readiness
/// check.
/// Every duration and threshold is strictly positive: zero has provider-specific "infinite"
/// semantics
/// and is therefore never a safe value on the only path that can close the breaker.
/// </summary>
public sealed class DatabaseAvailabilityOptions
{
    public const string ConnectTimeoutKey = "Database:ConnectTimeoutSeconds";
    public const string ProbeConnectTimeoutKey = "Database:Probe:ConnectTimeoutSeconds";
    public const string ProbeCommandTimeoutKey = "Database:Probe:CommandTimeoutSeconds";
    public const string CleanupTimeoutKey = "Database:Probe:CleanupTimeoutSeconds";
    public const string IdleIntervalKey = "Database:Probe:IdleIntervalSeconds";
    public const string OutageIntervalKey = "Database:Probe:OutageIntervalSeconds";
    public const string SuccessesToRecoverKey = "Database:Probe:SuccessesToRecover";
    public const string FailureThresholdKey = "Database:Probe:FailureThreshold";
    public const string ReadinessTimeoutKey = "Database:ReadinessProbeTimeoutSeconds";
    public const string AuthReadTimeoutKey = "Database:AuthReadTimeoutSeconds";

    public int ConnectTimeoutSeconds { get; init; } = 5;
    public int ProbeConnectTimeoutSeconds { get; init; } = 2;
    public int ProbeCommandTimeoutSeconds { get; init; } = 2;
    public int CleanupTimeoutSeconds { get; init; } = 2;
    public int IdleIntervalSeconds { get; init; } = 5;
    public int OutageIntervalSeconds { get; init; } = 5;
    public int SuccessesToRecover { get; init; } = 2;
    public int FailureThreshold { get; init; } = 2;
    public int ReadinessTimeoutSeconds { get; init; } = 5;
    public int AuthReadTimeoutSeconds { get; init; } = 3;

    public static DatabaseAvailabilityOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new DatabaseAvailabilityOptions
        {
            ConnectTimeoutSeconds = configuration.GetValue<int?>(ConnectTimeoutKey) ?? 5,
            ProbeConnectTimeoutSeconds = configuration.GetValue<int?>(ProbeConnectTimeoutKey) ?? 2,
            ProbeCommandTimeoutSeconds = configuration.GetValue<int?>(ProbeCommandTimeoutKey) ?? 2,
            CleanupTimeoutSeconds = configuration.GetValue<int?>(CleanupTimeoutKey) ?? 2,
            IdleIntervalSeconds = configuration.GetValue<int?>(IdleIntervalKey) ?? 5,
            OutageIntervalSeconds = configuration.GetValue<int?>(OutageIntervalKey) ?? 5,
            SuccessesToRecover = configuration.GetValue<int?>(SuccessesToRecoverKey) ?? 2,
            FailureThreshold = configuration.GetValue<int?>(FailureThresholdKey) ?? 2,
            ReadinessTimeoutSeconds = configuration.GetValue<int?>(ReadinessTimeoutKey) ?? 5,
            AuthReadTimeoutSeconds = configuration.GetValue<int?>(AuthReadTimeoutKey) ?? 3,
        };
    }
}

/// <summary>Rejects non-positive availability limits before the host and its probe are
/// built.</summary>
public sealed class DatabaseAvailabilityOptionsBootValidator : IBootValidator
{
    public string Name => "DatabaseAvailabilityOptions";

    public void Validate(IConfiguration configuration, IList<BootValidationIssue> issues)
    {
        foreach (var key in PositiveKeys)
            ValidatePositive(configuration, issues, key);
    }

    private static readonly string[] PositiveKeys =
    [
        DatabaseAvailabilityOptions.ConnectTimeoutKey,
        DatabaseAvailabilityOptions.ProbeConnectTimeoutKey,
        DatabaseAvailabilityOptions.ProbeCommandTimeoutKey,
        DatabaseAvailabilityOptions.CleanupTimeoutKey,
        DatabaseAvailabilityOptions.IdleIntervalKey,
        DatabaseAvailabilityOptions.OutageIntervalKey,
        DatabaseAvailabilityOptions.SuccessesToRecoverKey,
        DatabaseAvailabilityOptions.FailureThresholdKey,
        DatabaseAvailabilityOptions.ReadinessTimeoutKey,
        DatabaseAvailabilityOptions.AuthReadTimeoutKey,
    ];

    private void ValidatePositive(
        IConfiguration configuration,
        IList<BootValidationIssue> issues,
        string key)
    {
        var raw = configuration[key];
        if (raw is null) return;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value > 0)
            return;

        issues.Add(new BootValidationIssue(
            Name,
            BootValidationSeverity.Error,
            key,
            $"{key} must be a positive whole number; zero disables the hard safety limit."));
    }
}
