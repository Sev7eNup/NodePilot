using FluentAssertions;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// The single target and credential resolution shared by the remote activities, runScript,
/// waitForCondition and custom activities, including the localhost bypass.
/// </summary>
public sealed class ActivityTargetResolverTests : IDisposable
{
    private readonly Data.NodePilotDbContext _db = TestDbContext.Create();

    public void Dispose() => _db.Dispose();

    private static ManagedMachine Machine(string hostname, Guid? defaultCredentialId = null) => new()
    {
        Id = Guid.NewGuid(), Name = hostname, Hostname = hostname, WinRmPort = 5985,
        DefaultCredentialId = defaultCredentialId, IsReachable = true,
    };

    private static Credential Cred(Guid id) => new() { Id = id, Name = "c", Username = "u", EncryptedPassword = [1] };

    [Fact]
    public void RunsLocally_NoMachine_IsTrue()
    {
        new ActivityTarget(null, null).RunsLocally.Should().BeTrue();
    }

    [Theory]
    [InlineData("localhost", false, true)]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("localhost", true, false)]
    [InlineData("host.example.net", false, false)]
    [InlineData("host.example.net", true, false)]
    public void RunsLocally_FollowsLoopbackAndCredential(string hostname, bool withCredential, bool expected)
    {
        var target = new ActivityTarget(Machine(hostname), withCredential ? Cred(Guid.NewGuid()) : null);

        target.RunsLocally.Should().Be(expected);
    }

    [Fact]
    public async Task ResolveMachineAsync_PrefersTheEngineResolvedMachine()
    {
        var resolved = Machine("adhoc.example.net");
        var registered = Machine("registered.example.net");
        _db.ManagedMachines.Add(registered);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var context = new StepExecutionContext { StepId = "s", ResolvedMachine = resolved, TargetMachineId = registered.Id };

        var machine = await ActivityTargetResolver.ResolveMachineAsync(context, _db, TestContext.Current.CancellationToken);

        machine.Should().BeSameAs(resolved);
    }

    [Fact]
    public async Task ResolveMachineAsync_FallsBackToTheRegisteredMachine()
    {
        var registered = Machine("registered.example.net");
        _db.ManagedMachines.Add(registered);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var context = new StepExecutionContext { StepId = "s", TargetMachineId = registered.Id };

        var machine = await ActivityTargetResolver.ResolveMachineAsync(context, _db, TestContext.Current.CancellationToken);

        machine!.Id.Should().Be(registered.Id);
    }

    [Fact]
    public async Task ResolveCredentialAsync_StepCredentialWinsOverMachineDefault()
    {
        var stepCredential = Guid.NewGuid();
        var store = new Mock<ICredentialStore>();
        store.Setup(s => s.GetAsync(stepCredential, It.IsAny<CancellationToken>())).ReturnsAsync(Cred(stepCredential));
        var context = new StepExecutionContext { StepId = "s", CredentialId = stepCredential };

        var credential = await ActivityTargetResolver.ResolveCredentialAsync(
            context, Machine("h", defaultCredentialId: Guid.NewGuid()), store.Object, TestContext.Current.CancellationToken);

        credential!.Id.Should().Be(stepCredential);
    }

    [Fact]
    public async Task ResolveCredentialAsync_UsesTheMachineDefault()
    {
        var defaultCredential = Guid.NewGuid();
        var store = new Mock<ICredentialStore>();
        store.Setup(s => s.GetAsync(defaultCredential, It.IsAny<CancellationToken>())).ReturnsAsync(Cred(defaultCredential));

        var credential = await ActivityTargetResolver.ResolveCredentialAsync(
            new StepExecutionContext { StepId = "s" }, Machine("h", defaultCredential), store.Object,
            TestContext.Current.CancellationToken);

        credential!.Id.Should().Be(defaultCredential);
    }

    [Fact]
    public async Task ResolveCredentialAsync_WithoutMachine_LoadsNothing()
    {
        var store = new Mock<ICredentialStore>(MockBehavior.Strict);
        var context = new StepExecutionContext { StepId = "s", CredentialId = Guid.NewGuid() };

        var credential = await ActivityTargetResolver.ResolveCredentialAsync(
            context, machine: null, store.Object, TestContext.Current.CancellationToken);

        credential.Should().BeNull("a local run authenticates against nothing");
    }

    [Fact]
    public async Task ResolveCredentialAsync_ConfiguredCredentialWithoutStore_Throws()
    {
        // Dropping the credential silently would turn a remote run into a service-identity or
        // localhost-bypass run.
        var context = new StepExecutionContext { StepId = "s", CredentialId = Guid.NewGuid() };

        var act = () => ActivityTargetResolver.ResolveCredentialAsync(
            context, Machine("localhost"), credentialStore: null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*credential store*");
    }
}
