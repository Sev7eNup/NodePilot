using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Api.Security.Scim;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security.Scim;

/// <summary>
/// Read and PATCH surface of <see cref="ScimProvisioningService"/> — the half an IdP actually
/// drives on every sync cycle: GET/LIST with filters and paging, PUT/PATCH semantics, and
/// group membership reconciliation. <see cref="ScimProvisioningServiceTests"/> covers the
/// create/delete lifecycle.
/// </summary>
public sealed class ScimProvisioningReadPatchTests : IDisposable
{
    private const string Authority = "https://idp.example.test/tenant";
    private const string BaseUrl = "https://nodepilot/scim/v2";

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly NodePilotDbContext _db;

    public ScimProvisioningReadPatchTests()
    {
        (_connection, _db) = TestDbFactory.CreateWithConnection();
        // Break-glass admin so the last-admin guard never trips on unrelated assertions.
        _db.Users.Add(new User
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

    // ---------------------------------------------------------------- GetUserAsync

    [Fact]
    public async Task GetUserAsync_UnknownId_Returns404()
    {
        var result = await Service().GetUserAsync(Guid.NewGuid(), BaseUrl, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetUserAsync_ProvisionedUser_ReturnsResource()
    {
        var id = await SeedUserAsync("subject-1", "alice@example.test");

        var result = await Service().GetUserAsync(id, BaseUrl, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Value!.Id.Should().Be(id.ToString("D"));
        result.Value.ExternalId.Should().Be("subject-1");
        result.Value.UserName.Should().Be("alice@example.test");
    }

    [Fact]
    public async Task GetUserAsync_TombstonedUser_Returns404()
    {
        var id = await SeedUserAsync("subject-1", "alice@example.test");
        await Service().DeleteUserAsync(id, TestContext.Current.CancellationToken);

        var result = await Service().GetUserAsync(id, BaseUrl, TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(404);
    }

    // ---------------------------------------------------------------- ListUsersAsync

    [Fact]
    public async Task ListUsersAsync_UnsupportedFilter_Returns400InvalidFilter()
    {
        var result = await Service().ListUsersAsync(
            "displayName eq \"alice\"", 1, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("invalidFilter");
    }

    [Fact]
    public async Task ListUsersAsync_MalformedFilter_Returns400InvalidFilter()
    {
        var result = await Service().ListUsersAsync(
            "userName sw \"ali\"", 1, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("invalidFilter");
    }

    [Fact]
    public async Task ListUsersAsync_UserNameFilter_ReturnsOnlyTheMatch()
    {
        await SeedUserAsync("subject-1", "alice@example.test");
        await SeedUserAsync("subject-2", "bob@example.test");

        var result = await Service().ListUsersAsync(
            "userName eq \"bob@example.test\"", 1, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Value!.TotalResults.Should().Be(1);
        result.Value.Resources.Should().ContainSingle()
            .Which.UserName.Should().Be("bob@example.test");
    }

    [Fact]
    public async Task ListUsersAsync_ExternalIdFilter_ReturnsOnlyTheMatch()
    {
        await SeedUserAsync("subject-1", "alice@example.test");
        await SeedUserAsync("subject-2", "bob@example.test");

        var result = await Service().ListUsersAsync(
            "externalId eq \"subject-1\"", 1, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.Value!.TotalResults.Should().Be(1);
        result.Value.Resources.Single().ExternalId.Should().Be("subject-1");
    }

    [Fact]
    public async Task ListUsersAsync_NoFilter_ExcludesTombstonedUsers()
    {
        await SeedUserAsync("subject-1", "alice@example.test");
        var bob = await SeedUserAsync("subject-2", "bob@example.test");
        await Service().DeleteUserAsync(bob, TestContext.Current.CancellationToken);

        var result = await Service().ListUsersAsync(null, 1, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.Value!.TotalResults.Should().Be(1);
        result.Value.Resources.Single().ExternalId.Should().Be("subject-1");
    }

    [Fact]
    public async Task ListUsersAsync_CountZero_ReportsTotalButReturnsNoResources()
    {
        await SeedUserAsync("subject-1", "alice@example.test");
        await SeedUserAsync("subject-2", "bob@example.test");

        var result = await Service().ListUsersAsync(null, 1, 0, BaseUrl, TestContext.Current.CancellationToken);

        result.Value!.TotalResults.Should().Be(2, "the IdP uses count=0 to probe the result size");
        result.Value.ItemsPerPage.Should().Be(0);
        result.Value.Resources.Should().BeEmpty();
    }

    [Fact]
    public async Task ListUsersAsync_StartIndexBeyondFirstPage_SkipsEarlierEntries()
    {
        await SeedUserAsync("subject-1", "alice@example.test");
        await SeedUserAsync("subject-2", "bob@example.test");
        await SeedUserAsync("subject-3", "carol@example.test");

        var result = await Service().ListUsersAsync(null, 2, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.Value!.StartIndex.Should().Be(2);
        result.Value.TotalResults.Should().Be(3);
        result.Value.Resources.Select(x => x.UserName)
            .Should().Equal("bob@example.test", "carol@example.test");
    }

    [Fact]
    public async Task ListUsersAsync_StartIndexBelowOne_ClampsToFirstPage()
    {
        await SeedUserAsync("subject-1", "alice@example.test");

        var result = await Service().ListUsersAsync(null, 0, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.Value!.StartIndex.Should().Be(1, "SCIM start indices are 1-based");
        result.Value.Resources.Should().ContainSingle();
    }

    [Fact]
    public async Task ListUsersAsync_WithoutConfiguredAuthority_Returns503()
    {
        var result = await ServiceWithoutAuthority().ListUsersAsync(
            null, 1, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(503);
    }

    // ---------------------------------------------------------------- ReplaceUserAsync

    [Fact]
    public async Task ReplaceUserAsync_UnknownId_Returns404()
    {
        var result = await Service().ReplaceUserAsync(
            Guid.NewGuid(),
            new ScimUserWriteRequest { UserName = "alice@example.test", Active = true },
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ReplaceUserAsync_ChangedExternalId_Returns400Mutability()
    {
        var id = await SeedUserAsync("subject-1", "alice@example.test");

        var result = await Service().ReplaceUserAsync(
            id,
            new ScimUserWriteRequest
            {
                ExternalId = "subject-changed", UserName = "alice@example.test", Active = true,
            },
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("mutability");
    }

    [Fact]
    public async Task ReplaceUserAsync_RenameAndDeactivate_PersistsBoth()
    {
        var id = await SeedUserAsync("subject-1", "alice@example.test");

        var result = await Service().ReplaceUserAsync(
            id,
            new ScimUserWriteRequest { UserName = "alice.renamed@example.test", Active = false },
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        _db.ChangeTracker.Clear();
        var user = await _db.Users.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        user.Username.Should().Be("alice.renamed@example.test");
        user.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task ReplaceUserAsync_UserNameAlreadyTaken_Returns409Uniqueness()
    {
        var alice = await SeedUserAsync("subject-1", "alice@example.test");
        await SeedUserAsync("subject-2", "bob@example.test");

        var result = await Service().ReplaceUserAsync(
            alice,
            new ScimUserWriteRequest { UserName = "bob@example.test", Active = true },
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(409);
        result.ScimType.Should().Be("uniqueness");
    }

    [Fact]
    public async Task ReplaceUserAsync_MissingUserName_Returns400InvalidValue()
    {
        var id = await SeedUserAsync("subject-1", "alice@example.test");

        var result = await Service().ReplaceUserAsync(
            id, new ScimUserWriteRequest { Active = true }, BaseUrl, TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("invalidValue");
    }

    // ---------------------------------------------------------------- PatchUserAsync

    [Fact]
    public async Task PatchUserAsync_MissingPatchSchema_Returns400InvalidSyntax()
    {
        var id = await SeedUserAsync("subject-1", "alice@example.test");

        var result = await Service().PatchUserAsync(
            id,
            new ScimPatchRequest { Schemas = ["urn:wrong"], Operations = [Replace("active", "false")] },
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("invalidSyntax");
    }

    [Fact]
    public async Task PatchUserAsync_EmptyOperations_Returns400InvalidSyntax()
    {
        var id = await SeedUserAsync("subject-1", "alice@example.test");

        var result = await Service().PatchUserAsync(
            id, Patch(), BaseUrl, TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("invalidSyntax");
    }

    [Fact]
    public async Task PatchUserAsync_MoreThanTwentyOperations_Returns400InvalidSyntax()
    {
        var id = await SeedUserAsync("subject-1", "alice@example.test");
        var operations = Enumerable.Range(0, 21).Select(_ => Replace("active", "false")).ToList();

        var result = await Service().PatchUserAsync(
            id,
            new ScimPatchRequest { Schemas = [ScimSchemas.Patch], Operations = operations },
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("invalidSyntax");
    }

    [Fact]
    public async Task PatchUserAsync_UnknownId_Returns404()
    {
        var result = await Service().PatchUserAsync(
            Guid.NewGuid(), Patch(Replace("active", "false")), BaseUrl, TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task PatchUserAsync_NonReplaceOperation_Returns400InvalidValue()
    {
        var id = await SeedUserAsync("subject-1", "alice@example.test");

        var result = await Service().PatchUserAsync(
            id,
            Patch(new ScimPatchOperation { Op = "add", Path = "active", Value = El("false") }),
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("invalidValue");
    }

    [Fact]
    public async Task PatchUserAsync_UnsupportedPath_Returns400InvalidPath()
    {
        var id = await SeedUserAsync("subject-1", "alice@example.test");

        var result = await Service().PatchUserAsync(
            id, Patch(Replace("displayName", "\"Alice\"")), BaseUrl, TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("invalidPath");
    }

    [Fact]
    public async Task PatchUserAsync_ActivePath_DeactivatesUser()
    {
        var id = await SeedUserAsync("subject-1", "alice@example.test");

        var result = await Service().PatchUserAsync(
            id, Patch(Replace("active", "false")), BaseUrl, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken))
            .IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task PatchUserAsync_UserNamePath_RenamesUser()
    {
        var id = await SeedUserAsync("subject-1", "alice@example.test");

        var result = await Service().PatchUserAsync(
            id,
            Patch(Replace("userName", "\"alice.new@example.test\"")),
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        _db.ChangeTracker.Clear();
        (await _db.Users.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken))
            .Username.Should().Be("alice.new@example.test");
    }

    [Fact]
    public async Task PatchUserAsync_PathlessObjectValue_AppliesBothAttributes()
    {
        // Entra ID sends replace operations without a path and the whole resource fragment
        // as the value — the shape that broke real-world syncs before it was handled.
        var id = await SeedUserAsync("subject-1", "alice@example.test");

        var result = await Service().PatchUserAsync(
            id,
            Patch(new ScimPatchOperation
            {
                Op = "replace",
                Value = El("""{"active":false,"userName":"alice.combined@example.test"}"""),
            }),
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        _db.ChangeTracker.Clear();
        var user = await _db.Users.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        user.IsActive.Should().BeFalse();
        user.Username.Should().Be("alice.combined@example.test");
    }

    [Fact]
    public async Task PatchUserAsync_RenameToTakenUserName_Returns409Uniqueness()
    {
        var alice = await SeedUserAsync("subject-1", "alice@example.test");
        await SeedUserAsync("subject-2", "bob@example.test");

        var result = await Service().PatchUserAsync(
            alice,
            Patch(Replace("userName", "\"bob@example.test\"")),
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(409);
        result.ScimType.Should().Be("uniqueness");
    }

    // ---------------------------------------------------------------- Groups

    [Fact]
    public async Task GetGroupAsync_UnknownId_Returns404()
    {
        var result = await Service().GetGroupAsync(Guid.NewGuid(), BaseUrl, TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetGroupAsync_ProvisionedGroup_ReturnsResource()
    {
        var groupId = await SeedGroupAsync("group-1", "NodePilot Users");

        var result = await Service().GetGroupAsync(groupId, BaseUrl, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Value!.DisplayName.Should().Be("NodePilot Users");
        result.Value.ExternalId.Should().Be("group-1");
    }

    [Fact]
    public async Task GetGroupAsync_TombstonedGroup_Returns404()
    {
        var groupId = await SeedGroupAsync("group-1", "NodePilot Users");
        await Service().DeleteGroupAsync(groupId, TestContext.Current.CancellationToken);

        var result = await Service().GetGroupAsync(groupId, BaseUrl, TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ListGroupsAsync_UnsupportedFilter_Returns400InvalidFilter()
    {
        var result = await Service().ListGroupsAsync(
            "members eq \"x\"", 1, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("invalidFilter");
    }

    [Fact]
    public async Task ListGroupsAsync_DisplayNameFilter_ReturnsOnlyTheMatch()
    {
        await SeedGroupAsync("group-1", "NodePilot Users");
        await SeedGroupAsync("group-2", "NodePilot Admins");

        var result = await Service().ListGroupsAsync(
            "displayName eq \"NodePilot Admins\"", 1, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.Value!.TotalResults.Should().Be(1);
        result.Value.Resources.Single().ExternalId.Should().Be("group-2");
    }

    [Fact]
    public async Task ListGroupsAsync_ExternalIdFilter_ReturnsOnlyTheMatch()
    {
        await SeedGroupAsync("group-1", "NodePilot Users");
        await SeedGroupAsync("group-2", "NodePilot Admins");

        var result = await Service().ListGroupsAsync(
            "externalId eq \"group-1\"", 1, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.Value!.Resources.Single().DisplayName.Should().Be("NodePilot Users");
    }

    [Fact]
    public async Task ListGroupsAsync_ExcludesTombstonedGroups()
    {
        await SeedGroupAsync("group-1", "NodePilot Users");
        var admins = await SeedGroupAsync("group-2", "NodePilot Admins");
        await Service().DeleteGroupAsync(admins, TestContext.Current.CancellationToken);

        var result = await Service().ListGroupsAsync(null, 1, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.Value!.TotalResults.Should().Be(1);
        result.Value.Resources.Single().ExternalId.Should().Be("group-1");
    }

    [Fact]
    public async Task ListGroupsAsync_WithoutConfiguredAuthority_Returns503()
    {
        var result = await ServiceWithoutAuthority().ListGroupsAsync(
            null, 1, 10, BaseUrl, TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task PatchGroupAsync_UnknownId_Returns404()
    {
        var result = await Service().PatchGroupAsync(
            Guid.NewGuid(),
            Patch(Replace("displayName", "\"Renamed\"")),
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task PatchGroupAsync_MissingPatchSchema_Returns400InvalidSyntax()
    {
        var groupId = await SeedGroupAsync("group-1", "NodePilot Users");

        var result = await Service().PatchGroupAsync(
            groupId,
            new ScimPatchRequest { Schemas = ["urn:wrong"], Operations = [Replace("displayName", "\"x\"")] },
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("invalidSyntax");
    }

    [Fact]
    public async Task PatchGroupAsync_ReplaceDisplayName_RenamesGroup()
    {
        var groupId = await SeedGroupAsync("group-1", "NodePilot Users");

        var result = await Service().PatchGroupAsync(
            groupId,
            Patch(Replace("displayName", "\"NodePilot Operators\"")),
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Value!.DisplayName.Should().Be("NodePilot Operators");
    }

    [Fact]
    public async Task PatchGroupAsync_UnsupportedPath_Returns400InvalidPath()
    {
        var groupId = await SeedGroupAsync("group-1", "NodePilot Users");

        var result = await Service().PatchGroupAsync(
            groupId,
            Patch(Replace("externalId", "\"group-changed\"")),
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("invalidPath");
    }

    [Fact]
    public async Task PatchGroupAsync_AddMember_GrantsMembership()
    {
        var userId = await SeedUserAsync("subject-1", "alice@example.test");
        var groupId = await SeedGroupAsync("group-1", "NodePilot Users");

        var result = await Service().PatchGroupAsync(
            groupId,
            Patch(new ScimPatchOperation
            {
                Op = "add",
                Path = "members",
                Value = El($$"""[{"value":"{{userId:D}}"}]"""),
            }),
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Value!.Members.Select(x => x.Value).Should().Contain(userId.ToString("D"));
    }

    [Fact]
    public async Task PatchGroupAsync_RemoveMemberByValue_DropsMembership()
    {
        var userId = await SeedUserAsync("subject-1", "alice@example.test");
        var groupId = await SeedGroupAsync("group-1", "NodePilot Users", userId);

        var result = await Service().PatchGroupAsync(
            groupId,
            Patch(new ScimPatchOperation
            {
                Op = "remove",
                Path = "members",
                Value = El($$"""[{"value":"{{userId:D}}"}]"""),
            }),
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Value!.Members.Should().BeEmpty();
    }

    [Fact]
    public async Task PatchGroupAsync_RemoveMemberByPathFilter_DropsMembership()
    {
        // Okta removes members with a filtered path and no value payload.
        var userId = await SeedUserAsync("subject-1", "alice@example.test");
        var groupId = await SeedGroupAsync("group-1", "NodePilot Users", userId);

        var result = await Service().PatchGroupAsync(
            groupId,
            Patch(new ScimPatchOperation { Op = "remove", Path = $"members[value eq \"{userId:D}\"]" }),
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Value!.Members.Should().BeEmpty();
    }

    [Fact]
    public async Task PatchGroupAsync_ReplaceMembers_SwapsTheWholeSet()
    {
        var alice = await SeedUserAsync("subject-1", "alice@example.test");
        var bob = await SeedUserAsync("subject-2", "bob@example.test");
        var groupId = await SeedGroupAsync("group-1", "NodePilot Users", alice);

        var result = await Service().PatchGroupAsync(
            groupId,
            Patch(new ScimPatchOperation
            {
                Op = "replace",
                Path = "members",
                Value = El($$"""[{"value":"{{bob:D}}"}]"""),
            }),
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Value!.Members.Select(x => x.Value).Should().Equal(bob.ToString("D"));
    }

    [Fact]
    public async Task PatchGroupAsync_MalformedMembersValue_Returns400InvalidValue()
    {
        var groupId = await SeedGroupAsync("group-1", "NodePilot Users");

        var result = await Service().PatchGroupAsync(
            groupId,
            Patch(new ScimPatchOperation { Op = "add", Path = "members", Value = El("\"not-an-array\"") }),
            BaseUrl,
            TestContext.Current.CancellationToken);

        result.StatusCode.Should().Be(400);
        result.ScimType.Should().Be("invalidValue");
    }

    [Fact]
    public async Task DeleteGroupAsync_UnknownId_IsIdempotent()
    {
        var result = await Service().DeleteGroupAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue("SCIM DELETE is idempotent");
    }

    [Fact]
    public async Task DeleteGroupAsync_TombstonesGroupAndDropsMemberships()
    {
        var userId = await SeedUserAsync("subject-1", "alice@example.test");
        var groupId = await SeedGroupAsync("group-1", "NodePilot Users", userId);

        var result = await Service().DeleteGroupAsync(groupId, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        _db.ChangeTracker.Clear();
        var group = await _db.ScimGroups.SingleAsync(x => x.Id == groupId, TestContext.Current.CancellationToken);
        group.IsTombstoned.Should().BeTrue();
        group.IsActive.Should().BeFalse();
        (await _db.DirectoryMemberships.CountAsync(
            x => x.UserId == userId, TestContext.Current.CancellationToken)).Should().Be(0);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Guid> SeedUserAsync(string externalId, string userName)
    {
        var created = await Service().CreateUserAsync(
            new ScimUserWriteRequest { ExternalId = externalId, UserName = userName, Active = true },
            BaseUrl,
            TestContext.Current.CancellationToken);
        created.Succeeded.Should().BeTrue();
        return Guid.Parse(created.Value!.Id);
    }

    private async Task<Guid> SeedGroupAsync(string externalId, string displayName, params Guid[] members)
    {
        var created = await Service().CreateGroupAsync(
            new ScimGroupWriteRequest
            {
                ExternalId = externalId,
                DisplayName = displayName,
                Members = members.Select(x => new ScimMember { Value = x.ToString("D") }).ToList(),
            },
            BaseUrl,
            TestContext.Current.CancellationToken);
        created.Succeeded.Should().BeTrue();
        return Guid.Parse(created.Value!.Id);
    }

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ScimPatchOperation Replace(string path, string valueJson) =>
        new() { Op = "replace", Path = path, Value = El(valueJson) };

    private static ScimPatchRequest Patch(params ScimPatchOperation[] operations) =>
        new() { Schemas = [ScimSchemas.Patch], Operations = [.. operations] };

    private ScimProvisioningService Service() => new(
        _db,
        Options.Create(new ScimOptions
        {
            Enabled = true,
            BearerToken = new string('s', 32),
            Authority = Authority,
        }),
        Options.Create(new EnterpriseOidcOptions
        {
            Enabled = true,
            Authority = Authority,
            AllowedGroupIds = ["nodepilot-users"],
            GlobalRoleMappings =
            [
                new OidcRoleMapping { GroupId = "nodepilot-admins", Role = UserRole.Admin },
            ],
        }),
        Options.Create(new AuthenticationPolicyOptions { MaxAuthorizationStalenessMinutes = 15 }),
        new AuditStager());

    /// <summary>
    /// GetAuthority() only trusts an authority that is a valid issuer AND matches the OIDC
    /// authority — a mismatch is how a half-configured deployment presents itself.
    /// </summary>
    private ScimProvisioningService ServiceWithoutAuthority() => new(
        _db,
        Options.Create(new ScimOptions
        {
            Enabled = true,
            BearerToken = new string('s', 32),
            Authority = "https://scim.example.test/other",
        }),
        Options.Create(new EnterpriseOidcOptions { Enabled = true, Authority = Authority }),
        Options.Create(new AuthenticationPolicyOptions { MaxAuthorizationStalenessMinutes = 15 }),
        new AuditStager());
}
