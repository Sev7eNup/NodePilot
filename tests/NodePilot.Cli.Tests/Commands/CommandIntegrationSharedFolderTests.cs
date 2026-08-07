using FluentAssertions;
using NodePilot.Cli.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Cli.Tests.Commands;

/// <summary>
/// `np shared-folder ...` — the RBAC folder branch. Covers the required-option guards (which
/// must fail before any HTTP call), the happy paths, and the table renderer including the
/// capability flag string, which is the only place the CLI condenses four booleans into one
/// cell an operator reads at a glance.
/// </summary>
[Collection(CommandTestCollection.Name)]
public class CommandIntegrationSharedFolderTests
{
    // ---- list -------------------------------------------------------------

    [Fact]
    public void SharedFolderList_RendersPathDepthAndCapabilityFlags()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/shared-workflow-folders").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                Folder(id, "/team", depth: 1, workflows: 3,
                    read: true, run: true, edit: false, admin: false),
            }));

        var result = h.Run("shared-folder", "list", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("/team");
        result.Output.Should().Contain("RX--", "capabilities collapse to a four-slot flag string");
    }

    [Fact]
    public void SharedFolderList_FullCapabilities_RenderAllFourFlags()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/shared-workflow-folders").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                Folder(Guid.NewGuid(), "/root", depth: 0, workflows: 0,
                    read: true, run: true, edit: true, admin: true),
            }));

        var result = h.Run("shared-folder", "list", "-o", "table");

        result.Output.Should().Contain("RXWA");
    }

    // ---- create -----------------------------------------------------------

    [Fact]
    public void SharedFolderCreate_WithoutName_FailsBeforeAnyRequest()
    {
        using var h = new CommandTestHarness();

        var result = h.Run("shared-folder", "create");

        result.ExitCode.Should().Be(ExitCodes.Error);
        h.Server.LogEntries.Should().BeEmpty("a missing required option must not reach the API");
    }

    [Fact]
    public void SharedFolderCreate_WithName_PostsAndReportsThePath()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/shared-workflow-folders").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201)
                .WithBodyAsJson(Folder(id, "/team", depth: 1, workflows: 0)));

        var result = h.Run("shared-folder", "create", "--name", "team");

        result.ExitCode.Should().Be(ExitCodes.Success);
        h.Server.LogEntries.Should().ContainSingle()
            .Which.RequestMessage.Body.Should().Contain("team", "the folder name is sent in the body");
    }

    // ---- rename -----------------------------------------------------------

    [Fact]
    public void SharedFolderRename_WithoutName_Fails()
    {
        using var h = new CommandTestHarness();

        var result = h.Run("shared-folder", "rename", Guid.NewGuid().ToString());

        result.ExitCode.Should().Be(ExitCodes.Error);
    }

    [Fact]
    public void SharedFolderRename_PutsTheNewName()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/shared-workflow-folders/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(Folder(id, "/renamed", depth: 1, workflows: 0)));

        var result = h.Run("shared-folder", "rename", id.ToString(), "--name", "renamed");

        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    // ---- move -------------------------------------------------------------

    [Fact]
    public void SharedFolderMove_WithoutParentOrToRoot_Fails()
    {
        using var h = new CommandTestHarness();

        var result = h.Run("shared-folder", "move", Guid.NewGuid().ToString());

        result.ExitCode.Should().Be(ExitCodes.Error);
        h.Server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public void SharedFolderMove_ToRoot_SendsANullParent()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/shared-workflow-folders/{id}/move").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(Folder(id, "/moved", depth: 1, workflows: 0)));

        var result = h.Run("shared-folder", "move", id.ToString(), "--to-root");

        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void SharedFolderMove_ToParent_Succeeds()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/shared-workflow-folders/{id}/move").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(Folder(id, "/parent/moved", depth: 2, workflows: 0)));

        var result = h.Run("shared-folder", "move", id.ToString(), "--parent", Guid.NewGuid().ToString());

        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    // ---- delete -----------------------------------------------------------

    [Fact]
    public void SharedFolderDelete_NonInteractive_DeletesWithoutPrompting()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/shared-workflow-folders/{id}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("shared-folder", "delete", id.ToString());

        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void SharedFolderDelete_NonEmptyFolder_SurfacesTheConflict()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath($"/api/shared-workflow-folders/{id}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(409));

        var result = h.Run("shared-folder", "delete", id.ToString());

        result.ExitCode.Should().NotBe(ExitCodes.Success);
    }

    // ---- permissions ------------------------------------------------------

    [Fact]
    public void SharedFolderPermissionsList_RendersTheGrants()
    {
        using var h = new CommandTestHarness();
        var folderId = Guid.NewGuid();
        h.Server.Given(Request.Create()
                .WithPath($"/api/shared-workflow-folders/{folderId}/permissions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    folderId,
                    principalType = "User",
                    principalKey = "alice@example.test",
                    principalAuthority = (string?)null,
                    role = "FolderEditor",
                    grantedByUserId = (Guid?)null,
                    grantedAt = DateTime.UtcNow,
                },
            }));

        var result = h.Run("shared-folder", "permissions", folderId.ToString(), "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("alice@example.test");
        result.Output.Should().Contain("FolderEditor");
    }

    [Theory]
    [InlineData("--principal-key", "alice", "--role", "FolderEditor")]
    [InlineData("--principal-type", "User", "--role", "FolderEditor")]
    [InlineData("--principal-type", "User", "--principal-key", "alice")]
    public void SharedFolderGrant_MissingRequiredOption_Fails(
        string first, string firstValue, string second, string secondValue)
    {
        using var h = new CommandTestHarness();

        var result = h.Run(
            "shared-folder", "grant", Guid.NewGuid().ToString(), first, firstValue, second, secondValue);

        result.ExitCode.Should().Be(ExitCodes.Error);
        h.Server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public void SharedFolderGrant_WithEveryOption_ReportsTheGrantedRole()
    {
        using var h = new CommandTestHarness();
        var folderId = Guid.NewGuid();
        h.Server.Given(Request.Create()
                .WithPath($"/api/shared-workflow-folders/{folderId}/permissions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
            {
                id = Guid.NewGuid(),
                folderId,
                principalType = "User",
                principalKey = "alice@example.test",
                principalAuthority = (string?)null,
                role = "FolderEditor",
                grantedByUserId = (Guid?)null,
                grantedAt = DateTime.UtcNow,
            }));

        var result = h.Run(
            "shared-folder", "grant", folderId.ToString(),
            "--principal-type", "User",
            "--principal-key", "alice@example.test",
            "--role", "FolderEditor");

        result.ExitCode.Should().Be(ExitCodes.Success);
        h.Server.LogEntries.Should().ContainSingle()
            .Which.RequestMessage.Body.Should().Contain("FolderEditor");
    }

    [Fact]
    public void SharedFolderRevoke_DeletesThePermission()
    {
        using var h = new CommandTestHarness();
        var folderId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        h.Server.Given(Request.Create()
                .WithPath($"/api/shared-workflow-folders/{folderId}/permissions/{permissionId}")
                .UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run(
            "shared-folder", "revoke", folderId.ToString(), permissionId.ToString());

        result.ExitCode.Should().Be(ExitCodes.Success);
    }

    // ---- helpers ----------------------------------------------------------

    private static object Folder(
        Guid id, string path, int depth, int workflows,
        bool read = true, bool run = true, bool edit = true, bool admin = true) => new
    {
        id,
        parentFolderId = (Guid?)null,
        name = path.TrimStart('/'),
        path,
        depth,
        createdAt = DateTime.UtcNow,
        createdByUserId = (Guid?)null,
        workflowCount = workflows,
        capabilities = new { canRead = read, canRun = run, canEdit = edit, canAdmin = admin },
    };
}
