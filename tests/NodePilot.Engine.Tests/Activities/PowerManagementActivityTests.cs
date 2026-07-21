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

public sealed class PowerManagementActivityTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db;
    private readonly Mock<ICredentialStore> _credentialStore;
    private readonly Mock<IRemoteSessionFactory> _sessionFactory;
    private readonly PowerShellEngineFactory _engineFactory = new(NullLoggerFactory.Instance);
    private readonly Guid _machineId = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();
    private string? _capturedScript;

    public PowerManagementActivityTests()
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

    private PowerManagementActivity CreateActivity()
        => new(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory,
               new ConfigurationBuilder().Build());

    private StepExecutionContext Ctx()
        => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1", TargetMachineId = _machineId, CredentialId = _credentialId };

    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    // ---- Error cases ----

    // Defence-in-depth: PowerManagement is all-destructive (shutdown/restart/logoff/
    // hibernate), so the backend must NOT silently default a missing `action`. The UI
    // persists the visual default on first render to heal the common case; this throw
    // catches malformed imports, AI-generated workflows, and hand-edited JSON before
    // they shut down a remote host.
    [Fact]
    public async Task MissingAction_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*action*");
    }

    [Fact]
    public async Task EmptyAction_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"action\":\"\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*action*");
    }

    [Fact]
    public async Task UnknownAction_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"action\": \"nuke\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*nuke*");
    }

    // ---- Script generation ----

    [Fact]
    public async Task Shutdown_GeneratesShutdownExeWithSlashS()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"action\": \"shutdown\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("shutdown.exe");
        _capturedScript.Should().Contain("/s");
    }

    [Fact]
    public async Task Restart_GeneratesShutdownExeWithSlashR()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"action\": \"restart\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("shutdown.exe");
        _capturedScript.Should().Contain("/r");
    }

    [Fact]
    public async Task Logoff_GeneratesShutdownExeWithSlashL()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"action\": \"logoff\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("shutdown.exe");
        _capturedScript.Should().Contain("/l");
    }

    [Fact]
    public async Task Abort_GeneratesShutdownExeWithSlashA()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"action\": \"abort\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("/a");
    }

    [Fact]
    public async Task Hibernate_GeneratesShutdownExeWithSlashH()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"action\": \"hibernate\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("/h");
    }

    [Fact]
    public async Task Shutdown_DefaultForceTrue_IncludesSlashF()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"action\": \"shutdown\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("/f");
    }

    [Fact]
    public async Task Shutdown_ForceFalse_NoSlashF()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"action\": \"shutdown\", \"force\": false}"),
            CancellationToken.None);
        _capturedScript.Should().NotContain("/f");
    }

    [Fact]
    public async Task Shutdown_WithDelay_IncludesTimerFlag()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"action\": \"shutdown\", \"delaySeconds\": 30}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("/t");
        _capturedScript.Should().Contain("30");
    }

    [Fact]
    public async Task Restart_WithMessage_MessageEscapedInScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"action\": \"restart\", \"message\": \"Maintenance restart\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("/c");
        _capturedScript.Should().Contain("Maintenance restart");
    }

    [Fact]
    public async Task Restart_MessageWithApostrophe_EscapedInScript()
    {
        _capturedScript = null;
        await CreateActivity().ExecuteAsync(Ctx(),
            Cfg("{\"action\": \"restart\", \"message\": \"Admin's reboot\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Admin''s reboot");
    }
}
