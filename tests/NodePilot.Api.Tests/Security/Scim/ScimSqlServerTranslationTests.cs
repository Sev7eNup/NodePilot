using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Security.Scim;

public sealed class ScimSqlServerTranslationTests
{
    [Fact]
    public void TenThousandMemberLookup_UsesAJsonCollectionParameter()
    {
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlServer("Server=(local);Database=NodePilotTranslationOnly;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new NodePilotDbContext(options);
        var ids = Enumerable.Range(1, 10_000)
            .Select(value => new Guid(value, 0, 0, new byte[8]))
            .ToHashSet();

        var sql = db.ExternalIdentities
            .Where(identity => ids.Contains(identity.UserId))
            .ToQueryString();

        sql.Should().Contain("OPENJSON", "SQL Server must receive the collection as one JSON parameter rather than one parameter per member");
        sql.Split("DECLARE ", StringSplitOptions.None).Length.Should().BeLessThan(10,
            "a 10,000-member group must stay well below SQL Server's 2,100-parameter limit");
    }
}
