using FluentAssertions;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// <see cref="OperationalKnowledgeReader"/> — the instance-wide read source behind the global AI
/// Chat's operational tools. SQLite in-memory; RBAC via <see cref="AccessibleFolderSet"/>;
/// redaction via the deterministic <see cref="StubAuditDetailsRedactor"/> (masks <c>hunter2</c>).
/// </summary>
public class OperationalKnowledgeReaderTests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid FolderA = Guid.NewGuid();
    private static readonly Guid FolderB = Guid.NewGuid();

    private static OperationalKnowledgeReader NewReader(NodePilotDbContext db) => new(db, new StubAuditDetailsRedactor());

    private static AccessibleFolderSet Scoped(params Guid[] folders)
        => new() { IsUnrestricted = false, FolderIds = new HashSet<Guid>(folders) };

    private static Workflow SeedWorkflow(NodePilotDbContext db, string name, Guid folderId, string def = "{}")
    {
        // Workflow.FolderId has an FK to SharedWorkflowFolder — lazily seed the (non-root) folder so
        // callers can just pass an arbitrary folder GUID. The Local check dedups within one SaveChanges.
        if (folderId != SharedWorkflowFolder.RootFolderId && db.SharedWorkflowFolders.Local.All(f => f.Id != folderId))
        {
            var tag = folderId.ToString()[..8];
            db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
            {
                Id = folderId,
                Name = "folder-" + tag,
                ParentFolderId = SharedWorkflowFolder.RootFolderId,
                Path = "/folder-" + tag,
                Depth = 1,
            });
        }
        var wf = new Workflow { Id = Guid.NewGuid(), Name = name, FolderId = folderId, DefinitionJson = def, ActivityCount = 1 };
        db.Workflows.Add(wf);
        return wf;
    }

    private static void SeedExec(NodePilotDbContext db, Guid workflowId, ExecutionStatus status, DateTime startedAt, string? error = null)
        => db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflowId, Status = status,
            StartedAt = startedAt, CompletedAt = startedAt.AddSeconds(3), TriggeredBy = "manual", ErrorMessage = error,
        });

    // ---- ListWorkflowsAsync (RBAC scoping) -----------------------------------------------------

    [Fact]
    public async Task ListWorkflowsAsync_Unrestricted_ReturnsAllFolders()
    {
        await using var db = TestDbFactory.Create();
        SeedWorkflow(db, "a", FolderA);
        SeedWorkflow(db, "b", FolderB);
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListWorkflowsAsync(AccessibleFolderSet.Unrestricted, null, 25, CancellationToken.None);
        result.Select(w => w.Name).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task ListWorkflowsAsync_Scoped_ReturnsOnlyAccessibleFolder()
    {
        await using var db = TestDbFactory.Create();
        SeedWorkflow(db, "mine", FolderA);
        SeedWorkflow(db, "hidden", FolderB);
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListWorkflowsAsync(Scoped(FolderA), null, 25, CancellationToken.None);
        result.Should().ContainSingle().Which.Name.Should().Be("mine");
    }

    [Fact]
    public async Task ListWorkflowsAsync_ZeroAccess_ReturnsEmpty()
    {
        await using var db = TestDbFactory.Create();
        SeedWorkflow(db, "a", FolderA);
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListWorkflowsAsync(AccessibleFolderSet.None, null, 25, CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListWorkflowsAsync_NameFilter_MatchesSubstringCaseInsensitive()
    {
        await using var db = TestDbFactory.Create();
        SeedWorkflow(db, "Nightly Backup", FolderA);
        SeedWorkflow(db, "Deploy", FolderA);
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListWorkflowsAsync(AccessibleFolderSet.Unrestricted, "backup", 25, CancellationToken.None);
        result.Should().ContainSingle().Which.Name.Should().Be("Nightly Backup");
    }

    // ---- GetWorkflowDefinitionAsync (redaction + name resolution) ------------------------------

    [Fact]
    public async Task GetWorkflowDefinitionAsync_RedactsKeyBasedAndInlineSecrets()
    {
        await using var db = TestDbFactory.Create();
        // "apiKey" is a secret config key → key-based masking; "hunter2" inside a runScript body is
        // caught only by the pattern-based IAuditDetailsRedactor pass.
        var def = """
            {"nodes":[{"id":"s1","data":{"activityType":"runScript","config":{"apiKey":"supersecret","script":"$p = 'hunter2'"}}}],"edges":[]}
            """;
        var wf = SeedWorkflow(db, "wf", FolderA, def);
        await db.SaveChangesAsync();

        var detail = await NewReader(db).GetWorkflowDefinitionAsync(AccessibleFolderSet.Unrestricted, "wf", CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.RedactedDefinitionJson.Should().NotContain("supersecret"); // key-based (WorkflowSecretRedactor)
        detail.RedactedDefinitionJson.Should().NotContain("hunter2");      // pattern-based (IAuditDetailsRedactor)
        detail.RedactedDefinitionJson.Should().Contain("***");
    }

    [Fact]
    public async Task GetWorkflowDefinitionAsync_ByGuid_Works()
    {
        await using var db = TestDbFactory.Create();
        var wf = SeedWorkflow(db, "wf", FolderA);
        await db.SaveChangesAsync();

        var detail = await NewReader(db).GetWorkflowDefinitionAsync(AccessibleFolderSet.Unrestricted, wf.Id.ToString(), CancellationToken.None);
        detail.Should().NotBeNull();
        detail!.Id.Should().Be(wf.Id);
    }

    [Fact]
    public async Task GetWorkflowDefinitionAsync_AmbiguousName_ReturnsNull()
    {
        await using var db = TestDbFactory.Create();
        SeedWorkflow(db, "dup", FolderA);
        SeedWorkflow(db, "dup", FolderB);
        await db.SaveChangesAsync();

        var detail = await NewReader(db).GetWorkflowDefinitionAsync(AccessibleFolderSet.Unrestricted, "dup", CancellationToken.None);
        detail.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkflowDefinitionAsync_OutOfScope_ReturnsNull()
    {
        await using var db = TestDbFactory.Create();
        SeedWorkflow(db, "secret-wf", FolderB);
        await db.SaveChangesAsync();

        var detail = await NewReader(db).GetWorkflowDefinitionAsync(Scoped(FolderA), "secret-wf", CancellationToken.None);
        detail.Should().BeNull();
    }

    // ---- Executions ----------------------------------------------------------------------------

    [Fact]
    public async Task ListRecentExecutionsAsync_ScopedByWorkflowFolder_FiltersStatus_AndRedacts()
    {
        await using var db = TestDbFactory.Create();
        var mine = SeedWorkflow(db, "mine", FolderA);
        var hidden = SeedWorkflow(db, "hidden", FolderB);
        SeedExec(db, mine.Id, ExecutionStatus.Failed, T0, error: "leaked hunter2 token");
        SeedExec(db, mine.Id, ExecutionStatus.Succeeded, T0.AddMinutes(1));
        SeedExec(db, hidden.Id, ExecutionStatus.Failed, T0.AddMinutes(2));
        await db.SaveChangesAsync();

        var failed = await NewReader(db).ListRecentExecutionsAsync(Scoped(FolderA), "Failed", 25, CancellationToken.None);

        failed.Should().ContainSingle();
        failed[0].WorkflowName.Should().Be("mine");
        failed[0].Status.Should().Be("Failed");
        failed[0].ErrorMessage.Should().Be("leaked *** token"); // redacted
    }

    [Fact]
    public async Task GetWorkflowExecutionsAsync_ByName_ReturnsOnlyThatWorkflowsRuns()
    {
        await using var db = TestDbFactory.Create();
        var a = SeedWorkflow(db, "a", FolderA);
        var b = SeedWorkflow(db, "b", FolderA);
        SeedExec(db, a.Id, ExecutionStatus.Succeeded, T0);
        SeedExec(db, b.Id, ExecutionStatus.Succeeded, T0);
        await db.SaveChangesAsync();

        var runs = await NewReader(db).GetWorkflowExecutionsAsync(AccessibleFolderSet.Unrestricted, "a", 25, CancellationToken.None);
        runs.Should().ContainSingle().Which.WorkflowName.Should().Be("a");
    }

    // ---- ListScheduledFiresAsync (next-fire forecast) ------------------------------------------

    private static string ScheduleDef(string cron) =>
        """{"nodes":[{"id":"t1","type":"activity","data":{"activityType":"scheduleTrigger","config":{"cronExpression":"__CRON__"}}}],"edges":[]}"""
        .Replace("__CRON__", cron);

    private static Workflow SeedScheduled(NodePilotDbContext db, string name, Guid folderId, string cron, bool enabled = true)
    {
        var wf = SeedWorkflow(db, name, folderId, ScheduleDef(cron));
        wf.IsEnabled = enabled;
        return wf;
    }

    [Fact]
    public async Task ListScheduledFiresAsync_ReturnsUpcomingUtcFires_SpacedByCron()
    {
        await using var db = TestDbFactory.Create();
        SeedScheduled(db, "Every2Min", FolderA, "0 0/2 * * * ?");
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListScheduledFiresAsync(AccessibleFolderSet.Unrestricted, null, 3, 25, CancellationToken.None);

        var forecast = result.Should().ContainSingle().Subject;
        forecast.WorkflowName.Should().Be("Every2Min");
        forecast.NextFiresUtc.Should().HaveCount(3);
        forecast.NextFiresUtc.Should().OnlyContain(f => f.Kind == DateTimeKind.Utc);
        forecast.NextFiresUtc.Should().BeInAscendingOrder();
        // "0 0/2 …" fires on even minutes → consecutive fires are 2 minutes apart.
        (forecast.NextFiresUtc[1] - forecast.NextFiresUtc[0]).Should().Be(TimeSpan.FromMinutes(2));
        forecast.NextFiresUtc[0].Should().BeAfter(DateTime.UtcNow.AddSeconds(-1));
    }

    [Fact]
    public async Task ListScheduledFiresAsync_ExcludesDisabledWorkflows()
    {
        await using var db = TestDbFactory.Create();
        SeedScheduled(db, "Off", FolderA, "0 0/2 * * * ?", enabled: false);
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListScheduledFiresAsync(AccessibleFolderSet.Unrestricted, null, 3, 25, CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListScheduledFiresAsync_ScopedByFolder_ExcludesInaccessible()
    {
        await using var db = TestDbFactory.Create();
        SeedScheduled(db, "mine", FolderA, "0 0/2 * * * ?");
        SeedScheduled(db, "hidden", FolderB, "0 0/2 * * * ?");
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListScheduledFiresAsync(Scoped(FolderA), null, 1, 25, CancellationToken.None);
        result.Should().ContainSingle().Which.WorkflowName.Should().Be("mine");
    }

    [Fact]
    public async Task ListScheduledFiresAsync_IdOrNameFilter_ReturnsOnlyThatWorkflow()
    {
        await using var db = TestDbFactory.Create();
        SeedScheduled(db, "Alpha", FolderA, "0 0/2 * * * ?");
        SeedScheduled(db, "Beta", FolderA, "0 0/5 * * * ?");
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListScheduledFiresAsync(AccessibleFolderSet.Unrestricted, "Beta", 1, 25, CancellationToken.None);
        result.Should().ContainSingle().Which.WorkflowName.Should().Be("Beta");
    }

    [Fact]
    public async Task ListScheduledFiresAsync_InvalidCron_IsSkipped()
    {
        await using var db = TestDbFactory.Create();
        SeedScheduled(db, "Broken", FolderA, "not-a-cron");
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListScheduledFiresAsync(AccessibleFolderSet.Unrestricted, null, 3, 25, CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListScheduledFiresAsync_NonScheduleTrigger_ProducesNoForecast()
    {
        await using var db = TestDbFactory.Create();
        var def = """{"nodes":[{"id":"t1","type":"activity","data":{"activityType":"manualTrigger","config":{}}}],"edges":[]}""";
        var wf = SeedWorkflow(db, "Manual", FolderA, def);
        wf.IsEnabled = true;
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListScheduledFiresAsync(AccessibleFolderSet.Unrestricted, null, 3, 25, CancellationToken.None);
        result.Should().BeEmpty();
    }

    // ---- Machines ------------------------------------------------------------------------------

    [Fact]
    public async Task ListMachinesAsync_ReturnsHostFacts_WithoutCredentials()
    {
        await using var db = TestDbFactory.Create();
        db.ManagedMachines.Add(new ManagedMachine
        {
            Id = Guid.NewGuid(), Name = "web01", Hostname = "web01.corp", WinRmPort = 5986, UseSsl = true,
            IsReachable = true,
        });
        await db.SaveChangesAsync();

        var machines = await NewReader(db).ListMachinesAsync(50, CancellationToken.None);

        var m = machines.Should().ContainSingle().Subject;
        m.Name.Should().Be("web01");
        m.Hostname.Should().Be("web01.corp");
        m.IsReachable.Should().BeTrue();
        // The DTO carries no credential field at all — credentials can't leak by construction.
    }
}
