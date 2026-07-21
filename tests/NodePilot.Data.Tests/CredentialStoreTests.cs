using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

public sealed class CredentialStoreTests : IDisposable
{
    private readonly NodePilotDbContext _context;
    private readonly CredentialStore _store;

    public CredentialStoreTests()
    {
        _context = TestDbFactory.Create();
        _store = new CredentialStore(_context, new NodePilot.Data.Security.DpapiSecretProtector(System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    [Fact]
    public async Task CreateAsync_StoresEncryptedCredential()
    {
        var plainPassword = "SuperSecret123!";

        var credential = await _store.CreateAsync("TestCred", "admin", plainPassword, null, null, CancellationToken.None);

        credential.Should().NotBeNull();
        credential.Name.Should().Be("TestCred");
        credential.Username.Should().Be("admin");
        credential.EncryptedPassword.Should().NotBeNull();
        credential.EncryptedPassword.Should().NotBeEmpty();

        // Encrypted bytes should differ from plaintext bytes
        var plainBytes = Encoding.UTF8.GetBytes(plainPassword);
        credential.EncryptedPassword.Should().NotEqual(plainBytes);
    }

    [Fact]
    public async Task GetAsync_ExistingId_ReturnsCredential()
    {
        var created = await _store.CreateAsync("MyCred", "user1", "pass1", "DOMAIN", null, CancellationToken.None);

        var retrieved = await _store.GetAsync(created.Id, CancellationToken.None);

        retrieved.Should().NotBeNull();
        retrieved.Id.Should().Be(created.Id);
        retrieved.Name.Should().Be("MyCred");
        retrieved.Username.Should().Be("user1");
        retrieved.Domain.Should().Be("DOMAIN");
    }

    [Fact]
    public async Task GetAsync_NonexistentId_ThrowsKeyNotFoundException()
    {
        var act = () => _store.GetAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllOrderedByName()
    {
        await _store.CreateAsync("Zebra", "z_user", "pass", null, null, CancellationToken.None);
        await _store.CreateAsync("Alpha", "a_user", "pass", null, null, CancellationToken.None);
        await _store.CreateAsync("Middle", "m_user", "pass", null, null, CancellationToken.None);

        var all = await _store.GetAllAsync(CancellationToken.None);

        all.Should().HaveCount(3);
        all[0].Name.Should().Be("Alpha");
        all[1].Name.Should().Be("Middle");
        all[2].Name.Should().Be("Zebra");
    }

    [Fact]
    public async Task CreateAsync_WithExpiresAt_PersistsIt()
    {
        var expires = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var created = await _store.CreateAsync("Expiring", "user", "pass1234", null, expires, CancellationToken.None);

        var fetched = await _store.GetAsync(created.Id, CancellationToken.None);
        fetched.ExpiresAt.Should().Be(expires);
    }

    [Fact]
    public async Task UpdateAsync_CanSetAndClearExpiresAt()
    {
        var created = await _store.CreateAsync("Cred", "user", "pass1234", null, null, CancellationToken.None);

        var expires = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        await _store.UpdateAsync(created.Id, "Cred", "user", null, null, expires, CancellationToken.None);
        (await _store.GetAsync(created.Id, CancellationToken.None)).ExpiresAt.Should().Be(expires);

        await _store.UpdateAsync(created.Id, "Cred", "user", null, null, null, CancellationToken.None);
        (await _store.GetAsync(created.Id, CancellationToken.None)).ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_WithPassword_ReEncrypts()
    {
        var created = await _store.CreateAsync("Cred", "user", "OldPassword", null, null, CancellationToken.None);
        var originalEncrypted = created.EncryptedPassword.ToArray();

        await _store.UpdateAsync(created.Id, "Cred", "user", "NewPassword", null, null, CancellationToken.None);

        var updated = await _store.GetAsync(created.Id, CancellationToken.None);
        updated.EncryptedPassword.Should().NotEqual(originalEncrypted);

        var decrypted = _store.DecryptPassword(updated);
        decrypted.Should().Be("NewPassword");
    }

    [Fact]
    public async Task UpdateAsync_WithoutPassword_KeepsExistingPassword()
    {
        var created = await _store.CreateAsync("Cred", "user", "OriginalPass", null, null, CancellationToken.None);

        await _store.UpdateAsync(created.Id, "UpdatedCred", "new_user", null, "NEWDOMAIN", null, CancellationToken.None);

        var updated = await _store.GetAsync(created.Id, CancellationToken.None);
        updated.Name.Should().Be("UpdatedCred");
        updated.Username.Should().Be("new_user");
        updated.Domain.Should().Be("NEWDOMAIN");

        var decrypted = _store.DecryptPassword(updated);
        decrypted.Should().Be("OriginalPass");
    }

    [Fact]
    public async Task DeleteAsync_RemovesCredential()
    {
        var created = await _store.CreateAsync("ToDelete", "user", "pass", null, null, CancellationToken.None);

        await _store.DeleteAsync(created.Id, CancellationToken.None);

        var act = () => _store.GetAsync(created.Id, CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task DecryptPassword_RoundTrip_ReturnsOriginalPassword()
    {
        var originalPassword = "My$ecure_P@ssw0rd!";

        var credential = await _store.CreateAsync("RoundTrip", "user", originalPassword, null, null, CancellationToken.None);

        var decrypted = _store.DecryptPassword(credential);

        decrypted.Should().Be(originalPassword);
    }
}
