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

public sealed class BaseRemoteActivityTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db;
    private readonly Mock<ICredentialStore> _credentialStore;
    private readonly Mock<IRemoteSessionFactory> _sessionFactory;
    private readonly PowerShellEngineFactory _engineFactory = new(NullLoggerFactory.Instance);

    private readonly Guid _machineId = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();

    public BaseRemoteActivityTests()
    {
        _db = TestDbContext.Create();
        _credentialStore = new Mock<ICredentialStore>();
        _sessionFactory = MockRemoteSession.CreateFactory();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    // Localhost-bypass is gated by config; opt in here so BaseRemoteActivity tests exercising
    // the local-engine path still reach it.
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Remote:AllowLocalhostBypass"] = "true",
        })
        .Build();

    private ServiceManagementActivity CreateActivity() =>
        new(_sessionFactory.Object, _credentialStore.Object, _db, _engineFactory, _configuration);

    private StepExecutionContext CreateContext(Guid? targetMachineId = null, Guid? credentialId = null) =>
        new()
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "step-1",
            TargetMachineId = targetMachineId,
            CredentialId = credentialId
        };

    private JsonElement CreateConfig(string serviceName = "Spooler", string action = "status") =>
        JsonDocument.Parse($"{{\"serviceName\": \"{serviceName}\", \"action\": \"{action}\"}}").RootElement;

    private ManagedMachine CreateMachine(Guid? defaultCredentialId = null) =>
        new()
        {
            Id = _machineId,
            Name = "TestServer",
            Hostname = "test-server.local",
            WinRmPort = 5985,
            UseSsl = false,
            DefaultCredentialId = defaultCredentialId,
            IsReachable = true
        };

    private Credential CreateCredential() =>
        new()
        {
            Id = _credentialId,
            Name = "TestCred",
            Username = "admin",
            EncryptedPassword = new byte[] { 1, 2, 3 },
            Domain = "TESTDOMAIN"
        };

    private void SeedCredentialInDb()
    {
        _db.Credentials.Add(CreateCredential());
        _db.SaveChanges();
    }

    private void SeedMachine(Guid? defaultCredentialId = null)
    {
        if (defaultCredentialId is not null)
            SeedCredentialInDb();
        _db.ManagedMachines.Add(CreateMachine(defaultCredentialId));
        _db.SaveChanges();
    }

    private void SetupCredentialStore()
    {
        _credentialStore
            .Setup(cs => cs.GetAsync(_credentialId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCredential());
    }

    [Fact]
    public async Task ExecuteAsync_NoTargetMachine_ReturnsFailure()
    {
        var activity = CreateActivity();
        var context = CreateContext(targetMachineId: null);
        var config = CreateConfig();

        var result = await activity.ExecuteAsync(context, config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Be("No target machine specified");
    }

    [Fact]
    public async Task ExecuteAsync_MachineNotFound_ReturnsFailure()
    {
        // When the target machine GUID doesn't exist in the DB, the activity returns a failure
        // result (no exception). This lets the workflow engine mark the step Failed cleanly.
        var activity = CreateActivity();
        var context = CreateContext(targetMachineId: Guid.NewGuid());
        var config = CreateConfig();

        var result = await activity.ExecuteAsync(context, config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_NoCredential_UsesImplicitWinRmIdentity()
    {
        // No credential = use the NodePilot process identity. For non-localhost, WinRM is
        // attempted (mock session returns success). Legitimate usage, must not throw.
        SeedMachine(defaultCredentialId: null);
        var activity = CreateActivity();
        var context = CreateContext(targetMachineId: _machineId, credentialId: null);
        var config = CreateConfig("Get-Process");

        var result = await activity.ExecuteAsync(context, config, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulExecution_ReturnsSuccessResult()
    {
        SeedMachine(defaultCredentialId: _credentialId);
        SetupCredentialStore();
        var activity = CreateActivity();
        var context = CreateContext(targetMachineId: _machineId, credentialId: _credentialId);
        var config = CreateConfig("Get-Process");

        var result = await activity.ExecuteAsync(context, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("OK");
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAsync_UsesDefaultCredential_WhenContextCredentialNull()
    {
        SeedMachine(defaultCredentialId: _credentialId);
        SetupCredentialStore();
        var activity = CreateActivity();
        var context = CreateContext(targetMachineId: _machineId, credentialId: null);
        var config = CreateConfig("Get-Process");

        var result = await activity.ExecuteAsync(context, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        _credentialStore.Verify(cs => cs.GetAsync(_credentialId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Localhost bypass: wrapper-marker stripping ----

    private ServiceManagementActivity CreateLocalhostActivity(string engineOutput)
    {
        // Localhost + no credential routes through the local engine, and both local engines wrap
        // the script via PowerShellScriptWrapper — stdout therefore ends with the
        // ###NODEPILOT_EXITCODE### block (plus an optional PARAMS block). The WinRM path never
        // produces markers, so PostProcess implementations are written against marker-free output.
        var fakeEngine = new Mock<IPowerShellExecutionEngine>();
        fakeEngine.SetupGet(e => e.IsAvailable).Returns(true);
        fakeEngine.SetupGet(e => e.EngineType).Returns("runspace");
        fakeEngine
            .Setup(e => e.ExecuteAsync(It.IsAny<PowerShellExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerShellExecutionResult { Success = true, Output = engineOutput });
        var engineFactory = new PowerShellEngineFactory(fakeEngine.Object, fakeEngine.Object, fakeEngine.Object);

        _db.ManagedMachines.Add(new ManagedMachine
        {
            Id = _machineId, Name = "Local", Hostname = "localhost",
            WinRmPort = 5985, DefaultCredentialId = null, IsReachable = true
        });
        _db.SaveChanges();

        return new ServiceManagementActivity(
            _sessionFactory.Object, _credentialStore.Object, _db, engineFactory, _configuration);
    }

    [Fact]
    public async Task ExecuteAsync_LocalhostBypass_StripsMarkers_SoStatusPostProcessEmitsParams()
    {
        // Regression: the marker block reached PostProcess un-stripped, JsonDocument.Parse threw
        // on the trailing marker text, and serviceManagement/status silently lost its
        // OutputParameters — downstream {{step.param.status}} failed as unresolved.
        var activity = CreateLocalhostActivity(
            "{\"Name\":\"wuauserv\",\"Status\":\"Running\",\"StartType\":\"Manual\"}\r\n###NODEPILOT_EXITCODE###\r\n0");
        var context = CreateContext(targetMachineId: _machineId, credentialId: null);

        var result = await activity.ExecuteAsync(context, CreateConfig("wuauserv", "status"), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().NotContain("###NODEPILOT");
        result.OutputParameters["status"].Should().Be("Running");
        result.OutputParameters["name"].Should().Be("wuauserv");
        result.OutputParameters["startType"].Should().Be("Manual");
    }

    [Fact]
    public async Task ExecuteAsync_LocalhostBypass_DropsWrapperCapturedParams_ForWinRmParity()
    {
        // The wrapper's variable capture (PARAMS block) is a runScript feature; remote-activity
        // scripts get their OutputParameters exclusively from PostProcess. Wrapper-captured
        // locals must be dropped, not surfaced — the WinRM path can never provide them.
        var activity = CreateLocalhostActivity(
            "Some output\r\n###NODEPILOT_PARAMS###\r\n{\"helper\":\"x\"}\r\n###NODEPILOT_EXITCODE###\r\n0");
        var context = CreateContext(targetMachineId: _machineId, credentialId: null);

        var result = await activity.ExecuteAsync(context, CreateConfig("Spooler", "start"), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("Some output");
        result.OutputParameters.Should().BeEmpty();
    }
}
