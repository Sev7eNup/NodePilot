using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public sealed class WmiQueryActivityTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db;
    private readonly Mock<ICredentialStore> _credentialStore;
    private readonly Mock<IRemoteSessionFactory> _sessionFactory;
    private readonly PowerShellEngineFactory _engineFactory = new(NullLoggerFactory.Instance);
    private readonly Guid _machineId = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();
    private string? _capturedScript;

    public WmiQueryActivityTests()
    {
        _db = TestDbContext.Create();
        _credentialStore = new Mock<ICredentialStore>();

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((script, _, _) => _capturedScript = script)
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = "OK" });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _sessionFactory = new Mock<IRemoteSessionFactory>();
        _sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        _db.Credentials.Add(new Credential { Id = _credentialId, Name = "C", Username = "u", EncryptedPassword = new byte[] { 1 } });
        _db.ManagedMachines.Add(new ManagedMachine
        {
            Id = _machineId, Name = "S", Hostname = "host.local",
            WinRmPort = 5985, DefaultCredentialId = _credentialId, IsReachable = true
        });
        _db.SaveChanges();

        _credentialStore
            .Setup(cs => cs.GetAsync(_credentialId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Credential { Id = _credentialId, Name = "C", Username = "u", EncryptedPassword = new byte[] { 1 } });
    }

    public void Dispose() => _db.Dispose();

    private WmiQueryActivity CreateActivity()
        => new(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory,
               new ConfigurationBuilder().Build());

    private StepExecutionContext Ctx()
        => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1", TargetMachineId = _machineId, CredentialId = _credentialId };

    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    // ---- Error cases ----

    [Fact]
    public async Task MissingClassName_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"namespace\": \"root\\\\cimv2\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*className*");
    }

    [Fact]
    public async Task EmptyClassName_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"className\": \"\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*className*");
    }

    // ---- Script generation ----

    [Fact]
    public async Task ClassName_GeneratesGetCimInstanceScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"className\": \"Win32_Process\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Get-CimInstance");
        _capturedScript.Should().Contain("Win32_Process");
    }

    [Fact]
    public async Task NoNamespace_DefaultsToRootCimv2()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"className\": \"Win32_Process\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("root\\cimv2");
    }

    [Fact]
    public async Task CustomNamespace_UsedInScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"className\": \"MSFT_NetAdapter\", \"namespace\": \"root\\\\StandardCimv2\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("StandardCimv2");
    }

    [Fact]
    public async Task Filter_BoundViaVariable_NotInterpolated()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"className\": \"Win32_Process\", \"filter\": \"Name='notepad.exe'\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__npFilter");
        _capturedScript.Should().Contain("-Filter $__npFilter");
    }

    [Fact]
    public async Task FilterWithApostrophe_EscapedInAssignment()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"className\": \"Win32_Process\", \"filter\": \"Name='o'brian.exe'\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("o''brian");
    }

    [Fact]
    public async Task ExplicitQueryMode_BehavesLikeDefault()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"query\", \"className\": \"Win32_Process\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Get-CimInstance");
        _capturedScript.Should().Contain("Win32_Process");
    }

    [Fact]
    public async Task UnknownMode_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"bogus\", \"className\": \"Win32_Process\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*bogus*");
    }

    // ---- WQL mode ----

    [Fact]
    public async Task WqlMode_GeneratesGetCimInstanceWithQuery()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"wql\", \"query\": \"SELECT * FROM Win32_Service WHERE State='Running'\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Get-CimInstance -Query $__npQuery");
        _capturedScript.Should().Contain("$__npQuery = ");
        _capturedScript.Should().Contain("root\\cimv2");
    }

    [Fact]
    public async Task WqlMode_BindsQueryViaVariable_NotInterpolated()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"wql\", \"query\": \"SELECT * FROM Win32_Process WHERE Name='cmd.exe'\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("$__npQuery");
        _capturedScript.Should().Contain("-Query $__npQuery");
        // Apostrophe inside the value is doubled by PowerShellQuoter — never breaks out.
        _capturedScript.Should().Contain("Name=''cmd.exe''");
    }

    [Fact]
    public async Task WqlMode_CustomNamespace_Used()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"wql\", \"query\": \"SELECT * FROM MSFT_NetAdapter\", \"namespace\": \"root\\\\StandardCimv2\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("StandardCimv2");
    }

    [Fact]
    public async Task WqlMode_MissingQuery_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"wql\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*query*");
    }

    // ---- invokeMethod mode ----

    [Fact]
    public async Task InvokeMethod_StaticMethod_GeneratesInvokeCimMethod()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"invokeMethod\", \"className\": \"Win32_Process\", \"methodName\": \"Create\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Invoke-CimMethod");
        _capturedScript.Should().Contain("-ClassName 'Win32_Process'");
        _capturedScript.Should().Contain("-MethodName 'Create'");
        _capturedScript.Should().NotContain("Get-CimInstance");
    }

    [Fact]
    public async Task InvokeMethod_WithFilter_PipesIntoInvokeCimMethod()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"invokeMethod\", \"className\": \"Win32_Process\", \"filter\": \"Name='notepad.exe'\", \"methodName\": \"Terminate\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Get-CimInstance -ClassName 'Win32_Process'");
        _capturedScript.Should().Contain("-Filter $__npFilter");
        _capturedScript.Should().Contain("| Invoke-CimMethod -MethodName 'Terminate'");
    }

    [Fact]
    public async Task InvokeMethod_WithArguments_RendersHashtableLiteral()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"invokeMethod\", \"className\": \"Win32_Process\", \"methodName\": \"Create\", \"arguments\": {\"CommandLine\": \"notepad.exe\", \"Priority\": 32, \"ShowWindow\": true}}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("-Arguments @{");
        _capturedScript.Should().Contain("CommandLine = 'notepad.exe'");
        _capturedScript.Should().Contain("Priority = 32");
        _capturedScript.Should().Contain("ShowWindow = $true");
    }

    [Fact]
    public async Task InvokeMethod_StringArgWithApostrophe_Escaped()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"invokeMethod\", \"className\": \"Win32_Process\", \"methodName\": \"Create\", \"arguments\": {\"CommandLine\": \"echo it's working\"}}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("CommandLine = 'echo it''s working'");
    }

    [Fact]
    public async Task InvokeMethod_EmptyArguments_OmitsArgumentsFlag()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"invokeMethod\", \"className\": \"Win32_Service\", \"methodName\": \"StopService\", \"arguments\": {}}"),
            CancellationToken.None);
        _capturedScript.Should().NotContain("-Arguments");
    }

    [Fact]
    public async Task InvokeMethod_MissingMethodName_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"invokeMethod\", \"className\": \"Win32_Process\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*methodName*");
    }

    [Fact]
    public async Task InvokeMethod_MissingClassName_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"invokeMethod\", \"methodName\": \"Create\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*className*");
    }

    [Fact]
    public async Task InvokeMethod_BadMethodName_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"invokeMethod\", \"className\": \"Win32_Process\", \"methodName\": \"Create; rm -rf /\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*methodName*");
    }

    [Fact]
    public async Task InvokeMethod_BadArgumentKey_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"invokeMethod\", \"className\": \"Win32_Process\", \"methodName\": \"Create\", \"arguments\": {\"Bad Key; $x\": \"v\"}}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Bad Key*");
    }

    [Fact]
    public async Task InvokeMethod_ArgumentsAsArray_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"invokeMethod\", \"className\": \"Win32_Process\", \"methodName\": \"Create\", \"arguments\": [\"a\", \"b\"]}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*arguments*object*");
    }

    // ---- captureProperties (added 2026-05-17 — closes Caption/Name/... param gap) ----
    //
    // BEFORE this commit, {{wmi_os.param.Caption}} could never resolve because
    // wmiQuery produced no OutputParameters. The activity now optionally wraps the
    // CIM call, projects requested properties into a hashtable, and exposes them as
    // param.<PropName>. These tests pin the contract so the "test workflows broken
    // for months" situation cannot silently come back.

    [Fact]
    public void ParseCaptureProperties_AbsentKey_ReturnsEmpty()
    {
        WmiQueryActivity.ParseCaptureProperties(Cfg("{\"className\": \"X\"}"))
            .Should().BeEmpty("an absent key means \"keep legacy raw-output behaviour\"");
    }

    [Fact]
    public void ParseCaptureProperties_NullValue_ReturnsEmpty()
    {
        WmiQueryActivity.ParseCaptureProperties(Cfg("{\"captureProperties\": null}"))
            .Should().BeEmpty();
    }

    [Fact]
    public void ParseCaptureProperties_ValidArray_ReturnsTrimmedDistinctList()
    {
        var result = WmiQueryActivity.ParseCaptureProperties(
            Cfg("{\"captureProperties\": [\"Caption\", \"BuildNumber\", \"Caption\"]}"));
        // Duplicates dedup'd while preserving first-occurrence order.
        result.Should().Equal("Caption", "BuildNumber");
    }

    [Fact]
    public void ParseCaptureProperties_NonArray_Throws()
    {
        Action act = () => WmiQueryActivity.ParseCaptureProperties(
            Cfg("{\"captureProperties\": \"Caption\"}"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*JSON array*");
    }

    [Fact]
    public void ParseCaptureProperties_NumericEntry_Throws()
    {
        Action act = () => WmiQueryActivity.ParseCaptureProperties(
            Cfg("{\"captureProperties\": [42]}"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*entry must be a string*");
    }

    [Fact]
    public void ParseCaptureProperties_InvalidIdentifier_Throws()
    {
        // Property names land unquoted in the projection script — if we let an
        // attacker-controlled value through, it becomes PS injection. Same rule
        // as method-argument keys.
        Action act = () => WmiQueryActivity.ParseCaptureProperties(
            Cfg("{\"captureProperties\": [\"Bad; rm -rf /\"]}"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*not a valid CIM property identifier*");
    }

    [Fact]
    public void ParseCaptureProperties_ReservedNameCount_Throws()
    {
        // We auto-emit `count` (row total). Letting the user also request it would
        // shadow our value with whatever the CIM object's `count` property happens to
        // be — leading to a confusing contract.
        Action act = () => WmiQueryActivity.ParseCaptureProperties(
            Cfg("{\"captureProperties\": [\"Count\"]}"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*reserved*");
    }

    [Fact]
    public void ParseCaptureProperties_ExceedsMax_Throws()
    {
        var entries = string.Join(",", Enumerable.Range(0, WmiQueryActivity.MaxCaptureProperties + 1)
            .Select(i => $"\"P{i}\""));
        Action act = () => WmiQueryActivity.ParseCaptureProperties(
            Cfg($"{{\"captureProperties\": [{entries}]}}"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*too many*");
    }

    [Fact]
    public async Task CaptureProperties_NotSet_ScriptUnwrapped()
    {
        // Regression guard: when the user doesn't ask for capture, the script must
        // be the plain Get-CimInstance command with no envelope overhead.
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"className\": \"Win32_OperatingSystem\"}"),
            CancellationToken.None);

        _capturedScript.Should().NotBeNull();
        _capturedScript.Should().NotContain("$__npRows");
        _capturedScript.Should().NotContain("NODEPILOT_WMI_RESULT");
    }

    [Fact]
    public async Task CaptureProperties_Set_WrapsScriptWithCaptureEnvelope()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"className\": \"Win32_OperatingSystem\", \"captureProperties\": [\"Caption\", \"BuildNumber\"]}"),
            CancellationToken.None);

        _capturedScript.Should().NotBeNull();
        _capturedScript.Should().Contain("$__npRows = @(& {",
            "the wrap collects all matching rows so we can project the first one");
        _capturedScript.Should().Contain("Get-CimInstance");
        _capturedScript.Should().Contain("$__npResult = @{ Count = $__npRows.Count");
        _capturedScript.Should().Contain("$__npResult.Properties['Caption']");
        _capturedScript.Should().Contain("$__npResult.Properties['BuildNumber']");
        _capturedScript.Should().Contain("NODEPILOT_WMI_RESULT_START");
        _capturedScript.Should().Contain("NODEPILOT_WMI_RESULT_END");
    }

    [Fact]
    public async Task CaptureProperties_WqlMode_AlsoWrapped()
    {
        // Capture works across all three CIM-call modes — the wrap is mode-agnostic.
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"mode\": \"wql\", \"query\": \"SELECT * FROM Win32_BIOS\", \"captureProperties\": [\"SMBIOSBIOSVersion\"]}"),
            CancellationToken.None);

        _capturedScript.Should().Contain("Get-CimInstance -Query");
        _capturedScript.Should().Contain("$__npResult.Properties['SMBIOSBIOSVersion']");
    }

    [Fact]
    public async Task CaptureProperties_FullRoundTrip_PopulatesOutputParameters()
    {
        // Simulate a real WMI run: the remote session returns the formatted-table
        // output followed by the marker block we'd see from a real PS execution.
        const string fakeWmiOutput = """

            Caption                          BuildNumber  Version
            -------                          -----------  -------
            Microsoft Windows 11 Pro         22631        10.0.22631

            ###NODEPILOT_WMI_RESULT_START###
            {"Count":1,"Properties":{"Caption":"Microsoft Windows 11 Pro","BuildNumber":"22631"}}
            ###NODEPILOT_WMI_RESULT_END###
            """;

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = fakeWmiOutput });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var sessionFactory = new Mock<IRemoteSessionFactory>();
        sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var activity = new WmiQueryActivity(
            sessionFactory.Object, _credentialStore.Object, _db, _engineFactory,
            new ConfigurationBuilder().Build());

        var result = await activity.ExecuteAsync(Ctx(),
            Cfg("{\"className\": \"Win32_OperatingSystem\", \"captureProperties\": [\"Caption\", \"BuildNumber\"]}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("count").WhoseValue.Should().Be("1");
        result.OutputParameters.Should().ContainKey("Caption").WhoseValue.Should().Be("Microsoft Windows 11 Pro");
        result.OutputParameters.Should().ContainKey("BuildNumber").WhoseValue.Should().Be("22631");
        result.Output.Should().NotContain("NODEPILOT_WMI_RESULT",
            "the marker block must be stripped from user-visible output");
        result.Output.Should().Contain("Caption",
            "the formatted-table portion must survive so {{step.output}} stays useful");
    }

    [Fact]
    public async Task CaptureProperties_RequestedKeyMissingInJson_EmittedAsEmptyParam()
    {
        // Contract: every property the user listed is ALWAYS present in OutputParameters,
        // even if the CIM object didn't have it. This way a downstream
        // {{step.param.SerialNumber}} resolves to "" instead of hitting the engine's
        // unresolved-variable diagnostics with a misleading "param does not exist" —
        // the param is there, just empty.
        const string output = """

            ###NODEPILOT_WMI_RESULT_START###
            {"Count":1,"Properties":{"Caption":"ok"}}
            ###NODEPILOT_WMI_RESULT_END###
            """;

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = output });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var sessionFactory = new Mock<IRemoteSessionFactory>();
        sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var activity = new WmiQueryActivity(
            sessionFactory.Object, _credentialStore.Object, _db, _engineFactory,
            new ConfigurationBuilder().Build());

        var result = await activity.ExecuteAsync(Ctx(),
            Cfg("{\"className\": \"Win32_OperatingSystem\", \"captureProperties\": [\"Caption\", \"SerialNumber\"]}"),
            CancellationToken.None);

        result.OutputParameters.Should().ContainKey("Caption").WhoseValue.Should().Be("ok");
        result.OutputParameters.Should().ContainKey("SerialNumber").WhoseValue.Should().Be(string.Empty);
    }

    [Fact]
    public async Task CaptureProperties_ZeroRows_EmitsCountZeroAndEmptyParams()
    {
        const string output = """

            ###NODEPILOT_WMI_RESULT_START###
            {"Count":0,"Properties":{}}
            ###NODEPILOT_WMI_RESULT_END###
            """;

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = output });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var sessionFactory = new Mock<IRemoteSessionFactory>();
        sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var activity = new WmiQueryActivity(
            sessionFactory.Object, _credentialStore.Object, _db, _engineFactory,
            new ConfigurationBuilder().Build());

        var result = await activity.ExecuteAsync(Ctx(),
            Cfg("{\"className\": \"Win32_BIOS\", \"filter\": \"Manufacturer='NoMatch'\", \"captureProperties\": [\"SMBIOSBIOSVersion\"]}"),
            CancellationToken.None);

        result.OutputParameters.Should().ContainKey("count").WhoseValue.Should().Be("0");
        result.OutputParameters.Should().ContainKey("SMBIOSBIOSVersion").WhoseValue.Should().Be(string.Empty);
    }
}
