using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodePilot.Core.Interfaces;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

public class CustomActivityDefinitionStoreTests
{
    [Fact]
    public async Task Create_ConcurrentLiveKeyInsert_RejectsTheLosingDefinition()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, initial) = TestDbFactory.CreateWithConnection();
        await using var ownedConnection = connection;
        await using var winnerDb = initial;
        var winner = new CustomActivityDefinitionStore(winnerDb);
        var barrier = new BeforeSave(async () => { await winner.CreateAsync(Input(), "winner", ct); });
        await using var loserDb = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(barrier).Options);
        var loser = new CustomActivityDefinitionStore(loserDb);

        var create = () => loser.CreateAsync(Input(), "loser", ct);

        await create.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
        (await winner.GetAllAsync(true, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Update_AdminEnablesAfterDraftWasLoaded_RejectsStaleScriptAndPreservesApprovedDefinition()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, initial) = TestDbFactory.CreateWithConnection();
        await using var ownedConnection = connection;
        await using var adminDb = initial;
        var adminStore = new CustomActivityDefinitionStore(adminDb);
        var original = await adminStore.CreateAsync(Input(), "operator", ct);
        var expected = original.ConcurrencyToken;
        var barrier = new BeforeSave(async () => await adminStore.SetEnabledAsync(original.Id, true, original.ConcurrencyToken, "admin", ct));
        await using var editorDb = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(barrier).Options);
        var editor = new CustomActivityDefinitionStore(editorDb);

        var update = () => editor.UpdateAsync(original.Id, Input() with { ScriptTemplate = "Write-Output changed" },
            expected, "operator", ct);

        await update.Should().ThrowAsync<CustomActivityConcurrencyException>();
        await adminDb.Entry(original).ReloadAsync(ct);
        original.IsEnabled.Should().BeTrue();
        original.ScriptTemplate.Should().Be("Get-PSDrive C");
        original.Version.Should().Be(1);
        (await adminDb.CustomActivityDefinitionVersions.CountAsync(ct)).Should().Be(0);
    }

    private sealed class BeforeSave(Func<Task> mutation) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await mutation();
            return result;
        }
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("rollback")]
    [InlineData("enable")]
    public async Task Mutation_RejectsRevisionChangedAfterAuthorizationRead(string operation)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var definition = await store.CreateAsync(Input(), "operator", ct);
        var inspected = definition.ConcurrencyToken;
        await store.UpdateAsync(definition.Id, Input() with { ScriptTemplate = "Write-Output changed" }, inspected, "operator", ct);

        var mutate = () => MutateAsync(store, definition.Id, inspected, operation, ct);
        await mutate.Should().ThrowAsync<CustomActivityConcurrencyException>();
        definition.IsEnabled.Should().BeFalse();
        definition.IsDeleted.Should().BeFalse();
        definition.ScriptTemplate.Should().Be("Write-Output changed");
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("rollback")]
    [InlineData("enable")]
    public async Task Mutation_RejectsRevisionChangedBetweenLoadAndCommit(string operation)
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, initial) = TestDbFactory.CreateWithConnection();
        await using var ownedConnection = connection;
        await using var concurrentDb = initial;
        var other = new CustomActivityDefinitionStore(concurrentDb);
        var definition = await other.CreateAsync(Input(), "operator", ct);
        await other.UpdateAsync(definition.Id, Input() with { ScriptTemplate = "Write-Output v2" }, definition.ConcurrencyToken, "operator", ct);
        var inspected = definition.ConcurrencyToken;
        var barrier = new BeforeSave(async () =>
        {
            if (operation == "enable")
                await other.UpdateAsync(definition.Id, Input() with { ScriptTemplate = "Write-Output v3" }, definition.ConcurrencyToken, "operator", ct);
            else
                await other.SetEnabledAsync(definition.Id, true, definition.ConcurrencyToken, "admin", ct);
        });
        await using var editorDb = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(barrier).Options);
        var store = new CustomActivityDefinitionStore(editorDb);

        var mutate = () => MutateAsync(store, definition.Id, inspected, operation, ct);
        await mutate.Should().ThrowAsync<CustomActivityConcurrencyException>();
        await concurrentDb.Entry(definition).ReloadAsync(ct);
        definition.IsDeleted.Should().BeFalse();
        definition.IsEnabled.Should().Be(operation != "enable");
        definition.ScriptTemplate.Should().Be(operation == "enable" ? "Write-Output v3" : "Write-Output v2");
        definition.Version.Should().Be(operation == "enable" ? 3 : 2);
    }

    private static Task MutateAsync(CustomActivityDefinitionStore store, Guid id, Guid inspected,
        string operation, CancellationToken ct) => operation switch
    {
        "delete" => store.SoftDeleteAsync(id, inspected, ct),
        "rollback" => store.RollbackAsync(id, 1, inspected, "operator", ct),
        "enable" => store.SetEnabledAsync(id, true, inspected, "admin", ct),
        _ => throw new ArgumentException(operation)
    };

    private static CustomActivityDefinitionInput Input(string key = "disk_check", string name = "Disk Check") => new()
    {
        Key = key,
        Name = name,
        ScriptTemplate = "Get-PSDrive C",
        Engine = "auto",
        InputParametersJson = "[]",
        OutputParametersJson = "[]",
    };

    [Fact]
    public async Task Create_StartsDisabled_AtVersion1()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);

        var def = await store.CreateAsync(Input(), "alice", CancellationToken.None);

        def.IsEnabled.Should().BeFalse("a new custom activity is a Draft until an admin enables it");
        def.Version.Should().Be(1);
        def.CreatedBy.Should().Be("alice");
    }

    [Fact]
    public async Task Create_DuplicateLiveKey_Throws()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        await store.CreateAsync(Input(), "alice", CancellationToken.None);

        var act = () => store.CreateAsync(Input(), "bob", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Update_SnapshotsPrevious_BumpsVersion_RotatesToken()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(Input(), "alice", CancellationToken.None);
        var originalToken = def.ConcurrencyToken;

        var updated = await store.UpdateAsync(def.Id,
            Input() with { Name = "Disk Check v2", ScriptTemplate = "Get-PSDrive" },
            originalToken, "alice", CancellationToken.None);

        updated.Version.Should().Be(2);
        updated.Name.Should().Be("Disk Check v2");
        updated.ConcurrencyToken.Should().NotBe(originalToken);

        var versions = await store.GetVersionsAsync(def.Id, CancellationToken.None);
        versions.Should().ContainSingle(v => v.Version == 1 && v.Name == "Disk Check");
    }

    [Fact]
    public async Task Update_StaleToken_ThrowsConcurrency()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(Input(), "alice", CancellationToken.None);
        var staleToken = def.ConcurrencyToken;
        await store.UpdateAsync(def.Id, Input() with { Name = "v2" }, staleToken, "alice", CancellationToken.None);

        var act = () => store.UpdateAsync(def.Id, Input() with { Name = "v3" }, staleToken, "alice", CancellationToken.None);
        await act.Should().ThrowAsync<CustomActivityConcurrencyException>();
    }

    [Fact]
    public async Task SoftDelete_HidesFromReads_ButRetainsRow()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(Input(), "alice", CancellationToken.None);

        await store.SoftDeleteAsync(def.Id, def.ConcurrencyToken, CancellationToken.None);

        (await store.GetByIdAsync(def.Id, CancellationToken.None)).Should().BeNull();
        (await store.GetByKeyAsync("disk_check", CancellationToken.None)).Should().BeNull();
        db.CustomActivityDefinitions.Should().ContainSingle(d => d.Id == def.Id && d.IsDeleted,
            "tombstoned rows are retained so old executions stay reproducible");
        // Key is free again after a tombstone.
        var recreated = await store.CreateAsync(Input(), "bob", CancellationToken.None);
        recreated.Id.Should().NotBe(def.Id);
    }

    [Fact]
    public async Task GetAll_EnabledFilter()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var a = await store.CreateAsync(Input("a", "A"), "u", CancellationToken.None);
        await store.CreateAsync(Input("b", "B"), "u", CancellationToken.None);
        await store.SetEnabledAsync(a.Id, true, a.ConcurrencyToken, "admin", CancellationToken.None);

        (await store.GetAllAsync(includeDisabled: false, CancellationToken.None)).Should().ContainSingle(d => d.Key == "a");
        (await store.GetAllAsync(includeDisabled: true, CancellationToken.None)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Rollback_RestoresSnapshot_AsNewVersion()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(Input(), "alice", CancellationToken.None);
        var v2 = await store.UpdateAsync(def.Id, Input() with { ScriptTemplate = "Write-Output 2" },
            def.ConcurrencyToken, "alice", CancellationToken.None);

        var rolled = await store.RollbackAsync(def.Id, 1, def.ConcurrencyToken, "alice", CancellationToken.None);

        rolled.Version.Should().Be(3, "rollback emits a fresh forward version, not a rewind of the counter");
        rolled.ScriptTemplate.Should().Be("Get-PSDrive C", "version 1's script is restored");
        rolled.ChangeNote.Should().Contain("1");
        _ = v2; // silence unused
    }
}
