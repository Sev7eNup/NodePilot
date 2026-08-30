using System.Text.RegularExpressions;

namespace NodePilot.Engine.PowerShell;

/// <summary>
/// Validates PowerShell parameter keys against an allow-list regex to prevent
/// script-injection via attacker-controlled variable names.
/// </summary>
internal static class ParameterKeyValidator
{
    private static readonly Regex ValidKey =
        new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Returns true when the key contains only [A-Za-z0-9_] and is non-empty.</summary>
    public static bool IsValid(string? key)
        => !string.IsNullOrEmpty(key) && ValidKey.IsMatch(key);

    /// <summary>
    /// Same guarantee for a fully-qualified data-bus key (<c>step-1.param.hostName</c>), which
    /// carries the dots and hyphens a node id may contain. Used for the <c>$Params</c> entry that
    /// keeps an ambiguous published value reachable under its owner's name.
    /// </summary>
    private static readonly Regex ValidQualifiedKey =
        new(@"^[A-Za-z0-9_.-]+$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static bool IsValidQualified(string? key)
        => !string.IsNullOrEmpty(key) && ValidQualifiedKey.IsMatch(key);
}
