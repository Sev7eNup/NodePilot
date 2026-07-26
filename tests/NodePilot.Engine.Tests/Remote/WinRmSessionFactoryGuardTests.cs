using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Remote;
using Xunit;

namespace NodePilot.Engine.Tests.Remote;

/// <summary>
/// The guards <see cref="WinRmSessionFactory"/> applies before it opens a session. Both are
/// security-relevant: plaintext WinRM is refused unless an operator explicitly opted out, and a
/// failed DPAPI decrypt must not leak the raw CryptographicException (which carries paths and
/// stack frames a Viewer can read back through the step-output API).
/// </summary>
public sealed class WinRmSessionFactoryGuardTests
{
    [Fact]
    public async Task CreateSessionAsync_PlaintextWithDefaultConfiguration_IsRefused()
    {
        // Absent key reads as "required" — the hardened default.
        var factory = new WinRmSessionFactory(new StubCredentialStore(), Config([]), null);

        var act = () => factory.CreateSessionAsync(
            Machine(useSsl: false), null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("blocked by configuration");
    }

    [Fact]
    public async Task CreateSessionAsync_PlaintextWithRequireSslTrue_IsRefused()
    {
        var factory = new WinRmSessionFactory(
            new StubCredentialStore(),
            Config(new Dictionary<string, string?> { ["Remote:RequireWinRmSsl"] = "true" }),
            null);

        var act = () => factory.CreateSessionAsync(
            Machine(useSsl: false), null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateSessionAsync_PlaintextWithoutConfiguration_KeepsThePermissiveTestBehaviour()
    {
        // The load harness and unit tests construct the factory without an IConfiguration;
        // that path must stay permissive or every remote test would need a config stub.
        var factory = new WinRmSessionFactory(new StubCredentialStore());

        var act = () => factory.CreateSessionAsync(
            Machine(useSsl: false), null, TestContext.Current.CancellationToken);

        // It gets past the guard and fails later at the actual connect instead.
        (await act.Should().ThrowAsync<Exception>())
            .Which.Message.Should().NotContain("blocked by configuration");
    }

    [Fact]
    public async Task CreateSessionAsync_DecryptFailure_IsSanitisedBeforeItReachesTheCaller()
    {
        var store = new StubCredentialStore
        {
            Throw = new InvalidOperationException("Key not valid for use in specified state. C:\\keys\\dpapi"),
        };
        var factory = new WinRmSessionFactory(
            store,
            Config(new Dictionary<string, string?> { ["Remote:RequireWinRmSsl"] = "false" }),
            null);
        var credential = new Credential
        {
            Id = Guid.NewGuid(), Name = "svc", Username = "svc-acct", EncryptedPassword = [1, 2, 3],
        };

        var act = () => factory.CreateSessionAsync(
            Machine(useSsl: false), credential, TestContext.Current.CancellationToken);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain("Credential decrypt failed");
        thrown.Message.Should().Contain(credential.Id.ToString());
        thrown.Message.Should().NotContain("C:\\keys\\dpapi",
            "the raw decrypt message is log-only — it must not reach the step-output channel");
    }

    [Fact]
    public async Task CreateSessionAsync_DecryptFailure_NamesTheTargetHostAsTheActor()
    {
        var store = new StubCredentialStore { Throw = new InvalidOperationException("nope") };
        var factory = new WinRmSessionFactory(
            store,
            Config(new Dictionary<string, string?> { ["Remote:RequireWinRmSsl"] = "false" }),
            null);

        var act = () => factory.CreateSessionAsync(
            Machine(useSsl: false),
            new Credential { Id = Guid.NewGuid(), Name = "svc", Username = "u", EncryptedPassword = [1] },
            TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();

        store.LastActor.Should().Be("winrm:srv-01.contoso.test",
            "the audit trail records which target a decrypt was performed for");
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ManagedMachine Machine(bool useSsl) => new()
    {
        Id = Guid.NewGuid(),
        Name = "srv-01",
        Hostname = "srv-01.contoso.test",
        WinRmPort = useSsl ? 5986 : 5985,
        UseSsl = useSsl,
    };

    private sealed class StubCredentialStore : ICredentialStore
    {
        public Exception? Throw { get; init; }
        public string? LastActor { get; private set; }

        public string DecryptPassword(
            Credential credential, string? actor = null, Guid? workflowExecutionId = null)
        {
            LastActor = actor;
            if (Throw is not null) throw Throw;
            return "password";
        }

        // Not exercised — the factory only ever decrypts.
        public Task<Credential> GetAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Credential>> GetAllAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<Credential> CreateAsync(
            string name, string username, string password, string? domain, DateTime? expiresAt,
            CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateAsync(
            Guid id, string name, string username, string? password, string? domain,
            DateTime? expiresAt, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReencryptionSummary> ReencryptAllCredentialsAsync(CancellationToken ct)
            => throw new NotSupportedException();
    }
}
