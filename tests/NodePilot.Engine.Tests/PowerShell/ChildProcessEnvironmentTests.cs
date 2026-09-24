using System.Diagnostics;
using System.Text;
using FluentAssertions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// The module path rule exists twice: in C# for the engine's own child processes and as a
/// PowerShell function for generated scripts that start a process themselves, locally in the
/// PowerShell 7 pool and remotely in Windows PowerShell 5.1. All three must agree.
/// </summary>
public class ChildProcessEnvironmentTests
{
    private const string NullMarker = "<null>";

    public static TheoryData<string?, string?, string?> Cases => new()
    {
        { @"C:\User", @"C:\Machine", @"C:\User;C:\Machine" },
        { @"C:\User", null, @"C:\User" },
        { null, @"C:\Machine", @"C:\Machine" },
        { null, null, null },
        { "", "", null },
        { "   ", @"C:\Machine", @"C:\Machine" },
        { "  ", " \t ", null },
    };

    private static string Literal(string? value) => value is null ? "$null" : "'" + value.Replace("'", "''") + "'";

    private static string Script(string? user, string? machine) =>
        ChildProcessEnvironment.PowerShellFunction + "\n"
        + $"$r = __npChildModulePath {Literal(user)} {Literal(machine)}\n"
        + $"if ($null -eq $r) {{ '{NullMarker}' }} else {{ $r }}";

    [Theory]
    [MemberData(nameof(Cases))]
    public void Combine_DropsBlankPartsAndJoinsUserThenMachine(string? user, string? machine, string? expected)
        => ChildProcessEnvironment.Combine(user, machine).Should().Be(expected);

    [Theory]
    [MemberData(nameof(Cases))]
    public void PowerShellFunction_InThePowerShell7Sdk_MatchesCombine(string? user, string? machine, string? expected)
    {
        using var shell = System.Management.Automation.PowerShell.Create();
        var output = shell.AddScript(Script(user, machine)).Invoke();

        shell.HadErrors.Should().BeFalse(string.Join("; ", shell.Streams.Error));
        output.Should().ContainSingle().Which.ToString().Should().Be(expected ?? NullMarker);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PowerShellFunction_InWindowsPowerShell51_MatchesCombine(string? user, string? machine, string? expected)
    {
        var psi = new ProcessStartInfo(
            Path.Join(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(user, machine))))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        stderr.Should().BeEmpty();
        stdout.Trim().Should().Be(expected ?? NullMarker);
    }
}
