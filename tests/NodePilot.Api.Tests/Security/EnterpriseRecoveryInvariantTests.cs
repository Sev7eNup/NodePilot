using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public sealed class EnterpriseRecoveryInvariantTests
{
    [Fact]
    public async Task ExistingSsoOnlyDatabase_IsRejectedAtStartup()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = CreateDb(connection);
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "sso-admin@example.test",
            Provider = AuthProvider.Oidc, Role = UserRole.Admin, IsActive = true,
        });
        await db.SaveChangesAsync();

        var act = () => EnterpriseRecoveryInvariant.EnsureAsync(db, Configuration(oidc: true));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no active local break-glass Admin*");
    }

    [Fact]
    public async Task ExistingDatabase_WithRecoveryAdmin_IsAccepted()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = CreateDb(connection);
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "recovery", PasswordHash = "hash",
            Provider = AuthProvider.Local, Role = UserRole.Admin, IsActive = true,
            IsBreakGlass = true,
        });
        await db.SaveChangesAsync();

        var act = () => EnterpriseRecoveryInvariant.EnsureAsync(db, Configuration(ldap: true));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EmptyDatabase_AllowsOneShotLocalBootstrap()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = CreateDb(connection);

        var act = () => EnterpriseRecoveryInvariant.EnsureAsync(db, Configuration(scim: true));

        await act.Should().NotThrowAsync();
    }

    private static IConfiguration Configuration(
        bool ldap = false,
        bool oidc = false,
        bool scim = false) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Ldap:Enabled"] = ldap.ToString(),
            ["Authentication:Oidc:Enabled"] = oidc.ToString(),
            ["Authentication:Scim:Enabled"] = scim.ToString(),
        })
        .Build();

    private static NodePilot.Data.NodePilotDbContext CreateDb(
        Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var db = new NodePilot.Data.NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilot.Data.NodePilotDbContext>()
                .UseSqlite(connection)
                .Options);
        db.Database.EnsureCreated();
        return db;
    }
}
