using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Activities;
using NodePilot.Core.Audit;
using NodePilot.Data;
using NodePilot.TestCommons;
using NodePilot.Api.Tests.TestSupport;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Read + lifecycle endpoints of <see cref="CustomActivitiesController"/>: the palette catalog
/// (which must hide drafts from Viewers), detail/version reads, delete, rollback and the
/// Admin-only enable/disable pair. <see cref="CustomActivitiesControllerTests"/> covers
/// create/update/import and the governance rules around them.
/// </summary>
public sealed class CustomActivitiesLifecycleTests : IDisposable
{
    private readonly NodePilotDbContext _db = TestDbFactory.Create();

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------- catalog

    [Fact]
    public async Task GetCatalog_HidesDraftsFromViewers()
    {
        await CreateAsync("disk_check");

        var viewer = await Controller("Viewer").GetCatalog(includeDisabled: true, TestContext.Current.CancellationToken);

        Entries(viewer).Should().BeEmpty(
            "a Viewer asking for drafts must not see unpublished definitions");
    }

    [Fact]
    public async Task GetCatalog_IncludeDisabled_ShowsDraftsToAuthors()
    {
        await CreateAsync("disk_check");

        var author = await Controller("Operator").GetCatalog(includeDisabled: true, TestContext.Current.CancellationToken);

        Entries(author).Should().ContainSingle().Which.Type.Should().Be("custom:disk_check");
    }

    [Fact]
    public async Task GetCatalog_WithoutIncludeDisabled_OmitsDraftsEvenForAdmins()
    {
        await CreateAsync("disk_check");

        var result = await Controller("Admin").GetCatalog(includeDisabled: false, TestContext.Current.CancellationToken);

        Entries(result).Should().BeEmpty("the palette only shows published entries by default");
    }

    [Fact]
    public async Task GetCatalog_EnabledDefinition_IsVisibleToEveryRole()
    {
        var id = await CreateAsync("disk_check");
        await Controller("Admin").Enable(id, TestContext.Current.CancellationToken);

        var result = await Controller("Viewer").GetCatalog(includeDisabled: false, TestContext.Current.CancellationToken);

        Entries(result).Should().ContainSingle();
    }

    // ---------------------------------------------------------------- detail + versions

