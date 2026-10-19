using System.Buffers.Binary;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Reads the SCM recovery configuration of the service this process runs as. The installers
/// (server and desktop) set restart actions; a service installed by hand may not have them,
/// and a self-restart there would leave the service stopped.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsServiceRecovery
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const int ScActionRestart = 1;
    private static readonly Lazy<string?> OwnServiceKey = new(FindOwnServiceKey);

    public static ServiceRestartCapability Probe()
    {
        if (!WindowsServiceHelpers.IsWindowsService())
            return ServiceRestartCapability.No("NOT_A_WINDOWS_SERVICE");

        var name = OwnServiceKey.Value;
        if (name is null) return ServiceRestartCapability.No("SERVICE_NOT_RESOLVED");

        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{name}");
        return key?.GetValue("FailureActions") is byte[] actions && AllActionsRestart(actions)
            ? ServiceRestartCapability.Yes
            : ServiceRestartCapability.No("RECOVERY_NOT_CONFIGURED");
    }

    /// <summary>
    /// True when the stored <c>SERVICE_FAILURE_ACTIONS</c> blob (resetPeriod, rebootMsg, command,
    /// actionCount, actionsOffset, then <c>SC_ACTION[]</c> of type+delay) lists only restart
    /// actions. Every action must restart: the SCM picks them by failure count.
    /// </summary>
    public static bool AllActionsRestart(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20) return false;
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
        if (count < 1 || offset < 20 || (long)offset + (long)count * 8 > data.Length) return false;

        for (var i = 0; i < count; i++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(data[(offset + i * 8)..]) != ScActionRestart)
                return false;
        }
        return true;
    }

    /// <summary>The service key whose <c>ImagePath</c> is this process; null unless exactly one matches.</summary>
    private static string? FindOwnServiceKey()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return null;

        using var services = Registry.LocalMachine.OpenSubKey(ServicesKey);
        if (services is null) return null;

        string? match = null;
        foreach (var name in services.GetSubKeyNames())
        {
            using var key = services.OpenSubKey(name);
            if (key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string imagePath)
                continue;
            if (!Environment.ExpandEnvironmentVariables(imagePath).Contains(processPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match is not null) return null;
            match = name;
        }
        return match;
    }
}
