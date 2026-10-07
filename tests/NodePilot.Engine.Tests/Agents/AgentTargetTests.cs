using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Agents;
using NodePilot.Engine.PowerShell;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public sealed class AgentTargetTests
{
    [Fact]
    public async Task LocalTarget_DoesNotImplicitlyUseServiceIdentity()
    {
        await using var db = TestDbFactory.Create();
        var sessions = new Mock<IRemoteSessionFactory>(MockBehavior.Strict);
        var options = Monitor(new AgentOptions { AllowServiceIdentity = true });
        var engine = new Mock<IPowerShellExecutionEngine>();
        var factory = new AgentTargetFactory(new AgentRunDatabase(db), Mock.Of<ICredentialStore>(), sessions.Object,
            new PowerShellEngineFactory(engine.Object, engine.Object, engine.Object), options, NullLogger<AgentTargetFactory>.Instance);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => factory.CreateAsync(new AgentDefinition(), new StepExecutionContext(), TestContext.Current.CancellationToken));
        await using var allowed = await factory.CreateAsync(new AgentDefinition { UseServiceIdentity = true }, new StepExecutionContext(), TestContext.Current.CancellationToken);
        Assert.Equal("localhost", allowed.Hostname);
        sessions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ServiceIdentity_RequiresAdministratorOptIn_AndUnknownTargetNeverFallsBack()
    {
        await using var db = TestDbFactory.Create();
        var engine = new Mock<IPowerShellExecutionEngine>();
        var factory = new AgentTargetFactory(new AgentRunDatabase(db), Mock.Of<ICredentialStore>(), Mock.Of<IRemoteSessionFactory>(),
            new PowerShellEngineFactory(engine.Object, engine.Object, engine.Object), Monitor(new AgentOptions()), NullLogger<AgentTargetFactory>.Instance);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => factory.CreateAsync(new AgentDefinition { UseServiceIdentity = true }, new StepExecutionContext(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => factory.CreateAsync(new AgentDefinition { TargetMachineId = Guid.NewGuid() }, new StepExecutionContext(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TargetCredentialAndMachineAreHostBound_AndPolicyRevocationStopsNewCalls()
    {
        await using var db = TestDbFactory.Create();
        var options = new AgentOptions();
        var machine = new ManagedMachine { Id = Guid.NewGuid(), Name = "target", Hostname = "target.invalid" };
        var credential = new Credential { Id = Guid.NewGuid(), Username = "restricted-user" };
        var session = new Mock<IRemoteSession>();
        session.Setup(s => s.ExecuteScriptAsync("fixed script", 60, It.IsAny<CancellationToken>())).ReturnsAsync(new RemoteExecutionResult { Success = true, Output = "ok" });
        var sessions = new Mock<IRemoteSessionFactory>();
        sessions.Setup(s => s.CreateSessionAsync(machine, credential, It.IsAny<CancellationToken>())).ReturnsAsync(session.Object);
        var engine = new Mock<IPowerShellExecutionEngine>();
        await using var target = new AgentTarget(machine, credential, false, sessions.Object, new PowerShellEngineFactory(engine.Object, engine.Object, engine.Object),
            Monitor(options), "step", NullLogger.Instance);
        Assert.Equal("ok", await target.ExecuteAsync("fixed script", TestContext.Current.CancellationToken));
        options.Enabled = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => target.ExecuteAsync("second call", TestContext.Current.CancellationToken));
        sessions.Verify(s => s.CreateSessionAsync(machine, credential, It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static IOptionsMonitor<AgentOptions> Monitor(AgentOptions options)
    {
        var monitor = new Mock<IOptionsMonitor<AgentOptions>>(); monitor.SetupGet(m => m.CurrentValue).Returns(options); return monitor.Object;
    }

    [Fact]
    public async Task InvalidStagingResponse_IsNeverRememberedForRecursiveCleanup()
    {
        var session = new Mock<IRemoteSession>();
        session.Setup(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteExecutionResult { Success = true, Output = "C:\\" });
        var sessions = new Mock<IRemoteSessionFactory>();
        sessions.Setup(s => s.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>())).ReturnsAsync(session.Object);
        var engine = Mock.Of<IPowerShellExecutionEngine>();
        var target = new AgentTarget(new ManagedMachine { Hostname = "bound-target" }, new Credential(), false, sessions.Object,
            new PowerShellEngineFactory(engine, engine, engine), Monitor(new AgentOptions()), "step", NullLogger.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.EnsureWorkingRootAsync(Guid.NewGuid(), "agent", TestContext.Current.CancellationToken));
        await target.DisposeAsync();
        Assert.Equal("", target.WorkingRoot);
        session.Verify(s => s.ExecuteScriptAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
