using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NodePilot.Api.Dtos;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Covers <c>GET /api/workflows/names</c>. It exists so the executions filter stops pulling the
/// full workflow list for two columns, but it is still a permission-bearing read: it must show
/// exactly the workflows the caller may read, no more.
/// </summary>
public sealed class WorkflowNamesEndpointTests
{
    private static Workflow MakeWorkflow(string name, Guid folderId)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            DefinitionJson = "{}",
            UpdatedAt = DateTime.UtcNow,
            FolderId = folderId,
        };

    /// <summary>Adds a second folder next to the seeded root, so scoping has two sides.</summary>
    private static Guid AddFolder(NodePilot.Data.NodePilotDbContext db, string name)
    {
        var folder = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(),
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = name,
            Path = "/" + name,
            Depth = 1,
        };
        db.SharedWorkflowFolders.Add(folder);
        return folder.Id;
    }

    [Fact]
    public async Task GetNames_ReturnsIdAndName_OrderedByName()
    {
        var db = TestDbFactory.Create();
        db.Workflows.AddRange(
            MakeWorkflow("Zulu", SharedWorkflowFolder.RootFolderId),
            MakeWorkflow("Alpha", SharedWorkflowFolder.RootFolderId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await WorkflowControllerHarnessFactory.Build(db).Workflows
            .GetNames(TestContext.Current.CancellationToken);

        var names = result.Result.As<OkObjectResult>().Value.As<List<WorkflowNameItem>>();
        names.Select(n => n.Name).Should().ContainInOrder("Alpha", "Zulu");
        names.Should().OnlyContain(n => n.Id != Guid.Empty);
    }

    [Fact]
    public async Task GetNames_OmitsWorkflowsInFoldersTheCallerCannotRead()
    {
        var db = TestDbFactory.Create();
        var readable = AddFolder(db, "readable");
        var hidden = AddFolder(db, "hidden");
        db.Workflows.AddRange(MakeWorkflow("Visible", readable), MakeWorkflow("Secret", hidden));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var authz = new Mock<IResourceAuthorizationService>();
        authz.Setup(a => a.GetAccessibleFolderIdsAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessibleFolderSet { IsUnrestricted = false, FolderIds = [readable] });

        var result = await WorkflowControllerHarnessFactory.Build(db, authz: authz.Object).Workflows
            .GetNames(TestContext.Current.CancellationToken);

        var names = result.Result.As<OkObjectResult>().Value.As<List<WorkflowNameItem>>();
        names.Select(n => n.Name).Should().ContainSingle().Which.Should().Be("Visible");
    }

    [Fact]
    public async Task GetNames_CallerWithNoFolderAccess_GetsEmptyList()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(MakeWorkflow("Secret", SharedWorkflowFolder.RootFolderId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var authz = new Mock<IResourceAuthorizationService>();
        authz.Setup(a => a.GetAccessibleFolderIdsAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccessibleFolderSet.None);

        var result = await WorkflowControllerHarnessFactory.Build(db, authz: authz.Object).Workflows
            .GetNames(TestContext.Current.CancellationToken);

        result.Result.As<OkObjectResult>().Value.As<List<WorkflowNameItem>>().Should().BeEmpty();
    }
}
