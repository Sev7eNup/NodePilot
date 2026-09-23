using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NodePilot.Engine.Agents;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public sealed class AgentDiagnosticReadTests
{
    [Theory]
    [InlineData("Get-Website | Select-Object Name,State")]
    [InlineData("Get-WebBinding -Name 'WSUS Administration' -Protocol http | Select-Object bindingInformation")]
    [InlineData("Get-WebAppPoolState -Name 'WsusPool'")]
    [InlineData("Get-WebApplication -Site 'observed-site' | Select-Object Path,ApplicationPool")]
    [InlineData("netsh winhttp show proxy")]
    public void AllowsLocalDiagnosticReads(string command)
        => Assert.NotEmpty(AgentPermissionPolicy.PrepareShell("powershell", command));

    [Theory]
    [InlineData("Stop-WebAppPool -Name WsusPool")]
    [InlineData("Set-WebApplication -Site 'observed-site' -ApplicationPool other")]
    [InlineData("Get-WebApplication -PSPath 'IIS:\\Sites'")]
    [InlineData("Set-WebBinding -Name site -PropertyName bindingInformation -Value ':80:'")]
    [InlineData("Get-Website -PSPath 'IIS:\\Sites'")]
    [InlineData("netsh winhttp reset proxy")]
    [InlineData("netsh -r:other winhttp show proxy")]
    [InlineData("netsh winhttp show proxy; Set-Service BITS -StartupType Disabled")]
    [InlineData("netsh.exe winhttp import proxy source=ie")]
    public void CannotExpandReadIntoWritesOrAnotherTarget(string command)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("powershell", command));

    [Fact]
    public void CmdProxyReadUsesFixedSystemExecutable()
        => Assert.Contains(@"%SystemRoot%\System32\netsh.exe", AgentPermissionPolicy.PrepareShell("cmd", "netsh winhttp show proxy"));

    [Theory]
    [InlineData("netsh winhttp reset proxy")]
    [InlineData("netsh -r:other winhttp show proxy")]
    [InlineData("netsh winhttp show proxy & netsh winhttp reset proxy")]
    [InlineData("netsh winhttp show proxy > C:\\proxy.txt")]
    public void CmdProxyReadCannotBecomeWriteOrRemoteCommand(string command)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionPolicy.PrepareShell("cmd", command));

    [Fact]
    public async Task ServiceStatesRemainNamedThroughProjectionAndJson()
    {
        var prepared = AgentPermissionPolicy.PrepareShell("powershell",
            "Get-Service -Name Winmgmt | Select-Object Name,Status,StartType | ConvertTo-Json -Compress");
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(prepared)),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, await error);
        using var json = JsonDocument.Parse(await output);
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("Status").ValueKind);
        Assert.Contains(json.RootElement.GetProperty("Status").GetString(), new[] { "Running", "Stopped", "StartPending", "StopPending", "Paused", "PausePending", "ContinuePending" });
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("StartType").ValueKind);
    }
}
