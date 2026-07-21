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
}
