namespace NodePilot.Engine.Security;

/// <summary>
/// Single source of truth for PowerShell-safe quoting. Activity builders that emit PowerShell
/// from user/variable-supplied strings must route every interpolated value through
/// <see cref="Literal"/> before embedding it in the script.
///
/// Why the helper exists: before it was introduced, five remote activities (service, registry,
/// WMI, file-op, …) inlined config values directly into single-quoted PowerShell literals with
/// no escaping of inner apostrophes. When those values arrived via <c>{{step.param.X}}</c>
/// — i.e. potentially from an upstream step whose output an attacker controlled — a single
/// apostrophe broke out of the literal and the rest of the value ran as PowerShell on the next
/// target. The audit flagged this as H2.
///
/// Related: <c>WorkflowEngine.ResolveVariables</c> used to JSON-escape values before the
/// activity builders received them, producing a double-escape mismatch that could recreate
/// the same break-out via <c>\'</c>. Activities that call <see cref="Literal"/> on already
/// JSON-escaped input still produce safe output — the only thing that matters is that the
/// *final* value lands inside a proper single-quoted literal with doubled apostrophes.
/// </summary>
public static class PowerShellQuoter
{
    /// <summary>
    /// Returns a PowerShell single-quoted literal with embedded apostrophes doubled. Null input
    /// becomes the empty literal <c>''</c>. Use this instead of string concatenation every time
    /// a config- or variable-sourced string is embedded in a PowerShell script.
    /// </summary>
    public static string Literal(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "''";
        return "'" + value.Replace("'", "''") + "'";
    }
}
