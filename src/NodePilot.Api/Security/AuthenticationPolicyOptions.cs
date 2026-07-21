namespace NodePilot.Api.Security;

/// <summary>Enterprise-wide authentication and session policy.</summary>
public sealed class AuthenticationPolicyOptions
{
    public const string SectionName = "Authentication";

    /// <summary>
    /// Local passwords are reserved for explicitly marked break-glass accounts by default.
    /// </summary>
    public LocalLoginMode LocalLoginMode { get; set; } = LocalLoginMode.BreakGlassOnly;

    /// <summary>Absolute lifetime of a server-side session and its JWT.</summary>
    public int SessionAbsoluteLifetimeHours { get; set; } = 8;

    /// <summary>Maximum age of an external authorization snapshot.</summary>
    public int MaxAuthorizationStalenessMinutes { get; set; } = 15;
}

public enum LocalLoginMode
{
    Disabled = 0,
    BreakGlassOnly = 1,
    Enabled = 2,
}
