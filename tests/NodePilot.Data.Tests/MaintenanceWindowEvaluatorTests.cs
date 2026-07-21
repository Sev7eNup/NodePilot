using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

public class MaintenanceWindowEvaluatorTests
{
    // Minimal IServiceScopeFactory that always hands back the one test DbContext, so the
    // singleton evaluator can RefreshAsync without a real DI container (Data.Tests has none).
    private sealed class SingleContextScopeFactory(NodePilotDbContext db)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(NodePilotDbContext) ? db : null;
        public void Dispose() { }
    }

    private static async Task<MaintenanceWindowEvaluator> BuildAsync(NodePilotDbContext db)
    {
        var evaluator = new MaintenanceWindowEvaluator(
            new SingleContextScopeFactory(db), NullLogger<MaintenanceWindowEvaluator>.Instance);
        await evaluator.RefreshAsync(CancellationToken.None);
        return evaluator;
    }

    private static int DayBit(DayOfWeek dow) => 1 << (int)dow;

    private static MaintenanceWindow Weekly(string name, MaintenanceMode mode, int daysMask, int startMin, int endMin,
        MaintenanceScopeKind scope = MaintenanceScopeKind.Global, IEnumerable<MaintenanceWindowTarget>? targets = null,
        bool enabled = true)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsEnabled = enabled,
            Mode = mode,
            ScopeKind = scope,
            Recurrence = MaintenanceRecurrenceKind.Weekly,
            WeeklyDaysMask = daysMask,
            WeeklyStartMinuteOfDay = startMin,
            WeeklyEndMinuteOfDay = endMin,
            TimeZoneId = "UTC",
            Targets = targets?.ToList() ?? [],
        };

    [Fact]
    public async Task GlobalWeeklyBlackout_InsideWindow_BlocksAnyWorkflow()
    {
        await using var db = TestDbFactory.Create();
        var now = new DateTime(2026, 6, 3, 23, 0, 0, DateTimeKind.Utc); // 23:00
        db.MaintenanceWindows.Add(Weekly("Nightly", MaintenanceMode.Blackout, DayBit(now.DayOfWeek), 22 * 60, 23 * 60 + 30));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        var verdict = ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), now);

        verdict.Blocked.Should().BeTrue();
        verdict.Mode.Should().Be(MaintenanceMode.Blackout);
        verdict.ActiveUntilUtc.Should().Be(new DateTime(2026, 6, 3, 23, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task GlobalWeeklyBlackout_OutsideWindow_Allows()
    {
        await using var db = TestDbFactory.Create();
        var now = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc); // midday, window is 22:00-23:30
        db.MaintenanceWindows.Add(Weekly("Nightly", MaintenanceMode.Blackout, DayBit(now.DayOfWeek), 22 * 60, 23 * 60 + 30));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), now).Blocked.Should().BeFalse();
    }

    [Fact]
    public async Task WeeklyBlackout_WrapsPastMidnight_ActiveEveningAndMorningButNotGap()
    {
        await using var db = TestDbFactory.Create();
        var sat = new DateTime(2026, 6, 6, 0, 0, 0, DateTimeKind.Utc); // Saturday
        sat.DayOfWeek.Should().Be(DayOfWeek.Saturday);
        // Sat 22:00 -> Sun 02:00
        db.MaintenanceWindows.Add(Weekly("Patch", MaintenanceMode.Blackout, DayBit(DayOfWeek.Saturday), 22 * 60, 2 * 60));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), sat.AddHours(23)).Blocked.Should().BeTrue("Sat 23:00 is the evening portion");
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), sat.AddDays(1).AddHours(1)).Blocked.Should().BeTrue("Sun 01:00 is the morning portion that started Sat");
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), sat.AddDays(1).AddHours(3)).Blocked.Should().BeFalse("Sun 03:00 is past the window");
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), sat.AddHours(12)).Blocked.Should().BeFalse("Sat 12:00 is before the window");
    }

    [Fact]
    public async Task OneTimeBlackout_InsideAndOutside()
    {
        await using var db = TestDbFactory.Create();
        db.MaintenanceWindows.Add(new MaintenanceWindow
        {
            Id = Guid.NewGuid(), Name = "Migration", Mode = MaintenanceMode.Blackout,
            ScopeKind = MaintenanceScopeKind.Global, Recurrence = MaintenanceRecurrenceKind.OneTime,
            OneTimeStartUtc = new DateTime(2026, 6, 6, 22, 0, 0, DateTimeKind.Utc),
            OneTimeEndUtc = new DateTime(2026, 6, 7, 6, 0, 0, DateTimeKind.Utc),
            TimeZoneId = "UTC",
        });
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 6, 7, 1, 0, 0, DateTimeKind.Utc)).Blocked.Should().BeTrue();
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 6, 7, 7, 0, 0, DateTimeKind.Utc)).Blocked.Should().BeFalse();
    }

    [Fact]
    public async Task FolderScope_BlocksWorkflowInDescendantFolder()
    {
        await using var db = TestDbFactory.Create();
        var parent = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "finance", Path = "/finance", Depth = 1, ParentFolderId = null };
        var child = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "reports", Path = "/finance/reports", Depth = 2, ParentFolderId = parent.Id };
        db.SharedWorkflowFolders.AddRange(parent, child);
        db.MaintenanceWindows.Add(Weekly("FinanceFreeze", MaintenanceMode.Blackout, DayBit(DayOfWeek.Monday), 0, 1439,
            MaintenanceScopeKind.Folders, [new MaintenanceWindowTarget { Id = Guid.NewGuid(), TargetKind = MaintenanceTargetKind.Folder, TargetId = parent.Id }]));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        var monday = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        monday.DayOfWeek.Should().Be(DayOfWeek.Monday);

        // Workflow in the CHILD folder is blocked because its ancestor (parent) is targeted.
        ev.Evaluate(Guid.NewGuid(), child.Id, monday).Blocked.Should().BeTrue();
        // A workflow in an unrelated folder is not blocked.
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), monday).Blocked.Should().BeFalse();
    }

    [Fact]
    public async Task WorkflowScope_BlocksOnlyListedWorkflow()
    {
        await using var db = TestDbFactory.Create();
        var targetWf = Guid.NewGuid();
        db.MaintenanceWindows.Add(Weekly("Specific", MaintenanceMode.Blackout, DayBit(DayOfWeek.Monday), 0, 1439,
            MaintenanceScopeKind.Workflows, [new MaintenanceWindowTarget { Id = Guid.NewGuid(), TargetKind = MaintenanceTargetKind.Workflow, TargetId = targetWf }]));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        var monday = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        ev.Evaluate(targetWf, Guid.NewGuid(), monday).Blocked.Should().BeTrue();
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), monday).Blocked.Should().BeFalse();
    }

    [Fact]
    public async Task DanglingTargets_ReferencedFolderOrWorkflowDeleted_AreInert()
    {
        // F8: MaintenanceWindowTarget.TargetId is a soft reference (no FK). The evaluator is a
        // pure in-memory id matcher, so deleting the referenced folder/workflow must leave the
        // window inert — RefreshAsync must not choke on the missing referent, and the orphaned
        // target must never spuriously block a live, unrelated workflow.
        await using var db = TestDbFactory.Create();
        var monday = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        monday.DayOfWeek.Should().Be(DayOfWeek.Monday);

        var doomedFolder = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "temp", Path = "/temp", Depth = 1, ParentFolderId = null };
        db.SharedWorkflowFolders.Add(doomedFolder);
        await db.SaveChangesAsync();

        // Two all-day Monday blackouts: one scoped to the folder, one to a workflow id.
        db.MaintenanceWindows.Add(Weekly("FolderFreeze", MaintenanceMode.Blackout, DayBit(DayOfWeek.Monday), 0, 1439,
            MaintenanceScopeKind.Folders, [new MaintenanceWindowTarget { Id = Guid.NewGuid(), TargetKind = MaintenanceTargetKind.Folder, TargetId = doomedFolder.Id }]));
        var doomedWorkflowId = Guid.NewGuid();
        db.MaintenanceWindows.Add(Weekly("WorkflowFreeze", MaintenanceMode.Blackout, DayBit(DayOfWeek.Monday), 0, 1439,
            MaintenanceScopeKind.Workflows, [new MaintenanceWindowTarget { Id = Guid.NewGuid(), TargetKind = MaintenanceTargetKind.Workflow, TargetId = doomedWorkflowId }]));
        await db.SaveChangesAsync();

        // Delete the referenced folder — the folder window's target is now dangling. (The workflow
        // window's target id was never a live workflow, i.e. already "deleted" from the matcher's view.)
        db.SharedWorkflowFolders.Remove(doomedFolder);
        await db.SaveChangesAsync();

        // RefreshAsync must load the window with the now-missing folder referent without throwing...
        var ev = await BuildAsync(db);

        // ...and the dangling folder/workflow targets must not block an unrelated live workflow.
        var liveWorkflow = Guid.NewGuid();
        var liveFolder = Guid.NewGuid();
        ev.Evaluate(liveWorkflow, liveFolder, monday).Blocked.Should().BeFalse();
        ev.GetWindowsAffecting(liveWorkflow, liveFolder, monday).Should().BeEmpty();
    }

    [Fact]
    public async Task DenyWins_ActiveBlackoutBeatsAllowOnly()
    {
        await using var db = TestDbFactory.Create();
        var monday = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        db.MaintenanceWindows.Add(Weekly("AllowAllDay", MaintenanceMode.AllowOnly, DayBit(DayOfWeek.Monday), 0, 1439));
        db.MaintenanceWindows.Add(Weekly("BlackoutNow", MaintenanceMode.Blackout, DayBit(DayOfWeek.Monday), 9 * 60, 11 * 60));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), monday).Blocked.Should().BeTrue("an active blackout wins over an allow-only");
    }

    [Fact]
    public async Task AllowOnly_OutsideWindow_Blocks_InsideAllows()
    {
        await using var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        db.MaintenanceWindows.Add(Weekly("OnlyNight", MaintenanceMode.AllowOnly, DayBit(DayOfWeek.Monday), 1 * 60, 4 * 60,
            MaintenanceScopeKind.Workflows,
            [new MaintenanceWindowTarget { Id = Guid.NewGuid(), TargetKind = MaintenanceTargetKind.Workflow, TargetId = wf }]));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        var monday = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        ev.Evaluate(wf, Guid.NewGuid(), monday.AddHours(2)).Blocked.Should().BeFalse("02:00 is inside the allow window");
        ev.Evaluate(wf, Guid.NewGuid(), monday.AddHours(10)).Blocked.Should().BeTrue("10:00 is outside the only allow window");
    }

    [Fact]
    public async Task ExpiredOneTimeAllowOnly_IsInert()
    {
        await using var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        db.MaintenanceWindows.Add(new MaintenanceWindow
        {
            Id = Guid.NewGuid(), Name = "PastAllow", Mode = MaintenanceMode.AllowOnly,
            ScopeKind = MaintenanceScopeKind.Workflows, Recurrence = MaintenanceRecurrenceKind.OneTime,
            OneTimeStartUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            OneTimeEndUtc = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            TimeZoneId = "UTC",
            Targets = [new MaintenanceWindowTarget { Id = Guid.NewGuid(), TargetKind = MaintenanceTargetKind.Workflow, TargetId = wf }],
        });
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        // Long after the one-time allow window expired, it must neither block forever nor matter.
        ev.Evaluate(wf, Guid.NewGuid(), new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc)).Blocked.Should().BeFalse();
    }

    [Fact]
    public async Task DisabledWindow_IsIgnored()
    {
        await using var db = TestDbFactory.Create();
        var now = new DateTime(2026, 6, 3, 23, 0, 0, DateTimeKind.Utc);
        db.MaintenanceWindows.Add(Weekly("Off", MaintenanceMode.Blackout, DayBit(now.DayOfWeek), 0, 1439, enabled: false));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), now).Blocked.Should().BeFalse();
    }

    [Fact]
    public async Task GetWindowsAffecting_ReturnsTargetingWindows()
    {
        await using var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        db.MaintenanceWindows.Add(Weekly("Global1", MaintenanceMode.Blackout, DayBit(DayOfWeek.Monday), 0, 1439));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        var affecting = ev.GetWindowsAffecting(wf, Guid.NewGuid(), new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc));
        affecting.Should().HaveCount(1);
        affecting[0].Name.Should().Be("Global1");
        affecting[0].ActiveNow.Should().BeTrue();
    }

    [Fact]
    public async Task EmptySnapshot_Allows()
    {
        await using var db = TestDbFactory.Create();
        var ev = await BuildAsync(db);
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow).Blocked.Should().BeFalse();
    }

    [Fact]
    public async Task NonUtcTimeZone_WeeklyWindow_EvaluatesInLocalWallClock()
    {
        await using var db = TestDbFactory.Create();
        // 02:00–03:00 local Monday in W. Europe Standard Time (UTC+1 in January, no DST).
        db.MaintenanceWindows.Add(new MaintenanceWindow
        {
            Id = Guid.NewGuid(), Name = "BerlinNight", Mode = MaintenanceMode.Blackout,
            ScopeKind = MaintenanceScopeKind.Global, Recurrence = MaintenanceRecurrenceKind.Weekly,
            WeeklyDaysMask = DayBit(DayOfWeek.Monday), WeeklyStartMinuteOfDay = 2 * 60, WeeklyEndMinuteOfDay = 3 * 60,
            TimeZoneId = "W. Europe Standard Time",
        });
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        // 2026-01-05 is a Monday. 01:30 UTC == 02:30 Berlin local -> inside [02:00,03:00).
        var insideUtc = new DateTime(2026, 1, 5, 1, 30, 0, DateTimeKind.Utc);
        // 02:30 UTC == 03:30 Berlin local -> outside.
        var outsideUtc = new DateTime(2026, 1, 5, 2, 30, 0, DateTimeKind.Utc);

        // A naive UTC evaluator would treat 01:30 as outside the window and fail to block — so
        // this asserts the time-zone conversion actually happens.
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), insideUtc).Blocked.Should().BeTrue("02:30 Berlin local is inside");
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), outsideUtc).Blocked.Should().BeFalse("03:30 Berlin local is outside");
    }

    [Fact]
    public async Task IanaTimeZone_ResolvesAndEvaluates()
    {
        await using var db = TestDbFactory.Create();
        // The UI sends IANA ids (Intl...timeZone). The evaluator must resolve them even on a
        // Windows host, where raw FindSystemTimeZoneById historically only knew Windows ids.
        db.MaintenanceWindows.Add(new MaintenanceWindow
        {
            Id = Guid.NewGuid(), Name = "BerlinIana", Mode = MaintenanceMode.Blackout,
            ScopeKind = MaintenanceScopeKind.Global, Recurrence = MaintenanceRecurrenceKind.Weekly,
            WeeklyDaysMask = DayBit(DayOfWeek.Monday), WeeklyStartMinuteOfDay = 2 * 60, WeeklyEndMinuteOfDay = 3 * 60,
            TimeZoneId = "Europe/Berlin",
        });
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        // 2026-01-05 Monday, 01:30 UTC == 02:30 Berlin local -> inside; 02:30 UTC == 03:30 -> outside.
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 1, 5, 1, 30, 0, DateTimeKind.Utc)).Blocked.Should().BeTrue();
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 1, 5, 2, 30, 0, DateTimeKind.Utc)).Blocked.Should().BeFalse();
    }

    [Fact]
    public async Task UnknownTimeZone_WeeklyWindow_IsInert_FailsOpen()
    {
        await using var db = TestDbFactory.Create();
        // A garbage TimeZoneId resolves to neither an IANA nor a Windows zone. RefreshAsync must
        // compile the window with Tz=null (and log a warning) instead of throwing — and the
        // evaluator must then treat it as inert. A misconfigured blackout fails OPEN; it must
        // never trap every workflow forever just because someone fat-fingered the zone.
        db.MaintenanceWindows.Add(new MaintenanceWindow
        {
            Id = Guid.NewGuid(), Name = "BadTz", Mode = MaintenanceMode.Blackout,
            ScopeKind = MaintenanceScopeKind.Global, Recurrence = MaintenanceRecurrenceKind.Weekly,
            WeeklyDaysMask = DayBit(DayOfWeek.Monday), WeeklyStartMinuteOfDay = 0, WeeklyEndMinuteOfDay = 1439,
            TimeZoneId = "Not/ARealZone",
        });
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        var monday = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        monday.DayOfWeek.Should().Be(DayOfWeek.Monday);
        // Would be deep inside an all-day Monday blackout had the zone resolved — but it didn't,
        // so the window is skipped and the run is allowed.
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), monday).Blocked.Should().BeFalse();
    }

    [Fact]
    public async Task WeeklyFullDay_StartEqualsEnd_BlocksWholeDayOnEnabledWeekday()
    {
        await using var db = TestDbFactory.Create();
        // "every Sunday, all day": start == end == 00:00 means a full 24h window.
        db.MaintenanceWindows.Add(Weekly("SundayFreeze", MaintenanceMode.Blackout, DayBit(DayOfWeek.Sunday), 0, 0));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        var sunday = new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc);
        sunday.DayOfWeek.Should().Be(DayOfWeek.Sunday);

        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), sunday.AddMinutes(1)).Blocked.Should().BeTrue("00:01 Sunday");
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), sunday.AddHours(12)).Blocked.Should().BeTrue("noon Sunday");
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), sunday.AddHours(23).AddMinutes(59)).Blocked.Should().BeTrue("23:59 Sunday");
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), sunday.AddDays(1)).Blocked.Should().BeFalse("Monday 00:00 is the next day");
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), sunday.AddDays(-1).AddHours(12)).Blocked.Should().BeFalse("Saturday is not enabled");
    }

    [Fact]
    public async Task GetWindowsAffecting_ResolvesFolderAncestry()
    {
        await using var db = TestDbFactory.Create();
        var parent = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "ops", Path = "/ops", Depth = 1, ParentFolderId = null };
        var child = new SharedWorkflowFolder { Id = Guid.NewGuid(), Name = "db", Path = "/ops/db", Depth = 2, ParentFolderId = parent.Id };
        db.SharedWorkflowFolders.AddRange(parent, child);
        db.MaintenanceWindows.Add(Weekly("OpsWin", MaintenanceMode.Blackout, DayBit(DayOfWeek.Monday), 0, 1439,
            MaintenanceScopeKind.Folders, [new MaintenanceWindowTarget { Id = Guid.NewGuid(), TargetKind = MaintenanceTargetKind.Folder, TargetId = parent.Id }]));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        var now = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        ev.GetWindowsAffecting(Guid.NewGuid(), child.Id, now).Should().ContainSingle(w => w.Name == "OpsWin");
        ev.GetWindowsAffecting(Guid.NewGuid(), Guid.NewGuid(), now).Should().BeEmpty();
    }

    // ---- Cron recurrence ----

    private static MaintenanceWindow Cron(string name, MaintenanceMode mode, string expression,
        int? durationMinutes, string tz = "UTC")
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsEnabled = true,
            Mode = mode,
            ScopeKind = MaintenanceScopeKind.Global,
            Recurrence = MaintenanceRecurrenceKind.Cron,
            CronExpression = expression,
            DurationMinutes = durationMinutes,
            TimeZoneId = tz,
        };

    [Fact]
    public async Task CronBlackout_ActiveInsideFireWindow_InactiveOutside()
    {
        await using var db = TestDbFactory.Create();
        // Fires daily at 03:00 UTC, open for 60 minutes: active during [03:00, 04:00).
        db.MaintenanceWindows.Add(Cron("NightlyCron", MaintenanceMode.Blackout, "0 0 3 * * ?", 60));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        var day = new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);
        var atFire = ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), day.AddHours(3));
        atFire.Blocked.Should().BeTrue("the fire instant itself is inside the window");
        atFire.ActiveUntilUtc.Should().Be(day.AddHours(4));

        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), day.AddHours(3).AddMinutes(30)).Blocked
            .Should().BeTrue("03:30 is inside [03:00, 04:00)");
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), day.AddHours(4)).Blocked
            .Should().BeFalse("04:00 is the half-open end — no longer active");
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), day.AddHours(2).AddMinutes(59)).Blocked
            .Should().BeFalse("02:59 is before the fire");
    }

    [Fact]
    public async Task CronBlackout_TimeZoneRespected()
    {
        await using var db = TestDbFactory.Create();
        // Daily 03:00 Berlin local (UTC+1 in January, no DST) == 02:00 UTC; open 60 minutes.
        db.MaintenanceWindows.Add(Cron("BerlinCron", MaintenanceMode.Blackout, "0 0 3 * * ?", 60,
            tz: "W. Europe Standard Time"));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        // 02:30 UTC == 03:30 Berlin -> inside [03:00, 04:00) local.
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 1, 5, 2, 30, 0, DateTimeKind.Utc)).Blocked
            .Should().BeTrue("02:30 UTC is 03:30 Berlin local — inside");
        // 03:30 UTC == 04:30 Berlin -> outside. A naive UTC evaluator would block here.
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 1, 5, 3, 30, 0, DateTimeKind.Utc)).Blocked
            .Should().BeFalse("03:30 UTC is 04:30 Berlin local — outside");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task CronBlackout_MissingOrZeroDuration_NeverActive(int? durationMinutes)
    {
        await using var db = TestDbFactory.Create();
        db.MaintenanceWindows.Add(Cron("NoDuration", MaintenanceMode.Blackout, "0 0 3 * * ?", durationMinutes));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        // Even exactly at the fire instant the window must be inert (fail open).
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 6, 3, 3, 0, 0, DateTimeKind.Utc)).Blocked
            .Should().BeFalse();
    }

    [Fact]
    public async Task CronBlackout_InvalidExpression_IsInert_FailsOpen()
    {
        await using var db = TestDbFactory.Create();
        // A garbage expression must not throw during refresh and must never block.
        db.MaintenanceWindows.Add(Cron("Garbage", MaintenanceMode.Blackout, "not a cron", 60));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 6, 3, 3, 30, 0, DateTimeKind.Utc)).Blocked
            .Should().BeFalse();
    }

    [Fact]
    public async Task CronAllowOnly_InsideFireWindowAllows_OutsideBlocks()
    {
        await using var db = TestDbFactory.Create();
        // AllowOnly cron: workflows may run ONLY during [03:00, 04:00) UTC each day.
        db.MaintenanceWindows.Add(Cron("OnlyCronSlot", MaintenanceMode.AllowOnly, "0 0 3 * * ?", 60));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        var day = new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), day.AddHours(3).AddMinutes(15)).Blocked
            .Should().BeFalse("inside the allow slot");
        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), day.AddHours(10)).Blocked
            .Should().BeTrue("a configured cron AllowOnly is live and traps runs outside its slot");
    }

    [Fact]
    public async Task CronAllowOnly_ExhaustedExpression_DoesNotTrapForever()
    {
        await using var db = TestDbFactory.Create();
        // Quartz supports an optional YEAR field. This expression fired Saturdays 03:00 UTC
        // in 2020 only — as of 2026 it can never fire again. An exhausted AllowOnly window
        // must go non-live (like an elapsed OneTime), otherwise it permanently blocks every
        // targeted workflow (live allow window that is never active).
        db.MaintenanceWindows.Add(Cron("SatPatching2020", MaintenanceMode.AllowOnly, "0 0 3 ? * SAT 2020", 60));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        ev.Evaluate(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc)).Blocked
            .Should().BeFalse("an exhausted cron AllowOnly is no longer live and must not trap runs");
    }

    [Fact]
    public async Task GetWindowsAffecting_CronWindow_ReportsActiveNow()
    {
        await using var db = TestDbFactory.Create();
        db.MaintenanceWindows.Add(Cron("CronBadge", MaintenanceMode.Blackout, "0 0 3 * * ?", 60));
        await db.SaveChangesAsync();
        var ev = await BuildAsync(db);

        var inside = new DateTime(2026, 6, 3, 3, 30, 0, DateTimeKind.Utc);
        var outside = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        ev.GetWindowsAffecting(Guid.NewGuid(), Guid.NewGuid(), inside)
            .Should().ContainSingle(w => w.Name == "CronBadge" && w.ActiveNow);
        ev.GetWindowsAffecting(Guid.NewGuid(), Guid.NewGuid(), outside)
            .Should().ContainSingle(w => w.Name == "CronBadge" && !w.ActiveNow);
    }
}
