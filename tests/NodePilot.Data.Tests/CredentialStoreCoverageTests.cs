using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// Coverage-driving tests for the branches of <see cref="CredentialStore"/> that the
/// baseline <see cref="CredentialStoreTests"/> does not exercise:
/// <list type="bullet">
///   <item><description><c>NormalizeExpiry</c> for <c>Unspecified</c> and <c>Local</c>
///   DateTime kinds (the API/MCP date-only boundary produces Unspecified).</description></item>
///   <item><description>The scope-factory audit path (background scope) — both the
///   success write and the swallowed-failure guard.</description></item>
///   <item><description>The legacy (no scope-factory) audit path when the shared
///   DbContext write throws — the failure must be swallowed, not surfaced.</description></item>
/// </list>
/// </summary>
public sealed class CredentialStoreCoverageTests
{
    private static byte[] DeterministicKey()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 1);
        return k;
    }

    private static NodePilotDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connectionString).Options);

    // ---- NormalizeExpiry branches ------------------------------------------------------

    [Fact]
    public async Task CreateAsync_UnspecifiedExpiry_IsPinnedToUtc_SameClockValue()
    {
        await using var db = TestDbFactory.Create();
        var store = new CredentialStore(db, new AesGcmSecretProtector(DeterministicKey()), NullLogger<CredentialStore>.Instance);

        // A date-only ISO string ("2026-12-31") deserializes to Kind=Unspecified at the
        // API/MCP boundary. NormalizeExpiry must SpecifyKind(Utc) WITHOUT shifting the clock.
        var unspecified = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Unspecified);

        var cred = await store.CreateAsync("cred", "user", "pw1234", null, unspecified, CancellationToken.None);

        cred.ExpiresAt.Should().NotBeNull();
        cred.ExpiresAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        cred.ExpiresAt!.Value.Should().Be(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc));
    }

    [Fact]
    public async Task CreateAsync_LocalExpiry_IsConvertedToUtc()
    {
        await using var db = TestDbFactory.Create();
        var store = new CredentialStore(db, new AesGcmSecretProtector(DeterministicKey()), NullLogger<CredentialStore>.Instance);

        var local = new DateTime(2027, 1, 15, 12, 30, 0, DateTimeKind.Local);

        var cred = await store.CreateAsync("cred", "user", "pw1234", null, local, CancellationToken.None);

        cred.ExpiresAt.Should().NotBeNull();
        cred.ExpiresAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        cred.ExpiresAt!.Value.Should().Be(local.ToUniversalTime());
    }

    [Fact]
    public async Task UpdateAsync_LocalExpiry_IsConvertedToUtc()
    {
        await using var db = TestDbFactory.Create();
        var store = new CredentialStore(db, new AesGcmSecretProtector(DeterministicKey()), NullLogger<CredentialStore>.Instance);
        var cred = await store.CreateAsync("cred", "user", "pw1234", null, null, CancellationToken.None);

        var local = new DateTime(2028, 6, 1, 8, 0, 0, DateTimeKind.Local);
        await store.UpdateAsync(cred.Id, "cred", "user", null, null, local, CancellationToken.None);

        var updated = await store.GetAsync(cred.Id, CancellationToken.None);
        updated.ExpiresAt.Should().NotBeNull();
        updated.ExpiresAt!.Value.Should().Be(local.ToUniversalTime());
    }

    // ---- Scope-factory audit path (background scope) -----------------------------------

    [Fact]
    public async Task DecryptPassword_WithScopeFactory_PersistsAuditRow_OnBackgroundScope()
    {
        // Shared-cache in-memory DB so the store's context, the background scope's context,
        // and the polling context all see the same database across separate connections.
        var connString = $"DataSource=CredStoreScope_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keepAlive = new SqliteConnection(connString);
        await keepAlive.OpenAsync();

        using (var seed = NewContext(connString))
            await seed.Database.EnsureCreatedAsync();

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(connString));
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        Credential cred;
        await using (var db = NewContext(connString))
        {
            var store = new CredentialStore(
                db, new AesGcmSecretProtector(DeterministicKey()), NullLogger<CredentialStore>.Instance, scopeFactory);
            cred = await store.CreateAsync("svc", "user", "s3cret", null, null, CancellationToken.None);

            var plain = store.DecryptPassword(cred, actor: "tester", workflowExecutionId: Guid.NewGuid());
            plain.Should().Be("s3cret");
        }

        // The audit append runs fire-and-forget on an independent scope — wait for it.
        var wrote = await WaitForAuditRowsAsync(connString, TimeSpan.FromSeconds(10));
        wrote.Should().BeTrue(
            "the scope-factory branch must persist a CREDENTIAL_DECRYPTED audit row via an independent DI scope");
    }

    [Fact]
    public async Task DecryptPassword_WithBrokenScopeFactory_SwallowsAuditFailure_AndStillReturnsPlaintext()
    {
        // The background scope resolves a DbContext over a fresh, schema-less :memory: DB;
        // its SaveChanges throws. That exception must be caught & swallowed on the background
        // task and must never affect the (already-returned) decrypt result.
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        await using var db = TestDbFactory.Create();
        var store = new CredentialStore(
            db, new AesGcmSecretProtector(DeterministicKey()), NullLogger<CredentialStore>.Instance, scopeFactory);
        var cred = await store.CreateAsync("svc", "user", "pw", null, null, CancellationToken.None);

        var plain = store.DecryptPassword(cred);
        plain.Should().Be("pw", "a failed audit append must never break the decrypt path");

        // Give the fire-and-forget background task a chance to run its (failing) write.
        await Task.Delay(300);
    }

    // ---- Legacy (no scope-factory) audit path failure ---------------------------------

    [Fact]
    public async Task DecryptPassword_LegacyPath_AuditWriteFailure_IsSwallowed()
    {
        // No scope factory → legacy path writes the audit row on the shared DbContext.
        // Disposing that context before decrypt makes the AuditLog.Add/SaveChanges throw;
        // the store's inner try/catch must swallow it and still return the plaintext.
        var db = TestDbFactory.Create();
        var store = new CredentialStore(db, new AesGcmSecretProtector(DeterministicKey()), NullLogger<CredentialStore>.Instance);
        var cred = await store.CreateAsync("svc", "user", "hunter2", null, null, CancellationToken.None);

        await db.DisposeAsync();

        var plain = store.DecryptPassword(cred);
        plain.Should().Be("hunter2", "a disposed-context audit failure must not break decrypt");
    }

    private static async Task<bool> WaitForAuditRowsAsync(string connectionString, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var db = NewContext(connectionString);
                if (await db.AuditLog.AnyAsync())
                    return true;
            }
            catch
            {
                // Transient SQLite BUSY while the background write holds a lock — retry.
            }
            await Task.Delay(50);
        }
        return false;
    }
}
