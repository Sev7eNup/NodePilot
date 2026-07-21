using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

public class CustomActivityDefinitionStoreTests
{
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

        await store.SoftDeleteAsync(def.Id, CancellationToken.None);

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
        await store.SetEnabledAsync(a.Id, true, "admin", CancellationToken.None);

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

        var rolled = await store.RollbackAsync(def.Id, 1, "alice", CancellationToken.None);

        rolled.Version.Should().Be(3, "rollback emits a fresh forward version, not a rewind of the counter");
        rolled.ScriptTemplate.Should().Be("Get-PSDrive C", "version 1's script is restored");
        rolled.ChangeNote.Should().Contain("1");
        _ = v2; // silence unused
    }
}
