using System.Buffers.Binary;
using FluentAssertions;
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
}
