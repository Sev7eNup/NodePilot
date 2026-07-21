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

/// <summary>
/// Tests for ServiceManagementActivity error cases and edge cases.
/// Happy-path script generation (start/stop/restart/status) is covered in BuildScriptTests.
/// </summary>
public sealed class ServiceManagementActivityTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db;
    private readonly Mock<ICredentialStore> _credentialStore;
    private readonly Mock<IRemoteSessionFactory> _sessionFactory;
    private readonly PowerShellEngineFactory _engineFactory = new(NullLoggerFactory.Instance);
    private readonly Guid _machineId = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();
    private string? _capturedScript;

    public ServiceManagementActivityTests()
    {
        _db = TestDbContext.Create();
        _credentialStore = new Mock<ICredentialStore>();

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((script, _, _) => _capturedScript = script)
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = "Running" });
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

    private ServiceManagementActivity CreateActivity()
        => new(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory,
               new ConfigurationBuilder().Build());

    private StepExecutionContext Ctx()
        => new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1", TargetMachineId = _machineId, CredentialId = _credentialId };

    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    // ---- Error cases ----

    [Fact]
    public async Task MissingServiceName_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"action\": \"start\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*serviceName*");
    }

    [Fact]
    public async Task EmptyServiceName_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"serviceName\": \"\", \"action\": \"start\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UnknownAction_Throws()
    {
        var activity = CreateActivity();
        Func<Task> act = () => activity.ExecuteAsync(Ctx(),
            Cfg("{\"serviceName\": \"Spooler\", \"action\": \"kill\"}"),
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*kill*");
    }

    // ---- Default action behavior ----

    [Fact]
    public async Task NoActionField_DefaultsToStatus()
    {
        var activity = CreateActivity();
        _capturedScript = null;
        await activity.ExecuteAsync(Ctx(),
            Cfg("{\"serviceName\": \"Spooler\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("Get-Service");
    }

    [Fact]
    public async Task NoActionField_StillPostProcessesAndExposesOutputParameters()
    {
        // Regression: BuildScript defaults action to "status", PostProcess must do the same.
        // If it doesn't, downstream edges checking {{x.param.status}} get "" and == fails.
        var activity = CreateActivity();
        var jsonOutput = "{\"Name\": \"Spooler\", \"Status\": \"Running\", \"StartType\": \"Automatic\"}";

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = jsonOutput });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await activity.ExecuteAsync(Ctx(),
            Cfg("{\"serviceName\": \"Spooler\"}"),  // no action field
            CancellationToken.None);

        result.OutputParameters["status"].Should().Be("Running");
        result.OutputParameters["name"].Should().Be("Spooler");
    }

    // ---- Security: apostrophe injection defense ----

    [Fact]
    public async Task ServiceNameWithApostrophe_IsEscapedInScript()
    {
        var activity = CreateActivity();
        _capturedScript = null;
        await activity.ExecuteAsync(Ctx(),
            Cfg("{\"serviceName\": \"O'Brian Service\", \"action\": \"status\"}"),
            CancellationToken.None);
        _capturedScript.Should().Contain("'O''Brian Service'");
        _capturedScript.Should().NotContain("O'Brian Service'");
    }

    // ---- PostProcess: status action OutputParameters ----

    [Fact]
    public async Task ExecuteStatus_ReturnsOutputParameters_WithStateNameStartType()
    {
        var activity = CreateActivity();
        var jsonOutput = "{\"Name\": \"Spooler\", \"Status\": \"Running\", \"StartType\": \"Automatic\"}";

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = jsonOutput });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await activity.ExecuteAsync(Ctx(),
            Cfg("{\"serviceName\": \"Spooler\", \"action\": \"status\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["status"].Should().Be("Running");
        result.OutputParameters["name"].Should().Be("Spooler");
        result.OutputParameters["startType"].Should().Be("Automatic");
    }

    [Fact]
    public async Task ExecuteStatus_StoppedService_ParsesState()
    {
        var activity = CreateActivity();
        var jsonOutput = "{\"Name\": \"Spooler\", \"Status\": \"Stopped\", \"StartType\": \"Manual\"}";

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = jsonOutput });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await activity.ExecuteAsync(Ctx(),
            Cfg("{\"serviceName\": \"Spooler\", \"action\": \"status\"}"),
            CancellationToken.None);

        result.OutputParameters["status"].Should().Be("Stopped");
    }

    [Fact]
    public async Task ExecuteStatus_InvalidJson_ReturnsRawResultWithoutOutputParameters()
    {
        var activity = CreateActivity();
        var invalidOutput = "Status   Name\n------   ----\nRunning  Spooler";  // plain text, not JSON

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = invalidOutput });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await activity.ExecuteAsync(Ctx(),
            Cfg("{\"serviceName\": \"Spooler\", \"action\": \"status\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be(invalidOutput);
        result.OutputParameters.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteStatus_EmptyOutput_ReturnsRawResultWithoutOutputParameters()
    {
        var activity = CreateActivity();

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = false, Output = "" });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await activity.ExecuteAsync(Ctx(),
            Cfg("{\"serviceName\": \"Spooler\", \"action\": \"status\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.OutputParameters.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteStart_DoesNotPostProcess()
    {
        var activity = CreateActivity();
        var output = "Some output";

        var mockSession = new Mock<IRemoteSession>();
        mockSession
            .Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = output });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _sessionFactory
            .Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await activity.ExecuteAsync(Ctx(),
            Cfg("{\"serviceName\": \"Spooler\", \"action\": \"start\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be(output);
        result.OutputParameters.Should().BeEmpty();
    }
}
