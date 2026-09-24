using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Windows PowerShell 5.1 started by the engine must behave like a Windows PowerShell started on
/// the machine: its own core modules, and text that survives the trip in both directions.
/// </summary>
public class WindowsPowerShellChildProcessTests
{
    // The SDK's $PSHOME is where System.Management.Automation.dll was loaded from.
    private static readonly string SdkModulePath = Path.Combine(
        Path.GetDirectoryName(typeof(System.Management.Automation.PowerShell).Assembly.Location)!, "Modules");

    private static async Task<Dictionary<string, string>> RunInWindowsPowerShellAsync(string script, bool isolated)
    {
        // Building the factory opens the in-process pool, which rewrites this process's PSModulePath.
        var factory = new PowerShellEngineFactory(NullLoggerFactory.Instance);
        Environment.GetEnvironmentVariable("PSModulePath").Should().ContainEquivalentOf(SdkModulePath,
            "precondition: the open pool has put the SDK modules into the inherited module path");

        var result = await factory.GetEngine("powershell", isolated).ExecuteAsync(
            new PowerShellExecutionRequest { ScriptText = script, Engine = "powershell", Isolated = isolated },
            TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue(result.Error);
        var (_, _, parameters) = PowerShellActivitySupport.ExtractMarkers(result.Output, "step-1", NullLogger.Instance);
        return parameters;
    }

    [WindowsFact]
    public Task WindowsPowerShell_AfterThePoolOpened_LoadsItsOwnCoreModules() => AssertOwnCoreModules(isolated: false);

    [WindowsFact]
    public Task WindowsPowerShell_Isolated_AfterThePoolOpened_LoadsItsOwnCoreModules() => AssertOwnCoreModules(isolated: true);

    private static async Task AssertOwnCoreModules(bool isolated)
    {
        // New-Guid and Get-FileHash are script functions of the 5.1 Utility module; they vanish
        // when 5.1 loads the SDK's PowerShell 7 manifest of the same module instead.
        var parameters = await RunInWindowsPowerShellAsync(
            "$guid = (New-Guid).Guid; $hash = (Get-FileHash -InputStream ([IO.MemoryStream]::new())).Hash; $modulePath = $env:PSModulePath",
            isolated);

        parameters["guid"].Should().MatchRegex("^[0-9a-f-]{36}$");
        parameters["hash"].Should().NotBeNullOrEmpty();
        parameters["modulePath"].Should().NotContainEquivalentOf(SdkModulePath);
    }

    private readonly RunScriptActivity _activity = new(
        new PowerShellEngineFactory(NullLoggerFactory.Instance),
        NullLogger<RunScriptActivity>.Instance);

    [WindowsFact]
    public Task RunScript_WindowsPowerShell_UnicodeRoundTrip() => AssertUnicodeRoundTrip(isolated: false);

    [WindowsFact]
    public Task RunScript_WindowsPowerShell_Isolated_UnicodeRoundTrip() => AssertUnicodeRoundTrip(isolated: true);

    private async Task AssertUnicodeRoundTrip(bool isolated)
    {
        const string value = "Grüße ✓ C:\\Täst";
        var context = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "step-1",
            // An unqualified key is injected as $greeting.
            Variables = new Dictionary<string, string> { ["greeting"] = value },
        };
        const string script = """
            $echo = $greeting
            $literal = 'Größe'
            Write-Output $greeting
            Write-Error $greeting -ErrorAction Continue
            """;
        var config = JsonDocument.Parse(JsonSerializer.Serialize(new { script, engine = "powershell", isolated })).RootElement;

        var result = await _activity.ExecuteAsync(context, config, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue(result.ErrorOutput);
        result.Output.Should().Contain(value);
        result.OutputParameters.Should().ContainKey("echo").WhoseValue.Should().Be(value);
        result.OutputParameters.Should().ContainKey("literal").WhoseValue.Should().Be("Größe");
        result.ErrorOutput.Should().Contain(value);
    }
}
