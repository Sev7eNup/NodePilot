namespace NodePilot.Switcher.Services;

internal sealed record SwitcherOptions(
    TimeSpan GracefulStopTimeout,
    TimeSpan ForcedStopTimeout,
    TimeSpan StartTimeout)
{
    public static SwitcherOptions Default { get; } = new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30));
}
