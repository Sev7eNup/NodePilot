using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Api.Security.Oidc;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security.Oidc;

public sealed class OidcTicketStoreTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly NodePilotDbContext _db;
    private readonly ServiceProvider _provider;

    public OidcTicketStoreTests()
    {
        (_connection, _db) = TestDbFactory.CreateWithConnection();
        _provider = new ServiceCollection()
            .AddSingleton(_db)
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task FiveHundredGroups_AreStoredBehindSmallOpaqueHandle()
    {
        var claims = new List<Claim>
        {
            new("iss", "https://idp.example.test"),
            new("sub", "subject-1"),
        };
        claims.AddRange(Enumerable.Range(0, 500)
            .Select(index => new Claim("groups", $"group-{index:D4}")));
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc")),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) },
            "OidcExternal");
        var store = new OidcTicketStore(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new EphemeralDataProtectionProvider());

        var handle = await store.StoreAsync(ticket);
        _db.ChangeTracker.Clear();
        var restored = await store.RetrieveAsync(handle);

        handle.Length.Should().BeLessThan(100);
        restored.Should().NotBeNull();
        restored!.Principal.FindAll("groups").Should().HaveCount(500);
        (await _db.OidcLoginTickets.FindAsync(handle)).Should().BeNull(
            "retrieval atomically consumes the one-time external handoff");

        await store.RemoveAsync(handle);
        _db.ChangeTracker.Clear();
        (await _db.OidcLoginTickets.FindAsync(handle)).Should().BeNull();
    }

    [Fact]
    public async Task CompetingRetrievals_AllowExactlyOneTicketConsumer()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nodepilot-oidc-ticket-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));
        await using var provider = services.BuildServiceProvider();
        try
        {
            await using (var setup = provider.CreateAsyncScope())
                await setup.ServiceProvider.GetRequiredService<NodePilotDbContext>()
                    .Database.EnsureCreatedAsync();
            var protection = new EphemeralDataProtectionProvider();
            var store = new OidcTicketStore(
                provider.GetRequiredService<IServiceScopeFactory>(), protection);
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("iss", "https://idp.example.test"),
                    new Claim("sub", "subject-1"),
                ], "oidc")),
                new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) },
                "OidcExternal");
            var handle = await store.StoreAsync(ticket);

            var results = await Task.WhenAll(
                store.RetrieveAsync(handle),
                store.RetrieveAsync(handle));

            results.Should().ContainSingle(result => result != null);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task SharedKeyRing_AllowsDifferentHaNodeProviderToReadTicket()
    {
        var keyRing = Path.Combine(Path.GetTempPath(), $"nodepilot-dp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keyRing);
        try
        {
            var nodeAProtection = DataProtectionProvider.Create(
                new DirectoryInfo(keyRing), builder => builder.SetApplicationName("NodePilot"));
            var nodeBProtection = DataProtectionProvider.Create(
                new DirectoryInfo(keyRing), builder => builder.SetApplicationName("NodePilot"));
            var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
            var nodeA = new OidcTicketStore(scopeFactory, nodeAProtection);
            var nodeB = new OidcTicketStore(scopeFactory, nodeBProtection);
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("iss", "https://idp.example.test"),
                    new Claim("sub", "subject-1"),
                ], "oidc")),
                new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) },
                "OidcExternal");

            var handle = await nodeA.StoreAsync(ticket);
            _db.ChangeTracker.Clear();
            var restoredAfterFailover = await nodeB.RetrieveAsync(handle);

            restoredAfterFailover.Should().NotBeNull();
            restoredAfterFailover!.Principal.FindFirst("sub")!.Value.Should().Be("subject-1");
        }
        finally
        {
            Directory.Delete(keyRing, recursive: true);
        }
    }
}
