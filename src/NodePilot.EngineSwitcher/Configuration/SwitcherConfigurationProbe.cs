namespace NodePilot.EngineSwitcher.Configuration;

internal interface ISwitcherConfigurationProbe
{
    /// <summary>Returns why the configuration is unusable, or null when it loads.</summary>
    string? Probe();
}

/// <summary>
/// Loads the switcher configuration for the system check so an unusable file is reported before a
/// switch is started. Target-specific validation stays in the switch preflight.
/// </summary>
internal sealed class SwitcherConfigurationProbe : ISwitcherConfigurationProbe
{
    private readonly SwitcherConfigurationLoader _loader;

    public SwitcherConfigurationProbe(SwitcherConfigurationLoader loader) => _loader = loader;

    public string? Probe()
    {
        try
        {
            _loader.Load();
            return null;
        }
        catch (Exception exception)
        {
            // Any failure to read the file is a configuration fault the operator has to see.
            return exception.Message;
        }
    }
}
