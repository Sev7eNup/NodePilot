using FluentAssertions;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Output;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// Coverage for the renderer functions not exercised by <see cref="OutputAndRenderingTests"/>.
/// These assert on the *real formatting logic* — derived strings like "09:00" from a
/// minute-of-day int, "Mon,Wed" from a weekday bitmask, secret masking, reachability
/// markup, and the fail-rate colour tiers — not just "didn't throw".
/// </summary>
public class RenderersCoverageTests
{
    private static TestConsole NewBuffer()
    {
        var console = new TestConsole();
        console.Profile.Width = 240; // wide enough that cells never wrap mid-token
        return console;
    }

    private static string Render(Action<IAnsiConsole> render)
    {
        var console = NewBuffer();
        render(console);
        return console.Output;
    }

    // ---- ExecutionDetail --------------------------------------------------------

    [Fact]
    public void ExecutionDetail_ShowsErrorAndTrace_WhenPresent()
    {
        var e = new ExecutionResponse(
            Guid.NewGuid(), Guid.NewGuid(), "Failed",
            new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 1, 9, 0, 5, DateTimeKind.Utc),
            TriggeredBy: "scheduler", ErrorMessage: "disk full",
            TraceId: "abc123trace");

        var output = Render(c => Renderers.ExecutionDetail(c, e));

