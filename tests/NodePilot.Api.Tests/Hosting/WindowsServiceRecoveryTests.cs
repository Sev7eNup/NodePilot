using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Microsoft.Win32;
using NodePilot.Api.Hosting;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class WindowsServiceRecoveryTests
{
    private const int None = 0, Restart = 1, RunCommand = 2, Reboot = 3;

    /// <summary>SERVICE_FAILURE_ACTIONS as the SCM stores it: five DWORDs, then SC_ACTION[].</summary>
    private static byte[] Blob(params (int Type, int DelayMs)[] actions)
    {
        var data = new byte[20 + actions.Length * 8];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), 86400);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), actions.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 20);
        for (var i = 0; i < actions.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20 + i * 8), actions[i].Type);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24 + i * 8), actions[i].DelayMs);
        }
        return data;
    }

    [Fact]
    public void AllActionsRestart_InstallerConfiguration_ReturnsTrue()
    {
        // sc.exe failure NodePilot reset= 86400 actions= restart/5000/restart/5000/restart/60000
        var blob = Blob((Restart, 5000), (Restart, 5000), (Restart, 60000));

        WindowsServiceRecovery.AllActionsRestart(blob).Should().BeTrue();
    }

    [Theory]
    [InlineData(None)]
    [InlineData(RunCommand)]
    [InlineData(Reboot)]
    public void AllActionsRestart_AnyNonRestartAction_ReturnsFalse(int other)
    {
        var blob = Blob((Restart, 5000), (other, 5000), (Restart, 60000));

        WindowsServiceRecovery.AllActionsRestart(blob).Should().BeFalse();
    }

    [Fact]
    public void AllActionsRestart_NoActions_ReturnsFalse()
    {
        WindowsServiceRecovery.AllActionsRestart(Blob()).Should().BeFalse();
    }

    [Fact]
    public void AllActionsRestart_TruncatedBlob_ReturnsFalse()
    {
        var blob = Blob((Restart, 5000), (Restart, 5000));

        WindowsServiceRecovery.AllActionsRestart(blob.AsSpan(0, blob.Length - 4)).Should().BeFalse();
        WindowsServiceRecovery.AllActionsRestart(new byte[8]).Should().BeFalse();
    }

    [Fact]
    public void AllActionsRestart_ActionOffsetBeyondBlob_ReturnsFalse()
    {
        var blob = Blob((Restart, 5000));
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(16), int.MaxValue);

        WindowsServiceRecovery.AllActionsRestart(blob).Should().BeFalse();
    }

    private const string ProcessPath = @"C:\Program Files\NodePilot\NodePilot.Api.exe";

    [Fact]
    public void FindServiceKey_UnreadableForeignKey_IsSkipped()
    {
        using var services = new TempServicesKey();
        services.AddService("Other", @"C:\Other\other.exe", readable: false);
        services.AddService("NodePilot", $"\"{ProcessPath}\"");

        WindowsServiceRecovery.FindServiceKey(services.Root, ProcessPath).Should().Be("NodePilot");
    }

    [Fact]
    public void FindServiceKey_OwnKeyUnreadable_ReturnsNull()
    {
        using var services = new TempServicesKey();
        services.AddService("NodePilot", $"\"{ProcessPath}\"", readable: false);

        WindowsServiceRecovery.FindServiceKey(services.Root, ProcessPath).Should().BeNull();
    }

    [Fact]
    public void ReadCapability_UnreadableKey_ReportsServiceNotResolved()
    {
        using var services = new TempServicesKey();
        services.AddService("NodePilot", $"\"{ProcessPath}\"", Blob((Restart, 5000)), readable: false);

        WindowsServiceRecovery.ReadCapability(services.Root, "NodePilot")
            .Should().Be(ServiceRestartCapability.No("SERVICE_NOT_RESOLVED"));
    }

    [Fact]
    public void ReadCapability_RestartActions_IsAvailable()
    {
        using var services = new TempServicesKey();
        services.AddService("NodePilot", $"\"{ProcessPath}\"", Blob((Restart, 5000), (Restart, 60000)));

        WindowsServiceRecovery.ReadCapability(services.Root, "NodePilot")
            .Should().Be(ServiceRestartCapability.Yes);
    }

    /// <summary>A throwaway stand-in for <c>HKLM\…\Services</c> under HKCU.</summary>
    private sealed class TempServicesKey : IDisposable
    {
        private readonly string _path = $@"Software\NodePilotTests-Services-{Guid.NewGuid():N}";
        private readonly List<string> _locked = [];

        public TempServicesKey() => Root = Registry.CurrentUser.CreateSubKey(_path);

        public RegistryKey Root { get; }

        public void AddService(string name, string imagePath, byte[]? failureActions = null, bool readable = true)
        {
            using var key = Root.CreateSubKey(name);
            key.SetValue("ImagePath", imagePath, RegistryValueKind.ExpandString);
            if (failureActions is not null) key.SetValue("FailureActions", failureActions, RegistryValueKind.Binary);
            if (readable) return;

            // Denying QueryValues makes a read-access OpenSubKey throw, as on an admin-only service key.
            var security = key.GetAccessControl();
            security.AddAccessRule(DenyQuery);
            key.SetAccessControl(security);
            _locked.Add(name);
        }

        private static RegistryAccessRule DenyQuery => new(
            WindowsIdentity.GetCurrent().User!, RegistryRights.QueryValues, AccessControlType.Deny);

        public void Dispose()
        {
            // The owner keeps READ_CONTROL and WRITE_DAC, so the deny can be lifted before deleting.
            foreach (var name in _locked)
            {
                using var key = Root.OpenSubKey(
                    name, RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.ReadPermissions | RegistryRights.ChangePermissions)!;
                var security = key.GetAccessControl();
                security.RemoveAccessRule(DenyQuery);
                key.SetAccessControl(security);
            }
            Root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_path);
        }
    }
}
