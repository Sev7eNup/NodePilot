namespace NodePilot.Core.Configuration;

/// <summary>
/// One parser for hardening flags that default to on, shared by the settings UI, the boot
/// warnings and the activities that enforce them. Only an explicit "false" turns such a flag
/// off; a missing, blank or unparseable value keeps the safe setting.
/// </summary>
public static class HardeningFlags
{
    public const string DisallowShellExecuteKey = "StartProgram:DisallowShellExecute";

    public static bool IsOn(string? raw) => !bool.TryParse(raw, out var parsed) || parsed;
}