    [Fact]
    public async Task Get_UnknownId_ReturnsNotFound()
    {
        var result = await Controller().Get(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Get_ExistingDefinition_ReturnsTheScriptTemplate()
    {
        var id = await CreateAsync("disk_check", script: "Get-PSDrive C");

        var result = await Controller().Get(id, TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<CustomActivityResponse>()
            .Which.ScriptTemplate.Should().Be("Get-PSDrive C");
    }

    [Fact]
    public async Task GetVersions_UnknownId_ReturnsNotFound()
    {
        var result = await Controller().GetVersions(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetVersions_AfterAnUpdate_ListsThePreviousVersion()
    {
        var id = await CreateAsync("disk_check", script: "v1");
        var current = await Detail(id);
        await Controller().Update(
            id,
            new UpdateCustomActivityRequest(
                "Disk Check", null, "extension", null, "v2", "auto", false, false,
                null, null, null, null, null, null, current.ConcurrencyToken, "bump"),
            TestContext.Current.CancellationToken);

        var result = await Controller().GetVersions(id, TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IReadOnlyList<CustomActivityVersionResponse>>()
            .Which.Should().NotBeEmpty();
    }

    // ---------------------------------------------------------------- delete

    [Fact]
    public async Task Delete_UnknownId_ReturnsNotFound()
    {
        var result = await Controller().Delete(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_Draft_IsAllowedForOperatorsAndAudited()
    {
        var id = await CreateAsync("disk_check");
        var (controller, audit) = ControllerWithAudit("Operator");

        var result = await controller.Delete(id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<NoContentResult>();
        audit.Calls.Should().ContainSingle().Which.Action.Should().Be(AuditActions.CustomActivityDeleted);
    }

    [Fact]
    public async Task Delete_EnabledDefinition_IsRefusedForOperators()
    {
        var id = await CreateAsync("disk_check");
        await Controller("Admin").Enable(id, TestContext.Current.CancellationToken);

        var result = await Controller("Operator").Delete(id, TestContext.Current.CancellationToken);

        result.Should().NotBeOfType<NoContentResult>(
            "once published, only an Admin may mutate a custom activity");
    }

    // ---------------------------------------------------------------- rollback

    [Fact]
    public async Task Rollback_UnknownId_ReturnsNotFound()
    {
        var result = await Controller().Rollback(Guid.NewGuid(), 1, TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Rollback_UnknownVersion_ReturnsNotFound()
    {
        var id = await CreateAsync("disk_check");

        var result = await Controller().Rollback(id, 99, TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Rollback_RestoresThePreviousScriptAsANewVersion()
    {
        var id = await CreateAsync("disk_check", script: "v1");
        var current = await Detail(id);
        await Controller().Update(
            id,
            new UpdateCustomActivityRequest(
                "Disk Check", null, "extension", null, "v2", "auto", false, false,
                null, null, null, null, null, null, current.ConcurrencyToken, "bump"),
            TestContext.Current.CancellationToken);
        var (controller, audit) = ControllerWithAudit();

        var result = await controller.Rollback(id, 1, TestContext.Current.CancellationToken);

        var response = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<CustomActivityResponse>().Subject;
        response.ScriptTemplate.Should().Be("v1");
        response.Version.Should().BeGreaterThan(2, "a rollback creates a new version, it never rewrites history");
        audit.Calls.Should().Contain(call => call.Action == AuditActions.CustomActivityRolledBack);
    }

    // ---------------------------------------------------------------- enable / disable

    [Fact]
    public async Task Enable_UnknownId_ReturnsNotFound()
    {
        var result = await Controller().Enable(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Enable_PublishesTheDefinitionAndAudits()
    {
        var id = await CreateAsync("disk_check");
        var (controller, audit) = ControllerWithAudit();

        var result = await controller.Enable(id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<NoContentResult>();
        (await Detail(id)).IsEnabled.Should().BeTrue();
        audit.Calls.Should().ContainSingle().Which.Action.Should().Be(AuditActions.CustomActivityEnabled);
    }

    [Fact]
    public async Task Disable_TakesThePublishedDefinitionBackToDraftAndAudits()
    {
        var id = await CreateAsync("disk_check");
        await Controller().Enable(id, TestContext.Current.CancellationToken);
        var (controller, audit) = ControllerWithAudit();

        var result = await controller.Disable(id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<NoContentResult>();
        (await Detail(id)).IsEnabled.Should().BeFalse();
        audit.Calls.Should().ContainSingle().Which.Action.Should().Be(AuditActions.CustomActivityDisabled);
    }

    [Fact]
    public async Task Disable_UnknownId_ReturnsNotFound()
    {
        var result = await Controller().Disable(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Guid> CreateAsync(string key, string script = "Get-PSDrive C")
    {
        var result = await Controller().Create(
            new CreateCustomActivityRequest(
                key, "Disk Check", null, "extension", null, script, "auto", false, false,
                null, null, null, null, null, null),
            TestContext.Current.CancellationToken);
        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        return created.Value.Should().BeOfType<CustomActivitySaveResponse>().Subject.Definition.Id;
    }

    private async Task<CustomActivityResponse> Detail(Guid id)
    {
        var result = await Controller().Get(id, TestContext.Current.CancellationToken);
        return result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<CustomActivityResponse>().Subject;
    }

    private static IReadOnlyList<CustomActivityCatalogEntry> Entries(
        ActionResult<IReadOnlyList<CustomActivityCatalogEntry>> result) =>
        result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IReadOnlyList<CustomActivityCatalogEntry>>().Subject;

    private CustomActivitiesController Controller(string role = "Admin") => ControllerWithAudit(role).controller;

    private (CustomActivitiesController controller, CapturingAuditWriter audit) ControllerWithAudit(
        string role = "Admin")
    {
        var audit = new CapturingAuditWriter();
        var controller = new CustomActivitiesController(new CustomActivityDefinitionStore(_db), audit)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.Name, "testuser")],
                        "TestAuth")),
                },
            },
        };
        return (controller, audit);
    }
}
