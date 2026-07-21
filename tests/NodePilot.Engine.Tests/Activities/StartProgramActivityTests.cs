using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class StartProgramActivityTests
{
    /// <summary>
    /// Test-only subclass: exposes <c>BuildScript</c> and <c>PostProcess</c> (protected on
    /// <see cref="BaseRemoteActivity"/>) so we can unit-test them without spinning up WinRM /
    /// a real PowerShell engine. Constructor deps are passed null — we never call ExecuteAsync.
    /// </summary>
    private class Accessor : StartProgramActivity
    {
        public Accessor(IConfiguration? config = null)
            : base(null!, null!, null!, null!, config ?? new ConfigurationBuilder().Build()) { }
        public string CallBuildScript(JsonElement config, StepExecutionContext ctx) => BuildScript(config, ctx);
        public ActivityResult CallPostProcess(ActivityResult raw, JsonElement config) => PostProcess(raw, config);
    }

    private static IConfiguration DisallowShellExecuteConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StartProgram:DisallowShellExecute"] = "true" })
            .Build();

    /// <summary>Dev-mode override: explicit DisallowShellExecute=false re-enables the shell
    /// path the way appsettings.Development.json does.</summary>
    private static IConfiguration AllowShellExecuteConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StartProgram:DisallowShellExecute"] = "false" })
            .Build();

    private static JsonElement Cfg(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    private static StepExecutionContext Ctx() =>
        new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "sp-1" };

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void BuildScript_MissingFilePath_Throws()
    {
        var act = () => new Accessor().CallBuildScript(Cfg(new { arguments = "x" }), Ctx());
        act.Should().Throw<InvalidOperationException>().WithMessage("*filePath*");
    }

    [Fact]
    public void BuildScript_EmbedsEscapedFilePathAndArgs()
    {
        // Paths with single quotes must be PS-escaped by doubling.
        var script = new Accessor().CallBuildScript(Cfg(new
        {
            filePath = @"C:\Program Files\foo's app\tool.exe",
            arguments = "-x 'inner quote' -y",
            workingDirectory = @"C:\Temp",
        }), Ctx());

        script.Should().Contain("'C:\\Program Files\\foo''s app\\tool.exe'");
        script.Should().Contain("'-x ''inner quote'' -y'");
        script.Should().Contain("'C:\\Temp'");
        script.Should().Contain("###NODEPILOT_PROGRAM_RESULT_START###");
        script.Should().Contain("###NODEPILOT_PROGRAM_RESULT_END###");
    }

    [Fact]
    public void BuildScript_DefaultsWaitTrueUseShellFalse()
    {
        var script = new Accessor().CallBuildScript(Cfg(new { filePath = CmdPath }), Ctx());
        script.Should().Contain("$__useShell = $false");
        script.Should().Contain("$__wait = $true");
        // C2: missing timeoutSeconds falls back to the documented 300s default
        // (= 300_000 ms) instead of Process.WaitForExit's "wait forever" sentinel.
        // A stuck wait-mode process no longer pins the step indefinitely.
        script.Should().Contain($"$__timeoutMs = {StartProgramActivity.DefaultTimeoutSeconds * 1000}");
    }

    [Fact]
    public void BuildScript_ExplicitTimeout_PassesThroughInMs()
    {
        var script = new Accessor().CallBuildScript(
            Cfg(new { filePath = CmdPath, timeoutSeconds = 45 }), Ctx());
        script.Should().Contain("$__timeoutMs = 45000");
    }

    [Fact]
    public void BuildScript_FireAndForgetRespectsFalseWait()
    {
        var script = new Accessor().CallBuildScript(Cfg(new
        {
            filePath = CmdPath,
            waitForExit = false,
        }), Ctx());
        script.Should().Contain("$__wait = $false");
    }

    [Fact]
    public void BuildScript_RelativeFilePath_Rejected()
    {
        var act = () => new Accessor().CallBuildScript(Cfg(new { filePath = "cmd.exe" }), Ctx());
        act.Should().Throw<InvalidOperationException>().WithMessage("*absolute local path*");
    }

    [Fact]
    public void BuildScript_UncFilePath_Rejected()
    {
        var act = () => new Accessor().CallBuildScript(Cfg(new { filePath = @"\\attacker\share\tool.exe" }), Ctx());
        act.Should().Throw<InvalidOperationException>().WithMessage("*UNC path*");
    }

    [Fact]
    public void BuildScript_UseShellExecuteTrue_RejectedByDefault()
    {
        // Phase-3 hardening: an empty IConfiguration now reads the missing
        // StartProgram:DisallowShellExecute as "true" so a stripped-down deployment falls
        // on the safe side. Activities running with a NULL configuration (load harness)
        // keep the old permissive behaviour — see BuildScript_UseShellExecuteTrue_NullConfig_Allowed.
        var act = () => new Accessor().CallBuildScript(Cfg(new
        {
            filePath = @"C:\data\report.xlsx",
            useShellExecute = true,
        }), Ctx());
        act.Should().Throw<InvalidOperationException>().WithMessage("*useShellExecute*");
    }

    [Fact]
    public void BuildScript_UseShellExecuteTrue_AllowedWhenExplicitlyDisabled()
    {
        // Dev-mode escape hatch: setting DisallowShellExecute=false explicitly (the dev
        // appsettings override) lets the shell-mediated launch flow through.
        var script = new Accessor(AllowShellExecuteConfig()).CallBuildScript(Cfg(new
        {
            filePath = @"C:\data\report.xlsx",
            useShellExecute = true,
        }), Ctx());
        script.Should().Contain("$__useShell = $true");
    }

    [Fact]
    public void BuildScript_UseShellExecuteTrue_Blocked_WhenOptedIn()
    {
        var act = () => new Accessor(DisallowShellExecuteConfig()).CallBuildScript(Cfg(new
        {
            filePath = @"C:\data\report.xlsx",
            useShellExecute = true,
        }), Ctx());
        act.Should().Throw<InvalidOperationException>().WithMessage("*useShellExecute*");
    }

    [Fact]
    public void PostProcess_NoMarker_PassesThroughRawResult()
    {
        var raw = new ActivityResult { Success = false, Output = "some random stdout", ErrorOutput = "err" };
        var result = new Accessor().CallPostProcess(raw, Cfg(new { }));

        result.Success.Should().BeFalse();
        result.Output.Should().Be("some random stdout");
        result.ErrorOutput.Should().Be("err");
    }

    [Fact]
    public void PostProcess_SuccessExitCode_SetsOutputParametersAndSuccess()
    {
        var json = "{\"Launched\":true,\"ProcessId\":1234,\"ExitCode\":0,\"StdOut\":\"hello\\n\",\"StdErr\":\"\",\"Waited\":true,\"TimedOut\":false}";
        var raw = new ActivityResult
        {
            Success = true,
            Output = $"###NODEPILOT_PROGRAM_RESULT_START###\n{json}\n###NODEPILOT_PROGRAM_RESULT_END###",
        };

        var result = new Accessor().CallPostProcess(raw, Cfg(new { }));

        result.Success.Should().BeTrue();
        result.OutputParameters["exitCode"].Should().Be("0");
        result.OutputParameters["processId"].Should().Be("1234");
        result.OutputParameters["stdout"].Should().Be("hello\n");
        result.OutputParameters["waited"].Should().Be("true");
        result.Output.Should().Contain("PID=1234").And.Contain("ExitCode=0").And.Contain("hello");
        result.ErrorOutput.Should().BeNull();
    }

    [Fact]
    public void PostProcess_NonZeroExitCode_FailsUnlessInSuccessList()
    {
        var json = "{\"Launched\":true,\"ProcessId\":42,\"ExitCode\":2,\"StdOut\":\"\",\"StdErr\":\"oops\",\"Waited\":true,\"TimedOut\":false}";
        var raw = new ActivityResult
        {
            Success = true,
            Output = $"###NODEPILOT_PROGRAM_RESULT_START###\n{json}\n###NODEPILOT_PROGRAM_RESULT_END###",
        };

        var failedByDefault = new Accessor().CallPostProcess(raw, Cfg(new { }));
        failedByDefault.Success.Should().BeFalse();
        failedByDefault.OutputParameters["exitCode"].Should().Be("2");

        var acceptedByList = new Accessor().CallPostProcess(raw, Cfg(new { successExitCodes = "0,2" }));
        acceptedByList.Success.Should().BeTrue();
    }

    [Fact]
    public void PostProcess_Timeout_ReturnsFailureWithClearError()
    {
        var json = "{\"Launched\":true,\"ProcessId\":7,\"ExitCode\":null,\"StdOut\":\"\",\"StdErr\":\"\",\"Waited\":true,\"TimedOut\":true}";
        var raw = new ActivityResult
        {
            Success = true,
            Output = $"###NODEPILOT_PROGRAM_RESULT_START###\n{json}\n###NODEPILOT_PROGRAM_RESULT_END###",
        };

        var result = new Accessor().CallPostProcess(raw, Cfg(new { }));

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("timed out");
    }

    [Fact]
    public void PostProcess_LaunchFailed_ReturnsFailureWithLaunchError()
    {
        var json = "{\"Launched\":false,\"LaunchError\":\"file not found\",\"Waited\":true}";
        var raw = new ActivityResult
        {
            Success = true,
            Output = $"###NODEPILOT_PROGRAM_RESULT_START###\n{json}\n###NODEPILOT_PROGRAM_RESULT_END###",
        };

        var result = new Accessor().CallPostProcess(raw, Cfg(new { }));

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("file not found");
    }

    [Fact]
    public void PostProcess_FireAndForget_SuccessWithoutExitCode()
    {
        var json = "{\"Launched\":true,\"ProcessId\":999,\"ExitCode\":null,\"StdOut\":\"\",\"StdErr\":\"\",\"Waited\":false}";
        var raw = new ActivityResult
        {
            Success = true,
            Output = $"###NODEPILOT_PROGRAM_RESULT_START###\n{json}\n###NODEPILOT_PROGRAM_RESULT_END###",
        };

        var result = new Accessor().CallPostProcess(raw, Cfg(new { waitForExit = false }));

        result.Success.Should().BeTrue();
        result.OutputParameters["waited"].Should().Be("false");
        result.OutputParameters["processId"].Should().Be("999");
        result.Output.Should().Contain("fire-and-forget");
    }
}
