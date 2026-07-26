using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Enums;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security.Oidc;

/// <summary>
/// Group-claim handling in <see cref="OidcIdentityMapper"/>. IdPs disagree wildly on the wire
/// format — Entra sends a JSON array in one claim, Keycloak sends repeated claims, and both can
/// signal "too many groups to inline" instead of the list. A snapshot that is silently
/// incomplete must never be treated as authoritative, because that would revoke folder access
/// the user actually still has.
/// </summary>
public sealed class OidcGroupClaimTests : IDisposable
{
    private const string Issuer = "https://idp.example.test/tenant";
    private const string AllowedGroup = "nodepilot-users";

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly NodePilotDbContext _db;

    public OidcGroupClaimTests()
    {
        (_connection, _db) = TestDbFactory.CreateWithConnection();
        // Break-glass admin so the last-admin guards never interfere with provisioning.
        _db.Users.Add(new NodePilot.Core.Models.User
        {
            Id = Guid.NewGuid(),
            Username = "recovery-admin",
            Provider = AuthProvider.Local,
            PasswordHash = "hash",
            Role = UserRole.Admin,
            IsActive = true,
            IsBreakGlass = true,
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ---------------------------------------------------------------- claim shapes

    [Fact]
    public async Task MapAsync_RepeatedGroupClaims_AreAllCollected()
    {
        var principal = Principal("subject-1", "alice@example.test", groupClaims:
            [AllowedGroup, "nodepilot-admins"]);

        var result = await Mapper().MapAsync(principal, TestContext.Current.CancellationToken);

        result.User.Should().NotBeNull();
        (await MembershipsAsync(result.User!.Id)).Should().BeEquivalentTo([AllowedGroup, "nodepilot-admins"]);
    }

    [Fact]
    public async Task MapAsync_JsonArrayGroupClaim_IsExpanded()
    {
        // Entra ID sends the whole membership list as a single JSON-array claim.
        var principal = Principal("subject-1", "alice@example.test", groupClaims:
            [$"[\"{AllowedGroup}\",\"nodepilot-admins\"]"]);

        var result = await Mapper().MapAsync(principal, TestContext.Current.CancellationToken);

        result.User.Should().NotBeNull();
        (await MembershipsAsync(result.User!.Id)).Should().BeEquivalentTo([AllowedGroup, "nodepilot-admins"]);
    }

    [Fact]
    public async Task MapAsync_JsonArrayWithAnInvalidEntry_IsNotAcceptedAsAuthoritative()
    {
        // One unusable entry makes the whole snapshot incomplete. Provisioning off a partial
        // membership list would hand out access the IdP never actually confirmed.
        var principal = Principal("subject-1", "alice@example.test", groupClaims:
            [$"[\"{AllowedGroup}\",\"\"]"]);

        var result = await Mapper().MapAsync(principal, TestContext.Current.CancellationToken);

        result.User.Should().BeNull();
    }

    [Fact]
    public async Task MapAsync_MalformedJsonGroupClaim_DeniesButLeavesTheStoredMembershipIntact()
    {
        // A broken claim must neither read as "user is in no groups" (that would silently
        // strip the stored membership) nor be trusted enough to mint a session.
        var userId = await SeedProvisionedUserAsync("subject-1", "alice@example.test", AllowedGroup);
        var principal = Principal("subject-1", "alice@example.test", groupClaims: ["[\"unterminated"]);

        var result = await Mapper().MapAsync(principal, TestContext.Current.CancellationToken);

        result.User.Should().BeNull("an unparseable membership snapshot is not authoritative");
        (await MembershipsAsync(userId)).Should().Contain(AllowedGroup,
            "the stored membership survives — a malformed claim must not revoke access");
    }

    [Fact]
    public async Task MapAsync_MixedRepeatedAndJsonArrayClaims_AreMerged()
    {
        var principal = Principal("subject-1", "alice@example.test", groupClaims:
            [AllowedGroup, "[\"nodepilot-admins\"]"]);

        var result = await Mapper().MapAsync(principal, TestContext.Current.CancellationToken);

        (await MembershipsAsync(result.User!.Id)).Should().BeEquivalentTo([AllowedGroup, "nodepilot-admins"]);
    }

    [Fact]
    public async Task MapAsync_WithoutAnyGroupClaim_IsDeniedWhenAGroupIsRequired()
    {
        var principal = Principal("subject-1", "alice@example.test");

        var result = await Mapper().MapAsync(principal, TestContext.Current.CancellationToken);

        result.User.Should().BeNull("no group claim at all means the allow-list cannot be satisfied");
    }

    // ---------------------------------------------------------------- overage signal

    [Fact]
    public void HasGroupOverageSignal_ClaimNamesTheConfiguredGroupClaim_IsDetected()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("_claim_names", "{\"groups\":\"src1\"}")], "oidc"));

