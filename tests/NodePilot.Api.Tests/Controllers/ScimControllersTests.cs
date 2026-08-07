using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodePilot.Api.Controllers;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Api.Security.Scim;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using NodePilot.Api.Tests.TestSupport;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// SCIM HTTP surface: status-code and content-type mapping in <see cref="ScimControllerBase"/>,
/// the Location header on create, 204 on delete, and the admin tombstone/reactivate pair.
/// The provisioning semantics themselves are covered in Security/Scim.
/// </summary>
public sealed class ScimControllersTests : IDisposable
{
    private const string Authority = "https://idp.example.test/tenant";

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly NodePilotDbContext _db;

    public ScimControllersTests()
    {
        (_connection, _db) = TestDbFactory.CreateWithConnection();
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

    // ---------------------------------------------------------------- Users

    [Fact]
    public async Task Create_ReturnsScimJsonWithLocationHeader()
    {
        var controller = Users();

        var result = await controller.Create(
            new ScimUserWriteRequest { ExternalId = "subject-1", UserName = "alice@example.test", Active = true },
            TestContext.Current.CancellationToken);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(201);
        objectResult.ContentTypes.Should().Contain("application/scim+json");
        controller.Response.Headers.Location.ToString().Should().NotBeEmpty(
            "SCIM 201 responses must carry the resource Location");
    }

    [Fact]
    public async Task Get_UnknownUser_ReturnsScimErrorPayload()
    {
        var result = await Users().Get(Guid.NewGuid(), TestContext.Current.CancellationToken);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(404);
        objectResult.ContentTypes.Should().Contain("application/scim+json");
        var error = objectResult.Value.Should().BeOfType<ScimError>().Subject;
        error.Status.Should().Be("404");
        error.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Get_ProvisionedUser_Returns200WithResource()
    {
        var id = await CreateUserAsync("subject-1", "alice@example.test");

        var result = await Users().Get(id, TestContext.Current.CancellationToken);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(200);
        objectResult.Value.Should().BeOfType<ScimUserResource>()
            .Which.UserName.Should().Be("alice@example.test");
    }

    [Fact]
    public async Task List_UsesTheRequestOriginForResourceLocations()
    {
        await CreateUserAsync("subject-1", "alice@example.test");
        var controller = Users(scheme: "https", host: "scim.nodepilot.test");

        var result = await controller.List(ct: TestContext.Current.CancellationToken);

        var list = result.Should().BeOfType<ObjectResult>().Subject
            .Value.Should().BeOfType<ScimListResponse<ScimUserResource>>().Subject;
        list.TotalResults.Should().Be(1);
        list.Resources.Single().Meta.Location
            .Should().StartWith("https://scim.nodepilot.test/api/scim/v2");
    }

    [Fact]
    public async Task List_InvalidFilter_Returns400()
    {
        var result = await Users().List(
            filter: "displayName eq \"x\"", ct: TestContext.Current.CancellationToken);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Replace_ReturnsUpdatedResource()
    {
        var id = await CreateUserAsync("subject-1", "alice@example.test");

        var result = await Users().Replace(
            id,
            new ScimUserWriteRequest { UserName = "alice.renamed@example.test", Active = true },
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<ObjectResult>()
            .Which.Value.Should().BeOfType<ScimUserResource>()
            .Which.UserName.Should().Be("alice.renamed@example.test");
    }

    [Fact]
    public async Task Patch_ReturnsUpdatedResource()
    {
        var id = await CreateUserAsync("subject-1", "alice@example.test");

        var result = await Users().Patch(
            id,
            new ScimPatchRequest
            {
                Schemas = [ScimSchemas.Patch],
                Operations =
                [
                    new ScimPatchOperation { Op = "replace", Path = "active", Value = El("false") },
                ],
            },
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<ObjectResult>()
            .Which.Value.Should().BeOfType<ScimUserResource>()
            .Which.Active.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_ExistingUser_Returns204()
    {
        var id = await CreateUserAsync("subject-1", "alice@example.test");

        var result = await Users().Delete(id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Delete_UnknownUser_Returns204Idempotently()
    {
        var result = await Users().Delete(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<NoContentResult>();
    }

    // ---------------------------------------------------------------- Groups

    [Fact]
    public async Task CreateGroup_ReturnsScimJsonWithLocationHeader()
    {
        var controller = Groups();

        var result = await controller.Create(
            new ScimGroupWriteRequest { ExternalId = "group-1", DisplayName = "NodePilot Users" },
            TestContext.Current.CancellationToken);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(201);
        controller.Response.Headers.Location.ToString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetGroup_Unknown_Returns404ScimError()
    {
        var result = await Groups().Get(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<ObjectResult>().Which.Value
            .Should().BeOfType<ScimError>().Which.Status.Should().Be("404");
    }

    [Fact]
    public async Task ListGroups_ReturnsProvisionedGroups()
    {
        await CreateGroupAsync("group-1", "NodePilot Users");

        var result = await Groups().List(ct: TestContext.Current.CancellationToken);

        result.Should().BeOfType<ObjectResult>().Which.Value
            .Should().BeOfType<ScimListResponse<ScimGroupResource>>()
            .Which.TotalResults.Should().Be(1);
    }

    [Fact]
    public async Task ReplaceGroup_RenamesGroup()
    {
        var id = await CreateGroupAsync("group-1", "NodePilot Users");

        var result = await Groups().Replace(
            id,
            new ScimGroupWriteRequest { DisplayName = "NodePilot Operators" },
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<ObjectResult>().Which.Value
            .Should().BeOfType<ScimGroupResource>()
            .Which.DisplayName.Should().Be("NodePilot Operators");
    }

    [Fact]
    public async Task PatchGroup_RenamesGroup()
    {
        var id = await CreateGroupAsync("group-1", "NodePilot Users");

        var result = await Groups().Patch(
            id,
            new ScimPatchRequest
            {
                Schemas = [ScimSchemas.Patch],
                Operations =
                [
                    new ScimPatchOperation
                    {
                        Op = "replace", Path = "displayName", Value = El("\"Renamed\""),
                    },
                ],
            },
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<ObjectResult>().Which.Value
            .Should().BeOfType<ScimGroupResource>()
            .Which.DisplayName.Should().Be("Renamed");
    }

    [Fact]
    public async Task DeleteGroup_Returns204()
    {
        var id = await CreateGroupAsync("group-1", "NodePilot Users");

        var result = await Groups().Delete(id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<NoContentResult>();
    }

    // ---------------------------------------------------------------- Admin tombstones

    [Fact]
    public async Task ListTombstones_ReturnsOnlyTombstonedGroupsOfTheConfiguredAuthority()
    {
        var live = await CreateGroupAsync("group-1", "Live Group");
        var dead = await CreateGroupAsync("group-2", "Dead Group");
        await Groups().Delete(dead, TestContext.Current.CancellationToken);
        _db.ScimGroups.Add(new ScimGroup
        {
            Id = Guid.NewGuid(),
            Authority = "https://other.example.test",
            ExternalId = "foreign",
            DisplayName = "Foreign Group",
            IsTombstoned = true,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await AdminGroups().ListTombstones(TestContext.Current.CancellationToken);

        var payload = result.Should().BeOfType<OkObjectResult>().Subject.Value;
        var json = JsonSerializer.Serialize(payload);
        json.Should().Contain("Dead Group");
        json.Should().NotContain("Live Group");
        json.Should().NotContain("Foreign Group", "the tombstone list is scoped to the configured authority");
        live.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Reactivate_UnknownGroup_Returns404()
    {
        var result = await AdminGroups().Reactivate(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Reactivate_LiveGroup_Returns409()
    {
        var id = await CreateGroupAsync("group-1", "NodePilot Users");

        var result = await AdminGroups().Reactivate(id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Reactivate_TombstonedGroup_RestoresItAndWritesAudit()
    {
        var id = await CreateGroupAsync("group-1", "NodePilot Users");
        await Groups().Delete(id, TestContext.Current.CancellationToken);
        var audit = new CapturingAuditWriter();

        var result = await AdminGroups(audit).Reactivate(id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<NoContentResult>();
        _db.ChangeTracker.Clear();
        var group = await _db.ScimGroups.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        group.IsTombstoned.Should().BeFalse();
        group.IsActive.Should().BeTrue();
        audit.Calls.Should().ContainSingle().Which.Action.Should().Be(AuditActions.ScimGroupReactivated);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Guid> CreateUserAsync(string externalId, string userName)
    {
        var created = await Provisioning().CreateUserAsync(
            new ScimUserWriteRequest { ExternalId = externalId, UserName = userName, Active = true },
            "https://nodepilot/scim/v2",
            TestContext.Current.CancellationToken);
        created.Succeeded.Should().BeTrue();
        return Guid.Parse(created.Value!.Id);
    }

    private async Task<Guid> CreateGroupAsync(string externalId, string displayName)
    {
        var created = await Provisioning().CreateGroupAsync(
            new ScimGroupWriteRequest { ExternalId = externalId, DisplayName = displayName },
            "https://nodepilot/scim/v2",
            TestContext.Current.CancellationToken);
        created.Succeeded.Should().BeTrue();
        return Guid.Parse(created.Value!.Id);
    }

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private ScimUsersController Users(string scheme = "https", string host = "nodepilot.test")
        => WithHttpContext(new ScimUsersController(Provisioning()), scheme, host);

    private ScimGroupsController Groups(string scheme = "https", string host = "nodepilot.test")
        => WithHttpContext(new ScimGroupsController(Provisioning()), scheme, host);

    private AdminScimGroupsController AdminGroups(IAuditWriter? audit = null)
        => WithHttpContext(
            new AdminScimGroupsController(
                _db,
                audit ?? NoopAuditWriter.Instance,
                Options.Create(new EnterpriseOidcOptions { Enabled = true, Authority = Authority })),
            "https",
            "nodepilot.test");

    private static T WithHttpContext<T>(T controller, string scheme, string host)
        where T : ControllerBase
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = scheme;
        httpContext.Request.Host = new HostString(host);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private ScimProvisioningService Provisioning() => new(
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
        }),
        Options.Create(new AuthenticationPolicyOptions { MaxAuthorizationStalenessMinutes = 15 }),
        new AuditStager());
}
