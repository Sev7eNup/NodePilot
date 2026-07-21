using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public sealed class RunScriptExecutionTargetTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db = TestDbContext.Create();
    private readonly Mock<IRemoteSessionFactory> _sessionFactory = new(MockBehavior.Strict);
    private readonly Mock<ICredentialStore> _credentialStore = new(MockBehavior.Strict);
    private readonly PowerShellEngineFactory _engineFactory = new(NullLoggerFactory.Instance);

    public void Dispose() => _db.Dispose();

    private RunScriptActivity CreateActivity() =>
        new(_engineFactory, _sessionFactory.Object, _credentialStore.Object, _db, NullLogger<RunScriptActivity>.Instance);

    private static StepExecutionContext Context(ManagedMachine? machine = null, Guid? credentialId = null) => new()
    {
        WorkflowExecutionId = Guid.NewGuid(),
        StepId = "step-1",
        ResolvedMachine = machine,
        TargetMachineId = machine?.Id == Guid.Empty ? null : machine?.Id,
        CredentialId = credentialId,
        Variables = [],
    };

    private static ManagedMachine Machine(string hostname = "web01.corp") => new()
    {
        Id = Guid.NewGuid(),
        Name = hostname,
        Hostname = hostname,
        WinRmPort = 5985,
        UseSsl = false,
    };

    private static JsonElement Script(string script, int? timeoutSeconds = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["script"] = script,
            ["engine"] = "runspace",
        };
        if (timeoutSeconds is not null)
            payload["timeoutSeconds"] = timeoutSeconds.Value;
        return JsonSerializer.SerializeToElement(payload);
    }

    [Fact]
    public async Task ExecuteAsync_NoTargetMachine_RunsLocallyAndDoesNotOpenRemoteSession()
    {
        var activity = CreateActivity();

        var result = await activity.ExecuteAsync(
            Context(),
            Script("Write-Output 'local-ok'"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("local-ok");
        _sessionFactory.VerifyNoOtherCalls();
        _credentialStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_LocalhostTargetWithoutCredential_UsesLocalBypassAndDoesNotOpenRemoteSession()
    {
        var activity = CreateActivity();

        var result = await activity.ExecuteAsync(
            Context(Machine("localhost")),
            Script("Write-Output 'localhost-ok'"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("localhost-ok");
        _sessionFactory.VerifyNoOtherCalls();
        _credentialStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_RemoteTarget_RunsThroughWinRmWithWrappedScript()
    {
        var machine = Machine();
        var session = new Mock<IRemoteSession>(MockBehavior.Strict);
        string? capturedScript = null;
        int? capturedTimeout = null;

        session
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((script, timeout, _) =>
            {
                capturedScript = script;
                capturedTimeout = timeout;
            })
            .ReturnsAsync(new RemoteExecutionResult
            {
                Success = true,
                Output = "remote-ok\n###NODEPILOT_PARAMS###\n{\"server\":\"web01\"}",
                ErrorOutput = "",
                Duration = TimeSpan.FromMilliseconds(25),
            });
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _sessionFactory
            .Setup(f => f.CreateSessionAsync(machine, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        var result = await CreateActivity().ExecuteAsync(
            Context(machine),
            Script("$server = 'web01'; Write-Output 'remote-ok'", timeoutSeconds: 42),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("remote-ok");
        result.OutputParameters.Should().ContainKey("server").WhoseValue.Should().Be("web01");
        capturedTimeout.Should().Be(42);
        capturedScript.Should().Contain("# === NODEPILOT OUTPUT CAPTURE ===");
        capturedScript.Should().Contain("Write-Output 'remote-ok'");
        _sessionFactory.Verify(f => f.CreateSessionAsync(machine, null, It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.ExecuteScriptAsync(It.IsAny<string>(), 42, It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.DisposeAsync(), Times.Once);
        _credentialStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_RemoteTarget_UsesMachineDefaultCredential()
    {
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            Name = "Admin",
            Username = "admin",
            EncryptedPassword = [1],
        };
        var machine = Machine();
        machine.DefaultCredentialId = credential.Id;
        var session = new Mock<IRemoteSession>(MockBehavior.Strict);
        session
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult
            {
                Success = true,
                Output = "ok",
                ErrorOutput = "",
                Duration = TimeSpan.FromMilliseconds(10),
            });
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _credentialStore
            .Setup(s => s.GetAsync(credential.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(credential);
        _sessionFactory
            .Setup(f => f.CreateSessionAsync(machine, credential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        var result = await CreateActivity().ExecuteAsync(
            Context(machine),
            Script("Write-Output 'ok'"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        _credentialStore.Verify(s => s.GetAsync(credential.Id, It.IsAny<CancellationToken>()), Times.Once);
        _sessionFactory.Verify(f => f.CreateSessionAsync(machine, credential, It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.ExecuteScriptAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.DisposeAsync(), Times.Once);
    }

    private static JsonElement IsolatedScript(string script, string engine = "auto", int? memoryLimitMb = null, int? maxProcesses = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["script"] = script,
            ["engine"] = engine,
            ["isolated"] = true,
        };
        if (memoryLimitMb is not null) payload["memoryLimitMb"] = memoryLimitMb.Value;
        if (maxProcesses is not null) payload["maxProcesses"] = maxProcesses.Value;
        return JsonSerializer.SerializeToElement(payload);
    }

    [Fact]
    public async Task ExecuteAsync_IsolatedTrueWithRemoteTarget_IgnoresIsolationAndRunsViaWinRm()
    {
        // isolated is a local-only flag — a remote step already runs off-host via WinRM, so the
        // flag must be a no-op and the step must still go through the (mocked) remote session.
        var machine = Machine();
        var session = new Mock<IRemoteSession>(MockBehavior.Strict);
        session
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult
            {
                Success = true,
                Output = "remote-ok",
                ErrorOutput = "",
                Duration = TimeSpan.FromMilliseconds(5),
            });
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _sessionFactory
            .Setup(f => f.CreateSessionAsync(machine, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        var result = await CreateActivity().ExecuteAsync(
            Context(machine),
            IsolatedScript("Write-Output 'remote-ok'", memoryLimitMb: 256),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("remote-ok");
        _sessionFactory.Verify(f => f.CreateSessionAsync(machine, null, It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.DisposeAsync(), Times.Once);
        _credentialStore.VerifyNoOtherCalls();
    }

    [WindowsFact]
    public async Task ExecuteAsync_IsolatedTrueLocal_RunsOutOfProcessNotInRunspacePool()
    {
        // No target machine + isolated:true must route to a process engine and run in a SEPARATE
        // process — proven by comparing the script's $PID to the test process id. A regression that
        // fell back to the in-process runspace pool would emit the test process id and fail here.
        var result = await CreateActivity().ExecuteAsync(
            Context(),
            IsolatedScript("Write-Output \"iso-ok $PID\"", engine: "powershell"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("iso-ok");

        var match = System.Text.RegularExpressions.Regex.Match(result.Output, @"iso-ok (\d+)");
        match.Success.Should().BeTrue("the isolated script should have emitted its $PID");
        int.Parse(match.Groups[1].Value).Should().NotBe(
            Process.GetCurrentProcess().Id,
            "isolated execution must run out-of-process, not in the in-process runspace pool");

        _sessionFactory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_IsolatedLocalNoProcessHost_ReturnsCleanStepFailure()
    {
        // GetEngine(isolated:true) throws when no out-of-process host exists; the activity must
        // translate that into a clean step failure, not let it fault the run. Host-independent —
        // resolution throws before any launch.
        var factory = new PowerShellEngineFactory(
            new FakeEngine("pwsh", available: false),
            new FakeEngine("powershell", available: false),
            new FakeEngine("runspace", available: true));
        var activity = new RunScriptActivity(factory, NullLogger<RunScriptActivity>.Instance);

        var result = await activity.ExecuteAsync(
            Context(),
            IsolatedScript("Write-Output 'x'"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("no PowerShell host");
    }

    private sealed class FakeEngine(string engineType, bool available) : IPowerShellExecutionEngine
    {
        public string EngineType => engineType;
        public bool IsAvailable => available;
        public Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken ct)
            => Task.FromResult(new PowerShellExecutionResult { Success = true });
    }
}