        output.Should().Contain("Failed");
        output.Should().Contain("disk full");
        output.Should().Contain("abc123trace");
        output.Should().Contain("scheduler");
    }

    [Fact]
    public void ExecutionDetail_OmitsErrorAndTraceRows_AndShowsDashForNoCompletion()
    {
        var e = new ExecutionResponse(
            Guid.NewGuid(), Guid.NewGuid(), "Running",
            new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc),
            CompletedAt: null, TriggeredBy: null, ErrorMessage: null);

        var output = Render(c => Renderers.ExecutionDetail(c, e));

        output.Should().Contain("Running");
        output.Should().NotContain("Error");
        output.Should().NotContain("Trace");
        output.Should().Contain("-"); // Completed + Triggered By both fall back to "-"
    }

    // ---- WorkflowVersionDetail --------------------------------------------------

    [Fact]
    public void WorkflowVersionDetail_ShowsByteCountAndCurrentFlag()
    {
        var def = "{\"nodes\":[],\"edges\":[]}";
        var v = new WorkflowVersionDetail(
            3, "Nightly", "desc here", def,
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "admin", "tweaked cron", IsCurrent: true);

        var output = Render(c => Renderers.WorkflowVersionDetail(c, v));

        output.Should().Contain("Nightly");
        output.Should().Contain("tweaked cron");
        output.Should().Contain($"{def.Length} bytes");
        output.Should().Contain("yes"); // IsCurrent → "yes"
    }

    [Fact]
    public void WorkflowVersionDetail_NonCurrent_ShowsNo()
    {
        var v = new WorkflowVersionDetail(
            1, "Old", null, "{}",
            DateTime.UtcNow, null, null, IsCurrent: false);

        var output = Render(c => Renderers.WorkflowVersionDetail(c, v));

        output.Should().Contain("Old");
        output.Should().Contain("no");
    }

    // ---- Machines ---------------------------------------------------------------

    [Fact]
    public void Machines_RendersReachabilitySslAndDashForNeverChecked()
    {
        var rows = new[]
        {
            new MachineResponse(Guid.NewGuid(), "WEB-01", "web01.corp", 5986, UseSsl: true,
                DefaultCredentialId: Guid.NewGuid(), Tags: "prod",
                LastConnectivityCheck: new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc), IsReachable: true),
            new MachineResponse(Guid.NewGuid(), "DB-01", "db01.corp", 5985, UseSsl: false,
                DefaultCredentialId: null, Tags: null, LastConnectivityCheck: null, IsReachable: false),
        };

        var output = Render(c => Renderers.Machines(c, rows));

        output.Should().Contain("WEB-01").And.Contain("web01.corp");
        output.Should().Contain("DB-01").And.Contain("db01.corp");
        output.Should().Contain("5986").And.Contain("5985");
        output.Should().Contain("yes"); // reachable + ssl
        output.Should().Contain("no");  // unreachable + no ssl
    }

    [Fact]
    public void MachineDetail_RendersAllFields_WithDashFallbacks()
    {
        var m = new MachineResponse(Guid.NewGuid(), "APP-01", "app01.corp", 5985, UseSsl: false,
            DefaultCredentialId: null, Tags: null, LastConnectivityCheck: null, IsReachable: false);

        var output = Render(c => Renderers.MachineDetail(c, m));

        output.Should().Contain("APP-01");
        output.Should().Contain("app01.corp");
        output.Should().Contain("5985");
        output.Should().Contain("-"); // DefaultCredential + Tags + LastCheck → "-"
    }

    // ---- Credentials ------------------------------------------------------------

    [Fact]
    public void Credentials_RendersDomainOrDash()
    {
        var rows = new[]
        {
            new CredentialResponse(Guid.NewGuid(), "svc-deploy", "deployer", "CORP"),
            new CredentialResponse(Guid.NewGuid(), "local-admin", "administrator", null),
        };

        var output = Render(c => Renderers.Credentials(c, rows));

        output.Should().Contain("svc-deploy").And.Contain("deployer").And.Contain("CORP");
        output.Should().Contain("local-admin").And.Contain("administrator");
    }

    // ---- GlobalVariables --------------------------------------------------------

    [Fact]
    public void GlobalVariables_RendersSecretFlagAndServerMaskedValue()
    {
        var rows = new[]
        {
            new GlobalVariableResponse(Guid.NewGuid(), "ApiUrl", "https://api.corp", IsSecret: false,
                "endpoint", Guid.Empty, DateTime.UtcNow, DateTime.UtcNow, "admin"),
            new GlobalVariableResponse(Guid.NewGuid(), "ApiKey", "***", IsSecret: true,
                null, Guid.Empty, DateTime.UtcNow, DateTime.UtcNow, "admin"),
        };

        var output = Render(c => Renderers.GlobalVariables(c, rows));

        output.Should().Contain("ApiUrl").And.Contain("https://api.corp").And.Contain("endpoint");
        output.Should().Contain("ApiKey").And.Contain("***");
        output.Should().Contain("yes"); // IsSecret
    }

    // ---- MaintenanceWindows (DescribeWhen / DaysFromMask / MinuteToHhmm) ---------

    [Fact]
    public void MaintenanceWindows_Weekly_FormatsDayMaskAndTimes()
    {
        // Mon (bit1=2) + Wed (bit3=8) = mask 10; 540min = 09:00, 1020min = 17:00.
        var w = NewWindow("Weekly Patch", recurrence: "Weekly", scopeKind: "Machine",
            weeklyDaysMask: 10, weeklyStart: 540, weeklyEnd: 1020,
            targets: new() { new MaintenanceWindowTargetDto("Machine", Guid.NewGuid()) });

        var output = Render(c => Renderers.MaintenanceWindows(c, new[] { w }));

        output.Should().Contain("Weekly Patch");
        output.Should().Contain("Mon,Wed");
        output.Should().Contain("09:00");
        output.Should().Contain("17:00");
        output.Should().Contain("1"); // one target, ScopeKind != Global
    }

    [Fact]
    public void MaintenanceWindows_OneTime_FormatsRangeAndGlobalScopeShowsAll()
    {
        var w = NewWindow("One Shot", recurrence: "OneTime", scopeKind: "Global",
            oneTimeStart: new DateTime(2026, 5, 1, 22, 0, 0, DateTimeKind.Utc),
            oneTimeEnd: new DateTime(2026, 5, 2, 2, 0, 0, DateTimeKind.Utc));

        var output = Render(c => Renderers.MaintenanceWindows(c, new[] { w }));

        output.Should().Contain("One Shot");
        output.Should().Contain("→");   // OneTime range arrow
        output.Should().Contain("all"); // Global scope → "all" targets
    }

    [Fact]
    public void MaintenanceWindows_Weekly_EmptyMask_ShowsDash()
    {
        var w = NewWindow("No Days", recurrence: "Weekly", scopeKind: "Global",
            weeklyDaysMask: 0, weeklyStart: null, weeklyEnd: null);

        var output = Render(c => Renderers.MaintenanceWindows(c, new[] { w }));

        output.Should().Contain("No Days");
        output.Should().Contain("--:--"); // null minute → "--:--"
    }

    [Fact]
    public void MaintenanceWindows_OtherRecurrence_ShowsRawRecurrence()
    {
        var w = NewWindow("Cron Win", recurrence: "Cron", scopeKind: "Global");

        var output = Render(c => Renderers.MaintenanceWindows(c, new[] { w }));

        output.Should().Contain("Cron Win");
        output.Should().Contain("Cron");
    }

    // ---- Users ------------------------------------------------------------------

    [Fact]
    public void Users_RendersActiveFlagPerRow()
    {
        var rows = new[]
        {
            new UserResponse(Guid.NewGuid(), "alice", "Admin", IsActive: true, DateTime.UtcNow),
            new UserResponse(Guid.NewGuid(), "bob", "Viewer", IsActive: false, DateTime.UtcNow),
        };

        var output = Render(c => Renderers.Users(c, rows));

        output.Should().Contain("alice").And.Contain("Admin");
        output.Should().Contain("bob").And.Contain("Viewer");
        output.Should().Contain("yes").And.Contain("no");
    }

    // ---- Dashboard --------------------------------------------------------------

    [Fact]
    public void Dashboard_RendersCountsAndTopWorkflowsTable()
    {
        var s = new DashboardStats(
            WorkflowsTotal: 12, WorkflowsEnabled: 9,
            MachinesTotal: 5, MachinesReachable: 4,
            ExecutionsTotal: 1340,
            Last24h: new ExecutionCounts(120, 110, 8, 1, 1),
            Last24hBuckets: new(),
            TopWorkflows: new() { new TopWorkflow(Guid.NewGuid(), "Nightly Backup", 50, 47, 3) },
            Running: new() { new RunningExecutionInfo(Guid.NewGuid(), Guid.NewGuid(), "Nightly Backup", "Running", DateTime.UtcNow, "schedule") },
            Recent: new(),
            ArmedTriggers: new());

        var output = Render(c => Renderers.Dashboard(c, s));

        output.Should().Contain("12").And.Contain("9 enabled");
        output.Should().Contain("5").And.Contain("4 reachable");
        output.Should().Contain("1340");
        output.Should().Contain("Nightly Backup");
        output.Should().Contain("50"); // run count in top-workflows table
    }

    [Fact]
    public void Dashboard_OmitsTopWorkflowsTable_WhenEmpty()
    {
        var s = new DashboardStats(
            1, 1, 1, 1, 0,
            new ExecutionCounts(0, 0, 0, 0, 0),
            new(), new(), new(), new(), new());

        var output = Render(c => Renderers.Dashboard(c, s));

        output.Should().Contain("Workflows");
        output.Should().NotContain("Top workflows");
    }

    // ---- StepStats (fail-rate colour tiers + ordering) --------------------------

    [Fact]
    public void StepStats_OrdersByFailureRateDescending_AndRendersAllRows()
    {
        var stats = new Dictionary<string, StepStats>
        {
            ["low"] = new StepStats(1000, 5, 0.005, 100, 200, 110),    // grey tier (<1%)
            ["high"] = new StepStats(100, 50, 0.50, 300, 800, 400),    // red tier (>=10%)
            ["mid"] = new StepStats(100, 5, 0.05, 150, 300, 160),      // yellow tier (>=1%)
        };

        var output = Render(c => Renderers.StepStats(c, stats));

        output.Should().Contain("low").And.Contain("mid").And.Contain("high");
        // Highest failure-rate row must appear before the lowest.
        output.IndexOf("high", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("low", StringComparison.Ordinal));
        // Rates are formatted with the current culture's decimal separator — match the
        // renderer's own ":F1" formatting so this holds on de-DE and en-US alike.
        output.Should().Contain($"{50.0:F1}%").And.Contain($"{5.0:F1}%").And.Contain($"{0.5:F1}%");
    }

    // ---- TelemetrySummary -------------------------------------------------------

    [Fact]
    public void TelemetrySummary_NotAvailable_ShowsNotConfiguredMessage()
    {
        var r = new TelemetrySummaryResponse(Available: false, Panels: new());

        var output = Render(c => Renderers.TelemetrySummary(c, r));

        output.Should().Contain("not configured");
    }

    [Fact]
    public void TelemetrySummary_Available_RendersValueErrorAndNullPanels()
    {
        var r = new TelemetrySummaryResponse(Available: true, Panels: new()
        {
            new TelemetryPanel("cpu", "CPU Usage", "%", 42.5, null),
            new TelemetryPanel("mem", "Memory", "MB", null, "scrape timeout"),
            new TelemetryPanel("disk", "Disk", "GB", null, null),
        });

        var output = Render(c => Renderers.TelemetrySummary(c, r));

        output.Should().Contain("CPU Usage").And.Contain($"{42.5:F2}"); // culture-aware separator
        output.Should().Contain("Memory").And.Contain("scrape timeout");
        output.Should().Contain("Disk").And.Contain("-"); // null value, no error → "-"
    }

    // ---- StatusMarkup (all branches) --------------------------------------------

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Running")]
    [InlineData("Cancelled")]
    [InlineData("Skipped")]
    [InlineData("Paused")]
    [InlineData("Pending")]
    public void StatusMarkup_KnownStatuses_WrapStatusInMarkup(string status)
    {
        Renderers.StatusMarkup(status).Should().Contain(status).And.Contain("[").And.Contain("[/]");
    }

    [Fact]
    public void StatusMarkup_UnknownStatus_IsEscapedNotColoured()
    {
        // Unknown status falls through to Markup.Escape — no colour tags added.
        Renderers.StatusMarkup("Weird[x]").Should().NotContain("[green]");
    }

    // ---- helpers ----------------------------------------------------------------

    private static MaintenanceWindowResponse NewWindow(
        string name, string recurrence, string scopeKind,
        int weeklyDaysMask = 0, int? weeklyStart = null, int? weeklyEnd = null,
        DateTime? oneTimeStart = null, DateTime? oneTimeEnd = null,
        List<MaintenanceWindowTargetDto>? targets = null)
        => new(
            Guid.NewGuid(), name, null, IsEnabled: true,
            Mode: "Block", ScopeKind: scopeKind, Recurrence: recurrence,
            OneTimeStartUtc: oneTimeStart, OneTimeEndUtc: oneTimeEnd,
            WeeklyDaysMask: weeklyDaysMask, WeeklyStartMinuteOfDay: weeklyStart, WeeklyEndMinuteOfDay: weeklyEnd,
            CronExpression: null, DurationMinutes: null, TimeZoneId: "Europe/Berlin",
            Targets: targets ?? new(),
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow, UpdatedBy: "admin");
}
