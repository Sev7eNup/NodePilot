using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public sealed class UserSecurityStampConcurrencyTests
{
    [Fact]
    public async Task ConcurrentSecurityMutations_CannotOverwriteNewerStamp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nodepilot-stamp-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        try
        {
            var userId = Guid.NewGuid();
            await using (var seed = new NodePilotDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Users.Add(new User
                {
                    Id = userId, Username = "recovery", Provider = AuthProvider.Local,
                    PasswordHash = "hash", Role = UserRole.Admin, IsActive = true,
                    IsBreakGlass = true,
                });
                await seed.SaveChangesAsync();
            }

            await using var dbA = new NodePilotDbContext(options);
            await using var dbB = new NodePilotDbContext(options);
            var userA = await dbA.Users.SingleAsync(x => x.Id == userId);
            var userB = await dbB.Users.SingleAsync(x => x.Id == userId);
            userA.SecurityStamp++;
            await dbA.SaveChangesAsync();
            userB.SecurityStamp++;

            var saveB = () => dbB.SaveChangesAsync();
            await saveB.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
