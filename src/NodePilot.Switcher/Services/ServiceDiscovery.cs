using System.Text.Json;
using System.IO;
using Microsoft.Win32;
using NodePilot.Switcher.Models;

namespace NodePilot.Switcher.Services;

internal sealed class ServiceDiscovery
{
    internal static readonly string[] SystemCenterServiceNames =
        ["omanagement", "oremoting", "omonitor", "orunbook"];

    private readonly IServiceControlGateway _gateway;
    private readonly Func<IEnumerable<string>> _nodePilotCandidates;

    public ServiceDiscovery(
        IServiceControlGateway gateway,
        Func<IEnumerable<string>>? nodePilotCandidates = null)
    {
        _gateway = gateway;
        _nodePilotCandidates = nodePilotCandidates ?? ReadNodePilotCandidates;
    }

    public ManagedEnvironmentSnapshot Discover()
    {
        ServiceSnapshot? nodePilot = null;
        foreach (var candidate in _nodePilotCandidates()
                     .Where(IsSafeServiceName)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var service = _gateway.TryGetService(candidate);
            if (service is not null && IsNodePilotBinary(service.BinaryPath))
            {
                nodePilot = service;
                break;
            }
        }

        var systemCenter = SystemCenterServiceNames
            .Select(_gateway.TryGetService)
            .Where(service => service is not null)
            .Cast<ServiceSnapshot>()
            .ToArray();

        return new ManagedEnvironmentSnapshot(nodePilot, systemCenter);
    }

    internal static bool IsNodePilotBinary(string binaryPath)
    {
        var value = binaryPath.TrimStart();
        if (value.Length == 0) return false;
        string executable;
        if (value[0] == '"')
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote < 0) return false;
            executable = value[1..closingQuote];
        }
        else
        {
            var whitespace = value.IndexOfAny([' ', '\t']);
            executable = whitespace < 0 ? value : value[..whitespace];
        }
        return Path.GetFileName(executable).Equals("NodePilot.Api.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeServiceName(string value) =>
        value.Length is > 0 and <= 256
        && value.All(character => !char.IsControl(character) && character is not '/' and not '\\');

    private static IEnumerable<string> ReadNodePilotCandidates()
    {
        string? markerName = null;
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var marker = localMachine.OpenSubKey(@"SOFTWARE\NodePilot\Server", writable: false);
            markerName = marker?.GetValue("ServiceName") as string;
        }
        catch
        {
            // Discovery falls back to the desktop handoff and default service name.
        }

        if (!string.IsNullOrWhiteSpace(markerName)) yield return markerName;

        var desktopName = ReadDesktopServiceName();
        if (!string.IsNullOrWhiteSpace(desktopName)) yield return desktopName;
        yield return "NodePilot";
    }

    private static string? ReadDesktopServiceName()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NodePilot",
                "desktop.json");
            if (!File.Exists(path)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty("serviceName", out var value) ? value.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
