using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Execution;
using NodePilot.Engine.PowerShell;
using NodePilot.Remote;
using System.Management.Automation.Runspaces;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

public class ScriptOutputLimitTests
{
    [Theory]
    [InlineData("powershell", false, "[Console]::Out.Write(('x' * 20000))")]
    [InlineData("powershell", true, "[Console]::Out.Write(('x' * 20000))")]
    [InlineData("powershell", false, "[Console]::Error.Write(('x' * 20000))")]
    [InlineData("powershell", true, "[Console]::Error.Write(('x' * 20000))")]
    [InlineData("runspace", false, "1..10000 | ForEach-Object { 'line' }")]
    [InlineData("runspace", false, "Write-Output ('x' * 20000)")]
    [InlineData("runspace", false, "Write-Error ('x' * 20000) -ErrorAction Continue")]
    [InlineData("runspace", false, "Write-Warning ('x' * 20000)")]
    [InlineData("runspace", false, "$VerbosePreference='Continue'; Write-Verbose ('x' * 20000)")]
    [InlineData("runspace", false, "Write-Information ('x' * 20000)")]
    [InlineData("runspace", false, "while ($true) { 'more-output' }")]
    [InlineData("powershell", false, "while ($true) { 'more-output' }")]
    [InlineData("powershell", true, "while ($true) { 'more-output' }")]
    public async Task ExecuteAsync_OutputExceedsLimit_FailsWithBoundedOutput(string engineName, bool isolated, string script)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Engine:MaxCapturedOutputChars"] = "4096"
        }).Build();
        var factory = new Engine.PowerShell.PowerShellEngineFactory(NullLoggerFactory.Instance, config);
        var engine = factory.GetEngine(engineName, isolated);
        try
        {
            var result = await engine.ExecuteAsync(new()
            {
                ScriptText = script,
                Isolated = isolated,
                Timeout = TimeSpan.FromSeconds(15)
            }, TestContext.Current.CancellationToken);

            result.Success.Should().BeFalse();
            result.TimedOut.Should().BeFalse();
            result.Error.Should().Contain("output limit");
            result.Output.Length.Should().BeLessThanOrEqualTo(4096);
        }
        finally { (factory.GetEngine("runspace") as IDisposable)?.Dispose(); }
    }

    [Theory]
    [InlineData("Write-Output ('x' * 20000)")]
    [InlineData("1..10000 | ForEach-Object { 'line' }")]
    [InlineData("Write-Error ('x' * 20000) -ErrorAction Continue")]
    [InlineData("Write-Warning ('x' * 20000)")]
    [InlineData("while ($true) { 'more-output' }")]
    public async Task ExecuteScriptAsync_OutputExceedsLimit_FailsAndPoisonsSession(string script)
    {
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        await using var session = new WinRmSession(runspace, "test-local", maxCapturedOutputChars: 4096);

        var result = await session.ExecuteScriptAsync(script, 15, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("output limit");
        result.Output.Length.Should().BeLessThanOrEqualTo(4096);
        session.IsAlive.Should().BeFalse();
    }

    [Theory]
    [InlineData("powershell", false)]
    [InlineData("powershell", true)]
    [InlineData("runspace", false)]
    public async Task ExecuteAsync_WithinLimit_PreservesStructuredOutput(string name, bool isolated)
    {
        var factory = new PowerShellEngineFactory(NullLoggerFactory.Instance);
        try
        {
            var result = await factory.GetEngine(name, isolated).ExecuteAsync(new()
            {
                ScriptText = "$answer = 'forty-two'; Write-Output 'ok'",
                Isolated = isolated,
                Timeout = TimeSpan.FromSeconds(15)
            }, TestContext.Current.CancellationToken);
            result.Success.Should().BeTrue(result.Error);
            var (clean, _, parameters) = PowerShellActivitySupport.ExtractMarkers(result.Output, "step", NullLogger.Instance);
            clean.Should().Contain("ok");
            parameters.Should().ContainKey("answer").WhoseValue.Should().Be("forty-two");
        }
        finally { (factory.GetEngine("runspace") as IDisposable)?.Dispose(); }
    }

    [Fact]
    public async Task Capture_ConcurrentStreams_ShareOneBudgetAndContinueDraining()
    {
        using var budget = new ScriptOutputBudget(4096);
        var stdout = new ProcessExecutionEngine.OutputCapture(new StringReader(new string('o', 10000)),
            TestContext.Current.CancellationToken, budget.CreateBuffer());
        var stderr = new ProcessExecutionEngine.OutputCapture(new StringReader(new string('e', 10000)),
            TestContext.Current.CancellationToken, budget.CreateBuffer());
        await Task.WhenAll(stdout.Completion, stderr.Completion);
        budget.Exceeded.Should().BeTrue();
        (stdout.Text.Length + stderr.Text.Length).Should().Be(4096);
    }
}
