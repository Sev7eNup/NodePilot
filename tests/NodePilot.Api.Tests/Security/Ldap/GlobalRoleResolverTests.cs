using FluentAssertions;
using NodePilot.Api.Security.Ldap;
using NodePilot.Core.Enums;
using Xunit;

namespace NodePilot.Api.Tests.Security.Ldap;

public class GlobalRoleResolverTests
{
    private const string DomainAdminsSid = "S-1-5-21-1004336348-1177238915-682003330-512";
    private const string OperatorsSid = "S-1-5-21-1004336348-1177238915-682003330-1108";
    private const string ViewersSid = "S-1-5-21-1004336348-1177238915-682003330-1109";
    private const string UnmappedSid = "S-1-5-21-1004336348-1177238915-682003330-9999";

    [Fact]
    public void Resolve_NoUserGroups_ReturnsViewerDefault()
    {
        var mappings = new[]
        {
            new GlobalRoleMapping { GroupSid = DomainAdminsSid, Role = UserRole.Admin },
        };

        var role = GlobalRoleResolver.Resolve(Array.Empty<string>(), mappings);

        role.Should().Be(UserRole.Viewer);
    }

    [Fact]
    public void Resolve_NoMappings_ReturnsViewerDefault()
    {
        var role = GlobalRoleResolver.Resolve(new[] { DomainAdminsSid }, Array.Empty<GlobalRoleMapping>());

        role.Should().Be(UserRole.Viewer);
    }

    [Fact]
    public void Resolve_GroupNotInMappings_ReturnsViewerDefault()
    {
        var mappings = new[]
        {
            new GlobalRoleMapping { GroupSid = DomainAdminsSid, Role = UserRole.Admin },
        };

        var role = GlobalRoleResolver.Resolve(new[] { UnmappedSid }, mappings);

        role.Should().Be(UserRole.Viewer);
    }

    [Fact]
    public void Resolve_SingleMatch_ReturnsMappedRole()
    {
        var mappings = new[]
        {
            new GlobalRoleMapping { GroupSid = OperatorsSid, Role = UserRole.Operator },
        };

        var role = GlobalRoleResolver.Resolve(new[] { OperatorsSid }, mappings);

        role.Should().Be(UserRole.Operator);
    }

    [Fact]
    public void Resolve_MultipleMatches_HighestWins()
    {
        // User is in Domain Admins AND in Operators — Admin must win.
        var mappings = new[]
        {
            new GlobalRoleMapping { GroupSid = OperatorsSid, Role = UserRole.Operator },
            new GlobalRoleMapping { GroupSid = DomainAdminsSid, Role = UserRole.Admin },
        };

        var role = GlobalRoleResolver.Resolve(
            new[] { OperatorsSid, DomainAdminsSid },
            mappings);

        role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public void Resolve_MappingOrderIrrelevant_HighestStillWins()
    {
        // Same as above but with reversed mapping order — result must not depend on it.
        var mappings = new[]
        {
            new GlobalRoleMapping { GroupSid = DomainAdminsSid, Role = UserRole.Admin },
            new GlobalRoleMapping { GroupSid = OperatorsSid, Role = UserRole.Operator },
        };

        var role = GlobalRoleResolver.Resolve(
            new[] { OperatorsSid, DomainAdminsSid },
            mappings);

        role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public void Resolve_LowercaseSid_StillMatches()
    {
        // Operators sometimes paste SIDs lowercase — comparison must be case-insensitive.
        var mappings = new[]
        {
            new GlobalRoleMapping { GroupSid = DomainAdminsSid, Role = UserRole.Admin },
        };

        var role = GlobalRoleResolver.Resolve(
            new[] { DomainAdminsSid.ToLowerInvariant() },
            mappings);

        role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public void Resolve_LowercaseMapping_StillMatches()
    {
        var mappings = new[]
        {
            new GlobalRoleMapping { GroupSid = DomainAdminsSid.ToLowerInvariant(), Role = UserRole.Admin },
        };

        var role = GlobalRoleResolver.Resolve(new[] { DomainAdminsSid }, mappings);

        role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public void Resolve_BlankMappingSid_Skipped()
    {
        // A misconfigured row (empty SID) must not throw or accidentally match an empty
        // user-group entry.
        var mappings = new[]
        {
            new GlobalRoleMapping { GroupSid = "", Role = UserRole.Admin },
            new GlobalRoleMapping { GroupSid = "   ", Role = UserRole.Admin },
            new GlobalRoleMapping { GroupSid = OperatorsSid, Role = UserRole.Operator },
        };

        var role = GlobalRoleResolver.Resolve(new[] { OperatorsSid }, mappings);

        role.Should().Be(UserRole.Operator);
    }

    [Fact]
    public void Resolve_ViewerExplicitMapping_IsRespectedButNotPromoted()
    {
        // An explicit Viewer mapping is still just Viewer — no implicit promotion.
        var mappings = new[]
        {
            new GlobalRoleMapping { GroupSid = ViewersSid, Role = UserRole.Viewer },
        };

        var role = GlobalRoleResolver.Resolve(new[] { ViewersSid }, mappings);

        role.Should().Be(UserRole.Viewer);
    }

    [Fact]
    public void Resolve_NullUserGroups_Throws()
    {
        var mappings = new[]
        {
            new GlobalRoleMapping { GroupSid = OperatorsSid, Role = UserRole.Operator },
        };

        Action act = () => GlobalRoleResolver.Resolve(null!, mappings);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Resolve_NullMappings_Throws()
    {
        Action act = () => GlobalRoleResolver.Resolve(new[] { OperatorsSid }, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
