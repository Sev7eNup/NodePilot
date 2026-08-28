using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
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

        // Root is depth 0; depth 5 is the max, so a 6th level is rejected.
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
    public async Task DeleteRecursive_Subtree_RemovesFoldersAndVariables()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var varStore = NewVarStore(db);
        var a = await store.CreateAsync(Root, "A", null, CancellationToken.None);
        var child = await store.CreateAsync(a.Id, "Child", null, CancellationToken.None);
        var sibling = await store.CreateAsync(Root, "Keep", null, CancellationToken.None);
        await varStore.CreateAsync("IN_A", "v", false, null, a.Id, "t", CancellationToken.None);
        await varStore.CreateAsync("IN_CHILD", "v", false, null, child.Id, "t", CancellationToken.None);
        var survivor = await varStore.CreateAsync("OUTSIDE", "v", false, null, sibling.Id, "t", CancellationToken.None);

        var result = await store.DeleteRecursiveAsync(a.Id, CancellationToken.None);

        result.Folders.Select(f => f.Id).Should().BeEquivalentTo([a.Id, child.Id]);
        result.Variables.Select(v => v.Name).Should().BeEquivalentTo(["IN_A", "IN_CHILD"]);
        (await store.ExistsAsync(a.Id, CancellationToken.None)).Should().BeFalse();
        (await store.ExistsAsync(child.Id, CancellationToken.None)).Should().BeFalse();
        (await store.ExistsAsync(sibling.Id, CancellationToken.None)).Should().BeTrue();
        db.GlobalVariables.Select(v => v.Id).Should().BeEquivalentTo([survivor.Id]);
    }

    [Fact]
    public async Task DeleteRecursive_EmptyFolder_Succeeds()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var f = await store.CreateAsync(Root, "Empty", null, CancellationToken.None);

        var result = await store.DeleteRecursiveAsync(f.Id, CancellationToken.None);

        result.Folders.Should().ContainSingle();
        result.Variables.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteRecursive_ResultCarriesNoVariableValues()
    {
        // The result feeds the audit trail. A global's value is a secret and must not ride along
        // on a delete path — the record type only exposes Id and Name, pinned here so a later
        // "just add the value, it's handy" cannot pass unnoticed.
        typeof(DeletedGlobalVariable).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(["Id", "Name"]);
    }

    [Fact]
    public async Task DeleteRecursive_Root_Throws()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var act = async () => await store.DeleteRecursiveAsync(Root, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeleteRecursive_UnknownFolder_Throws404()
    {
        using var db = TestDbFactory.Create();
        var store = NewStore(db);
        var act = async () => await store.DeleteRecursiveAsync(Guid.NewGuid(), CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    /// <summary>
    /// Writes a variable into <paramref name="folderId"/> right before the subtree DELETE runs —
    /// the window a concurrent writer would use.
    ///
    /// Hooked on the DELETE, not on the snapshot SELECT: an interceptor fires before the command
    /// it wraps, so inserting at the SELECT would put the row into the snapshot instead.
    /// </summary>
    private sealed class InsertVariableBeforeDelete(SqliteConnection conn, Guid folderId) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired
                && command.Transaction is not null
                && command.CommandText.Contains("DELETE FROM \"GlobalVariables\"", StringComparison.Ordinal))
            {
                Fired = true;
                // Written through EF on a second context so the column set comes from the model
                // rather than a hand-written INSERT that drifts with the schema.
                var options = new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(conn).Options;
                await using var writer = new NodePilotDbContext(options);
                await writer.Database.UseTransactionAsync(command.Transaction, cancellationToken);
                writer.GlobalVariables.Add(new GlobalVariable
                {
                    Id = Guid.NewGuid(), Name = "LATECOMER", Value = "v", IsSecret = false,
                    FolderId = folderId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
                await writer.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task DeleteRecursive_VariableAppearsBetweenSnapshotAndDelete_Throws409_AndKeepsEverything()
    {
        // The window the snapshot-inside-the-transaction contract closes: a variable that lands in
        // the subtree after the snapshot must not be swept up silently — it would vanish without an
        // audit row and understate the reported count. It is refused instead, and nothing goes.
        var (conn, db) = TestDbFactory.CreateWithConnection();
        using (conn)
        using (db)
        {
            var seedStore = NewStore(db);
            var varStore = NewVarStore(db);
            var a = await seedStore.CreateAsync(Root, "A", null, CancellationToken.None);
            await varStore.CreateAsync("KNOWN", "v", false, null, a.Id, "t", CancellationToken.None);

            var interceptor = new InsertVariableBeforeDelete(conn, a.Id);
            var options = new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseSqlite(conn).AddInterceptors(interceptor).Options;
            await using var raced = new NodePilotDbContext(options);

            var act = async () => await new GlobalVariableFolderStore(raced)
                .DeleteRecursiveAsync(a.Id, CancellationToken.None);

            await act.Should().ThrowAsync<GlobalVariableFolderConflictException>();
            interceptor.Fired.Should().BeTrue(
                "the interceptor must fire inside the transaction, otherwise the test proves nothing");

            db.ChangeTracker.Clear();
            // The interceptor writes inside the store's own transaction, so its row disappears on
            // rollback and can't be asserted directly. What matters is pinned either way: the run
            // is refused as a race instead of silently sweeping up a row it never snapshotted.
            db.GlobalVariableFolders.Any(f => f.Id == a.Id).Should().BeTrue();
            db.GlobalVariables.Count(v => v.FolderId == a.Id).Should().Be(1);
        }
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
