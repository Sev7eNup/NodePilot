using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NodePilot.Engine.Agents;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public sealed class AgentShellTests
{
    [Fact]
    public async Task PowerShellChild_FindsWindowsModulesWithAnEmbeddedHostsModulePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-hash-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, "abc", new UTF8Encoding(false), TestContext.Current.CancellationToken);
            var literal = NodePilot.Engine.PowerShell.PowerShellOperation.Literal(path);
            var script = "$env:PSModulePath=" + NodePilot.Engine.PowerShell.PowerShellOperation.Literal(AppContext.BaseDirectory)
                + "; " + AgentProcessScript.Build("powershell", "$ErrorActionPreference='Stop'; (Get-FileHash -LiteralPath " + literal + " -Algorithm SHA256).Hash", null, null);
            using var result = JsonDocument.Parse(await Execute(script));
            Assert.Equal(0, result.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", result.RootElement.GetProperty("stdout").GetString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("powershell", "Write-Output 'agent & quoted'; exit 7")]
    [InlineData("cmd", "@echo agent ^& quoted\r\n@exit /b 7")]
    public async Task ShellWrapper_PreservesCommandTextAndReportsExitCode(string shell, string command)
    {
        var output = await Execute(AgentProcessScript.Build(shell, command, null, null));
        using var result = JsonDocument.Parse(output);
        Assert.Equal(7, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("agent & quoted", result.RootElement.GetProperty("stdout").GetString());
        Assert.False(result.RootElement.GetProperty("timedOut").GetBoolean());
    }

    [Theory]
    [InlineData("powershell", "Write-Output 'Grüße aus Windows'")]
    [InlineData("cmd", "@echo Grüße aus Windows")]
    public async Task ShellWrapper_PreservesUnicode(string shell, string command)
    {
        using var result = JsonDocument.Parse(await Execute(AgentProcessScript.Build(shell, command, null, null)));
        Assert.Contains("Grüße aus Windows", result.RootElement.GetProperty("stdout").GetString());
    }

    [Theory]
    [InlineData("first")]
    [InlineData("last")]
    public async Task TargetSearch_HandlesLongUtf16LinesAndReturnsBoundedEvidence(string order)
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-search-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            await File.WriteAllTextAsync(path, "header\n" + new string('x', 8190) + "NEEDLE" + new string('x', 20000) + "\nend", Encoding.Unicode, TestContext.Current.CancellationToken);
            var output = await Execute(AgentFileTools.BuildSearchScript(path, "NEEDLE", order));
            Assert.Contains(path + ":2:", output);
            Assert.Contains("NEEDLE", output);
            Assert.Contains("truncated=False", output);
            Assert.InRange(output.Length, 1, 1500);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TargetSearch_DirectoryIsNotMisreportedAsPermissionFailure()
    {
        var script = AgentFileTools.BuildSearchScript(Path.GetTempPath(), "needle");
        var error = await Execute("try { " + script + " } catch { $_.Exception.Message }");
        Assert.Contains("requires a file path, not a directory", error);
        Assert.Contains("files_list", error);
    }

    [Fact]
    public async Task TargetSearch_ReportsOmittedMatchesAndCanReadLatestFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-search-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var lines = Enumerable.Range(1, 25).Select(i => $"package: old success {i}")
                .Append("package: CURRENT_FAILURE");
            await File.WriteAllTextAsync(path, string.Join("\n", lines), Encoding.UTF8, TestContext.Current.CancellationToken);
            var first = await Execute(AgentFileTools.BuildSearchScript(path, "package"));
            Assert.Contains("truncated=True", first);
            Assert.DoesNotContain("CURRENT_FAILURE", first);
            var last = await Execute(AgentFileTools.BuildSearchScript(path, "package", "last"));
            Assert.Contains("truncated=True", last);
            Assert.Contains(path + ":26: package: CURRENT_FAILURE", last);
            Assert.DoesNotContain("old success 1\r", last);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(250)]
    public async Task TargetSearch_LastMatchesScansLargeLinesWithinToolDeadline(int megabytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-search-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            await using (var stream = File.Create(path))
            {
                var block = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024));
                for (var i = 0; i < megabytes; i++)
                    await stream.WriteAsync(block, TestContext.Current.CancellationToken);
                await stream.WriteAsync("\nCURRENT_FAILURE"u8.ToArray(), TestContext.Current.CancellationToken);
            }
            var output = await Execute(AgentFileTools.BuildSearchScript(path, "CURRENT_FAILURE", "last"));
            Assert.Contains(path + ":2: CURRENT_FAILURE", output);
            Assert.InRange(output.Length, 1, 1500);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ShellWrapper_DrainsButBoundsLargeOutput()
    {
        using var result = JsonDocument.Parse(await Execute(AgentProcessScript.Build("powershell", "[Console]::Write(('x' * 100000)); [Console]::Error.Write(('y' * 100000))", null, null)));
        Assert.True(result.RootElement.GetProperty("truncated").GetBoolean());
        Assert.InRange(result.RootElement.GetProperty("stdout").GetString()!.Length, 1, 16000);
        Assert.InRange(result.RootElement.GetProperty("stderr").GetString()!.Length, 1, 16000);
    }

    [Fact]
    public async Task GitBash_WhenInstalled_ExecutesAndReportsItsExitCode()
    {
        var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash)) Assert.Skip("Git Bash is not installed on this test host.");
        using var result = JsonDocument.Parse(await Execute(AgentProcessScript.Build("bash", "printf 'agent bash'; exit 7", null, bash)));
        Assert.Equal(7, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("agent bash", result.RootElement.GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task ShellWrapper_EnforcesProcessDeadline()
    {
        using var result = JsonDocument.Parse(await Execute(AgentProcessScript.Build("powershell", "Start-Sleep -Seconds 60", null, null, timeoutSeconds: 1)));
        Assert.True(result.RootElement.GetProperty("timedOut").GetBoolean());
    }

    internal static async Task<string> Execute(string script)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + script)));
        using var process = Process.Start(info)!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
