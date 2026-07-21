using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Remote;
using Xunit;

namespace NodePilot.Engine.Tests.Remote;

/// <summary>
/// Coverage for <see cref="WinRmSessionFactory"/> guards that fire BEFORE the WSMan
/// connection is opened. The full happy path needs a real WinRM listener and is exercised
/// by integration smoke tests in the lab — here we pin the deterministic guards:
///
///   * <c>Remote:RequireWinRmSsl=true</c> rejects plaintext sessions early.
///   * DPAPI decrypt failure surfaces a sanitised <see cref="InvalidOperationException"/>
///     (the raw <c>CryptographicException</c> would leak paths/stack frames into the
///     per-step error channel that a Viewer can read via the executions API).
///   * The credential store is called with an audit-friendly <c>actor</c> tag.
/// </summary>
public class WinRmSessionFactoryTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static IConfiguration RequireSsl() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Remote:RequireWinRmSsl"] = "true" })
            .Build();

    private static ManagedMachine Machine(string hostname = "test-host", bool ssl = false, int port = 5985) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test",
        Hostname = hostname,
        WinRmPort = port,
        UseSsl = ssl,
    };

    private static Credential FakeCredential(string username = "svc", string? domain = "DOMAIN") => new()
    {
        Id = Guid.NewGuid(),
        Name = "fake-cred",
        Username = username,
        Domain = domain,
        EncryptedPassword = new byte[] { 0x01, 0x02 },
    };

    [Fact]
    public async Task CreateSessionAsync_RequireSslSetButMachineUsesHttp_ThrowsBeforeOpen()
    {
        var store = new Mock<ICredentialStore>(MockBehavior.Strict);
        var factory = new WinRmSessionFactory(store.Object, RequireSsl(), NullLogger<WinRmSessionFactory>.Instance);

        var act = () => factory.CreateSessionAsync(Machine(ssl: false), credential: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*WinRM over HTTP is blocked*");
        // Strict mock would have failed if DecryptPassword had been called — we exited
        // before the credential branch.
    }

    [Fact]
    public async Task CreateSessionAsync_DpapiDecryptFails_SurfacesSanitisedException()
    {
        // The raw CryptographicException would include "CRYPT_E_*" codes and frame paths.
        // The factory must wrap it so the per-step error channel only carries the
        // credential id, not the underlying internals. Pin the wrap.
        var cred = FakeCredential();
        var store = new Mock<ICredentialStore>();
        store.Setup(s => s.DecryptPassword(It.IsAny<Credential>(), It.IsAny<string>()))
             .Throws(new System.Security.Cryptography.CryptographicException(
                 "Key not valid for use in specified state."));

        var factory = new WinRmSessionFactory(store.Object, EmptyConfig(), NullLogger<WinRmSessionFactory>.Instance);

        var act = () => factory.CreateSessionAsync(Machine(ssl: true), cred, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain($"Credential decrypt failed (id={cred.Id})");
        ex.Which.Message.Should().NotContain("Key not valid",
            "the underlying CryptographicException message must NOT bubble into the user-visible error " +
            "— it leaks state-of-DPAPI details that a Viewer-role user can read via /api/executions/*/steps");
    }

    [Fact]
    public async Task CreateSessionAsync_DecryptPassword_CalledWithHostnameTaggedActor()
    {
        // Audit contract: actor = "winrm:<hostname>". Without the prefix and host tag,
        // a security review can't tie a decrypt event to a specific target machine — they
        // have to join CredentialAuditLog → ExecutionStep → Workflow → Machine.
        var cred = FakeCredential();
        var store = new Mock<ICredentialStore>();
        store.Setup(s => s.DecryptPassword(It.IsAny<Credential>(), It.IsAny<string>()))
             .Throws(new System.Security.Cryptography.CryptographicException("forced"));
        var factory = new WinRmSessionFactory(store.Object, EmptyConfig(), NullLogger<WinRmSessionFactory>.Instance);

        try
        {
            await factory.CreateSessionAsync(Machine(hostname: "web01.corp", ssl: true), cred, CancellationToken.None);
        }
        catch (InvalidOperationException) { /* expected — decrypt failed */ }

        store.Verify(s => s.DecryptPassword(It.IsAny<Credential>(), "winrm:web01.corp"), Times.Once,
            "the actor tag must be 'winrm:<hostname>' so audit reviewers can scope by target machine");
    }

    [Fact]
    public void Constructor_SingleArg_AcceptsCredentialStoreOnly()
    {
        // Backward-compat constructor for legacy DI registrations (config + logger optional).
        // A regression that drops it would silently break older bootstrap code.
        var store = Mock.Of<ICredentialStore>();

        var factory = new WinRmSessionFactory(store);

        factory.Should().NotBeNull();
    }
}
