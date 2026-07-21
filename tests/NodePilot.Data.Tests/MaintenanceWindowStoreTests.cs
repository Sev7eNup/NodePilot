using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

public class MaintenanceWindowStoreTests
{
    private static MaintenanceWindow GlobalDraft(string name) => new()
    {
        Name = name,
        Mode = MaintenanceMode.Blackout,
        ScopeKind = MaintenanceScopeKind.Global,
        Recurrence = MaintenanceRecurrenceKind.Weekly,
        WeeklyDaysMask = 0b0111110, // Mon-Fri
        WeeklyStartMinuteOfDay = 22 * 60,
        WeeklyEndMinuteOfDay = 23 * 60,
        TimeZoneId = "UTC",
    };

    [Fact]
    public async Task CreateAsync_Global_PersistsWithAuditAndNoTargets()
    {
        await using var db = TestDbFactory.Create();
        var store = new MaintenanceWindowStore(db);

        var created = await store.CreateAsync(GlobalDraft("Nightly"), "alice", CancellationToken.None);

        created.Id.Should().NotBeEmpty();
        created.UpdatedBy.Should().Be("alice");
        var loaded = await store.GetAsync(created.Id, CancellationToken.None);
        loaded!.Targets.Should().BeEmpty();
        loaded.Name.Should().Be("Nightly");
    }

    [Fact]
    public async Task CreateAsync_Folders_PersistsTargets()
    {
        await using var db = TestDbFactory.Create();
        var store = new MaintenanceWindowStore(db);
        var draft = GlobalDraft("Freeze");
        draft.ScopeKind = MaintenanceScopeKind.Folders;
        draft.Targets =
        [
            new MaintenanceWindowTarget { TargetKind = MaintenanceTargetKind.Folder, TargetId = Guid.NewGuid() },
            new MaintenanceWindowTarget { TargetKind = MaintenanceTargetKind.Folder, TargetId = Guid.NewGuid() },
        ];

        var created = await store.CreateAsync(draft, "bob", CancellationToken.None);

        var loaded = await store.GetAsync(created.Id, CancellationToken.None);
        loaded!.Targets.Should().HaveCount(2);
        loaded.Targets.Should().OnlyContain(t => t.MaintenanceWindowId == created.Id);
    }

    [Fact]
    public async Task CreateAsync_GlobalIgnoresProvidedTargets()
    {
        await using var db = TestDbFactory.Create();
        var store = new MaintenanceWindowStore(db);
        var draft = GlobalDraft("GlobalWin");
        draft.Targets = [new MaintenanceWindowTarget { TargetKind = MaintenanceTargetKind.Workflow, TargetId = Guid.NewGuid() }];

        var created = await store.CreateAsync(draft, null, CancellationToken.None);

        (await store.GetAsync(created.Id, CancellationToken.None))!.Targets.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ReplacesTargetsAndScalars()
    {
        await using var db = TestDbFactory.Create();
        var store = new MaintenanceWindowStore(db);
        var draft = GlobalDraft("W1");
        draft.ScopeKind = MaintenanceScopeKind.Workflows;
        draft.Targets =
        [
            new MaintenanceWindowTarget { TargetKind = MaintenanceTargetKind.Workflow, TargetId = Guid.NewGuid() },
            new MaintenanceWindowTarget { TargetKind = MaintenanceTargetKind.Workflow, TargetId = Guid.NewGuid() },
        ];
        var created = await store.CreateAsync(draft, "x", CancellationToken.None);

        var update = GlobalDraft("W1-renamed");
        update.Mode = MaintenanceMode.AllowOnly;
        update.ScopeKind = MaintenanceScopeKind.Workflows;
        update.Targets = [new MaintenanceWindowTarget { TargetKind = MaintenanceTargetKind.Workflow, TargetId = Guid.NewGuid() }];
        await store.UpdateAsync(created.Id, update, "y", CancellationToken.None);

        var loaded = await store.GetAsync(created.Id, CancellationToken.None);
        loaded!.Name.Should().Be("W1-renamed");
        loaded.Mode.Should().Be(MaintenanceMode.AllowOnly);
        loaded.Targets.Should().HaveCount(1);
        loaded.UpdatedBy.Should().Be("y");
        // Old targets must be gone (no orphans).
        (await db.MaintenanceWindowTargets.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_Throws()
    {
        await using var db = TestDbFactory.Create();
        var store = new MaintenanceWindowStore(db);
        var act = () => store.UpdateAsync(Guid.NewGuid(), GlobalDraft("nope"), null, CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_CascadesTargets()
    {
        await using var db = TestDbFactory.Create();
        var store = new MaintenanceWindowStore(db);
        var draft = GlobalDraft("ToDelete");
        draft.ScopeKind = MaintenanceScopeKind.Folders;
        draft.Targets =
        [
            new MaintenanceWindowTarget { TargetKind = MaintenanceTargetKind.Folder, TargetId = Guid.NewGuid() },
            new MaintenanceWindowTarget { TargetKind = MaintenanceTargetKind.Folder, TargetId = Guid.NewGuid() },
        ];
        var created = await store.CreateAsync(draft, null, CancellationToken.None);

        await store.DeleteAsync(created.Id, CancellationToken.None);

        (await store.GetAsync(created.Id, CancellationToken.None)).Should().BeNull();
        (await db.MaintenanceWindowTargets.CountAsync()).Should().Be(0, "deleting a window must cascade its targets");
    }

    [Fact]
    public async Task GetAllAsync_OrdersByNameAndIncludesTargets()
    {
        await using var db = TestDbFactory.Create();
        var store = new MaintenanceWindowStore(db);
        await store.CreateAsync(GlobalDraft("Zebra"), null, CancellationToken.None);
        await store.CreateAsync(GlobalDraft("Alpha"), null, CancellationToken.None);

        var all = await store.GetAllAsync(CancellationToken.None);

        all.Select(w => w.Name).Should().ContainInOrder("Alpha", "Zebra");
    }
}
