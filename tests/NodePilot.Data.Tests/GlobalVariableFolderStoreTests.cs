using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// Unit tests for <see cref="GlobalVariableFolderStore"/> — the organizational folder tree for
/// global variables. Exercises the cycle-safe reparent, depth cap, sibling-name uniqueness,
/// materialized path recompute, and the empty-only delete guard, all against an in-memory SQLite
/// DB whose <c>HasData</c> seed provides the singleton Root.
/// </summary>
public class GlobalVariableFolderStoreTests
{
    private static GlobalVariableFolderStore NewStore(NodePilotDbContext db) => new(db);

    private static GlobalVariableStore NewVarStore(NodePilotDbContext db)
        => new(db, new DpapiSecretProtector(System.Security.Cryptography.DataProtectionScope.CurrentUser));

    private static readonly Guid Root = GlobalVariableFolder.RootFolderId;

    [Fact]
    public async Task Create_UnderRoot_SetsDepth1AndPath()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);

        var f = await store.CreateAsync(Root, "Databases", null, CancellationToken.None);

        f.ParentFolderId.Should().Be(Root);
        f.Depth.Should().Be(1);
        f.Path.Should().Be("/Databases");
    }

    [Fact]
    public async Task Create_NullParent_DefaultsToRoot()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);

        var f = await store.CreateAsync(null, "Top", null, CancellationToken.None);

        f.ParentFolderId.Should().Be(Root);
        f.Depth.Should().Be(1);
    }

    [Fact]
    public async Task Create_Nested_ComputesDeepPath()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);

        var env = await store.CreateAsync(Root, "Environment", null, CancellationToken.None);
        var prod = await store.CreateAsync(env.Id, "Prod", null, CancellationToken.None);

        prod.Depth.Should().Be(2);
        prod.Path.Should().Be("/Environment/Prod");
    }

    [Fact]
    public async Task Create_DuplicateSiblingName_Throws409Conflict()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        await store.CreateAsync(Root, "Dup", null, CancellationToken.None);

        var act = async () => await store.CreateAsync(Root, "Dup", null, CancellationToken.None);

        await act.Should().ThrowAsync<GlobalVariableFolderConflictException>();
    }

    [Fact]
    public async Task Create_SameNameDifferentParents_Allowed()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var a = await store.CreateAsync(Root, "A", null, CancellationToken.None);
        var b = await store.CreateAsync(Root, "B", null, CancellationToken.None);

        // "Shared" under both A and B — sibling uniqueness is per-parent, so both succeed.
        await store.CreateAsync(a.Id, "Shared", null, CancellationToken.None);
        var act = async () => await store.CreateAsync(b.Id, "Shared", null, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Create_BeyondMaxDepth_ThrowsBadRequest()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);

        // Root(0) → 1 → 2 → 3 → 4 → 5 is the max; a 6th level is rejected.
        var parentId = Root;
        for (var i = 1; i <= GlobalVariableFolder.MaxDepth; i++)
            parentId = (await store.CreateAsync(parentId, $"L{i}", null, CancellationToken.None)).Id;

        var act = async () => await store.CreateAsync(parentId, "TooDeep", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Rename_RecomputesDescendantPaths()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var env = await store.CreateAsync(Root, "Environment", null, CancellationToken.None);
        var prod = await store.CreateAsync(env.Id, "Prod", null, CancellationToken.None);

        await store.RenameAsync(env.Id, "Umgebung", CancellationToken.None);

        var all = await store.GetAllAsync(CancellationToken.None);
        all.Single(x => x.Folder.Id == env.Id).Folder.Path.Should().Be("/Umgebung");
        all.Single(x => x.Folder.Id == prod.Id).Folder.Path.Should().Be("/Umgebung/Prod");
    }

    [Fact]
    public async Task Rename_Root_Throws()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var act = async () => await store.RenameAsync(Root, "NewRoot", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Move_IntoOwnDescendant_ThrowsCycle()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var a = await store.CreateAsync(Root, "A", null, CancellationToken.None);
        var b = await store.CreateAsync(a.Id, "B", null, CancellationToken.None);

        var act = async () => await store.MoveAsync(a.Id, b.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Move_Reparents_AndRecomputesPaths()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var a = await store.CreateAsync(Root, "A", null, CancellationToken.None);
        var b = await store.CreateAsync(Root, "B", null, CancellationToken.None);
        var child = await store.CreateAsync(a.Id, "Child", null, CancellationToken.None);

        await store.MoveAsync(child.Id, b.Id, CancellationToken.None);

        var all = await store.GetAllAsync(CancellationToken.None);
        var moved = all.Single(x => x.Folder.Id == child.Id).Folder;
        moved.ParentFolderId.Should().Be(b.Id);
        moved.Path.Should().Be("/B/Child");
        moved.Depth.Should().Be(2);
    }

    [Fact]
    public async Task Delete_EmptyFolder_Succeeds()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var f = await store.CreateAsync(Root, "Temp", null, CancellationToken.None);

        await store.DeleteAsync(f.Id, CancellationToken.None);

        (await store.ExistsAsync(f.Id, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_FolderWithChild_Throws409()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var a = await store.CreateAsync(Root, "A", null, CancellationToken.None);
        await store.CreateAsync(a.Id, "Child", null, CancellationToken.None);

        var act = async () => await store.DeleteAsync(a.Id, CancellationToken.None);

        await act.Should().ThrowAsync<GlobalVariableFolderConflictException>();
    }

    [Fact]
    public async Task Delete_FolderWithVariable_Throws409()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var varStore = NewVarStore(db);
        var f = await store.CreateAsync(Root, "HasVar", null, CancellationToken.None);
        await varStore.CreateAsync("X", "v", false, null, f.Id, "t", CancellationToken.None);

        var act = async () => await store.DeleteAsync(f.Id, CancellationToken.None);

        await act.Should().ThrowAsync<GlobalVariableFolderConflictException>();
    }

    [Fact]
    public async Task Delete_Root_Throws()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var act = async () => await store.DeleteAsync(Root, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetAll_ReturnsRootPlusFoldersWithVariableCounts()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var varStore = NewVarStore(db);
        var f = await store.CreateAsync(Root, "Counted", null, CancellationToken.None);
        await varStore.CreateAsync("A", "1", false, null, f.Id, "t", CancellationToken.None);
        await varStore.CreateAsync("B", "2", false, null, f.Id, "t", CancellationToken.None);

        var all = await store.GetAllAsync(CancellationToken.None);

        all.Should().Contain(x => x.Folder.Id == Root); // seeded root always present
        all.Single(x => x.Folder.Id == f.Id).VariableCount.Should().Be(2);
    }

    [Fact]
    public async Task Variable_InSubfolder_ResolvesByNameRegardlessOfFolder()
    {
        // The whole point: folders are cosmetic. A variable placed deep in the tree resolves by
        // its bare name exactly as one at Root would.
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var varStore = NewVarStore(db);
        var env = await store.CreateAsync(Root, "Environment", null, CancellationToken.None);
        var prod = await store.CreateAsync(env.Id, "Prod", null, CancellationToken.None);
        await varStore.CreateAsync("API_BASE", "https://x", false, null, prod.Id, "t", CancellationToken.None);

        (await varStore.GetValueAsync("API_BASE", CancellationToken.None)).Should().Be("https://x");
        (await varStore.GetAllResolvedAsync(CancellationToken.None))["API_BASE"].Should().Be("https://x");
    }
}