        OidcIdentityMapper.HasGroupOverageSignal(principal, "groups").Should().BeTrue();
    }

    [Fact]
    public void HasGroupOverageSignal_FallsBackToTheStandardGroupsKey()
    {
        // Configured claim type differs, but the IdP still signals overage under "groups".
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("_claim_names", "{\"groups\":\"src1\"}")], "oidc"));

        OidcIdentityMapper.HasGroupOverageSignal(principal, "roles").Should().BeTrue();
    }

    [Fact]
    public void HasGroupOverageSignal_UnrelatedClaimNames_AreNotAnOverage()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("_claim_names", "{\"other\":\"src1\"}")], "oidc"));

        OidcIdentityMapper.HasGroupOverageSignal(principal, "groups").Should().BeFalse();
    }

    [Fact]
    public void HasGroupOverageSignal_MalformedMetadata_IsNotTrusted()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("_claim_names", "{not json")], "oidc"));

        OidcIdentityMapper.HasGroupOverageSignal(principal, "groups").Should().BeFalse();
    }

    [Fact]
    public void HasGroupOverageSignal_NonObjectMetadata_IsIgnored()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("_claim_names", "[\"groups\"]")], "oidc"));

        OidcIdentityMapper.HasGroupOverageSignal(principal, "groups").Should().BeFalse();
    }

    [Fact]
    public void HasGroupOverageSignal_WithoutTheMetadataClaim_IsFalse()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "x")], "oidc"));

        OidcIdentityMapper.HasGroupOverageSignal(principal, "groups").Should().BeFalse();
    }

    // ---------------------------------------------------------------- group id validation

    [Theory]
    [InlineData("nodepilot-users", true)]
    [InlineData("S-1-5-21-1000", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsValidGroupId_RejectsBlankIdentifiers(string value, bool expected)
    {
        OidcIdentityMapper.IsValidGroupId(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://idp.example.test/tenant", true)]
    [InlineData("http://idp.example.test", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    public void IsValidIssuer_RequiresAnAbsoluteHttpUrl(string value, bool expected)
    {
        OidcIdentityMapper.IsValidIssuer(value).Should().Be(expected);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<List<string>> MembershipsAsync(Guid userId)
    {
        _db.ChangeTracker.Clear();
        return await _db.DirectoryMemberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.GroupKey)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedProvisionedUserAsync(string subject, string username, params string[] groups)
    {
        var result = await Mapper().MapAsync(
            Principal(subject, username, groups), TestContext.Current.CancellationToken);
        result.User.Should().NotBeNull();
        _db.ChangeTracker.Clear();
        return result.User!.Id;
    }

    private static ClaimsPrincipal Principal(
        string subject, string username, params string[] groupClaims)
    {
        var claims = new List<Claim>
        {
            new("iss", Issuer),
            new("sub", subject),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new("preferred_username", username),
        };
        claims.AddRange(groupClaims.Select(group => new Claim("groups", group)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc"));
    }

    private OidcIdentityMapper Mapper() => new(
        _db,
        Options.Create(new EnterpriseOidcOptions
        {
            Enabled = true,
            Authority = Issuer,
            ClientId = "nodepilot",
            ClientSecret = "test-secret",
            AllowedGroupIds = [AllowedGroup],
            GlobalRoleMappings =
            [
                new OidcRoleMapping { GroupId = "nodepilot-admins", Role = UserRole.Admin },
            ],
        }),
        NullLogger<OidcIdentityMapper>.Instance);
}
