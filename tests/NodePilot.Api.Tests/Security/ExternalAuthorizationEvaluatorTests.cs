using FluentAssertions;
using Microsoft.Extensions.Options;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public sealed class ExternalAuthorizationEvaluatorTests
{
    private const string Authority = "https://idp.example.test/tenant";
    private const string AllowedGroup = "nodepilot-users";

    [Fact]
    public async Task UnrelatedFreshGroup_DoesNotExtendStaleAllowedMembership()
    {
        await using var db = TestDbFactory.Create();
        var now = DateTime.UtcNow;
        var user = OidcUser(UserRole.Viewer);
        db.AddRange(user, Identity(user),
            Membership(user, AllowedGroup, now.AddMinutes(-16)),
            Membership(user, "unrelated-heartbeat", now));
        await db.SaveChangesAsync();

        var result = await Evaluator(db).EvaluateAsync(user, now, default);

        result.IsCurrent.Should().BeFalse();
        result.Reason.Should().Be("oidc_allowed_membership_stale_or_missing");
    }

    [Fact]
    public async Task StaleRoleMapping_DoesNotLeaveFreshAllowedUserAsAdmin()
    {
        await using var db = TestDbFactory.Create();
        var now = DateTime.UtcNow;
        var user = OidcUser(UserRole.Admin);
        db.AddRange(user, Identity(user),
            Membership(user, AllowedGroup, now),
            Membership(user, "nodepilot-admins", now.AddMinutes(-16)));
        await db.SaveChangesAsync();

        var result = await Evaluator(db).EvaluateAsync(user, now, default);

        result.IsCurrent.Should().BeFalse();
        result.EffectiveRole.Should().Be(UserRole.Viewer);
        result.Reason.Should().Be("oidc_effective_role_changed");
    }

    [Fact]
    public async Task FreshAllowedAndRoleMemberships_AreCurrentUntilEarliestExpiry()
    {
        await using var db = TestDbFactory.Create();
        var now = DateTime.UtcNow;
        var oldest = now.AddMinutes(-4);
        var user = OidcUser(UserRole.Admin);
        db.AddRange(user, Identity(user),
            Membership(user, AllowedGroup, oldest),
            Membership(user, "nodepilot-admins", now.AddMinutes(-2)));
        await db.SaveChangesAsync();

        var result = await Evaluator(db).EvaluateAsync(user, now, default);

        result.IsCurrent.Should().BeTrue();
        result.ValidUntil.Should().Be(oldest.AddMinutes(15));
    }

    private static ExternalAuthorizationEvaluator Evaluator(NodePilot.Data.NodePilotDbContext db) => new(
        db,
        Options.Create(new AuthenticationPolicyOptions { MaxAuthorizationStalenessMinutes = 15 }),
        Options.Create(new EnterpriseOidcOptions
        {
            Enabled = true,
            Authority = Authority,
            AllowedGroupIds = [AllowedGroup],
            GlobalRoleMappings =
            [
                new OidcRoleMapping { GroupId = "nodepilot-admins", Role = UserRole.Admin },
            ],
        }));

    private static User OidcUser(UserRole role) => new()
    {
        Id = Guid.NewGuid(), Username = "alice@example.test", Provider = AuthProvider.Oidc,
        ExternalId = "subject-1", Role = role, IsActive = true,
    };

    private static ExternalIdentity Identity(User user) => new()
    {
        Id = Guid.NewGuid(), UserId = user.Id, Authority = Authority, Subject = "subject-1",
    };

    private static DirectoryMembership Membership(User user, string group, DateTime observedAt) => new()
    {
        UserId = user.Id, Authority = Authority, GroupKey = group, LastSeenAt = observedAt,
    };
}
