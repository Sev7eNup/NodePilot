using System.Diagnostics;

namespace NodePilot.Switcher.Services;

internal interface IProcessPresenceProbe
{
    bool IsRunning(string processName);
}

internal sealed class WindowsProcessPresenceProbe : IProcessPresenceProbe
{
    public bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }
}
