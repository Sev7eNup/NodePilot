using System.Text.Json;
using FluentAssertions;
using NodePilot.Ai.Knowledge;
using NodePilot.Core.Interfaces;
using Xunit;

namespace NodePilot.Ai.Tests.Knowledge;

public class KnowledgeChatToolRegistryTests
{
    // Minimal in-test readers — the registry's gating is over flags/privilege, not file IO.
    private sealed class FakeDocs : IDocsKnowledgeReader
    {
        public bool Available { get; set; } = true;
        public bool IsAvailable() => Available;
        public IReadOnlyList<KnowledgeSearchHit> Search(string query) => new[] { new KnowledgeSearchHit("getting-started.md", "…snippet…") };
        public KnowledgeFileResult Read(string relPath) => KnowledgeFileResult.Success(relPath, "# Doc");
    }

    private sealed class FakeSource : ISourceCodeKnowledgeReader
    {
        public bool Available { get; set; } = true;
        public bool IsAvailable() => Available;
        public IReadOnlyList<KnowledgeSearchHit> Search(string query) => new[] { new KnowledgeSearchHit("src/Program.cs", "…code…") };
        public KnowledgeFileResult Read(string relPath) => KnowledgeFileResult.Success(relPath, "class Program {}");
    }

    private sealed class FakeSettings : ISettingsKnowledgeReader
    {
        public List<SettingsSectionKnowledge> Sections { get; } = new()
        {
            new("Engine", "Engine Concurrency", false,
                JsonDocument.Parse("""{"runspace":{"minRunspaces":256,"maxRunspaces":768}}""").RootElement.Clone()),
            new("Smtp", "SMTP", true,
                JsonDocument.Parse("""{"host":"localhost","password":"********"}""").RootElement.Clone()),
        };
        public IReadOnlyList<SettingsSectionKnowledge> GetRedactedSnapshot() => Sections;
    }

    private sealed class FakeSql : ISqlKnowledgeReader
    {
        public string Provider { get; set; } = "postgres";
        public List<DbTableKnowledgeSummary> Tables { get; set; } = new();
        public DbTableKnowledgeDetail? TableDetail { get; set; }
        public SqlQueryKnowledgeResult QueryResult { get; set; } = new(Array.Empty<string>(), Array.Empty<IReadOnlyList<string?>>(), false, 0, null);
        public string? LastSql { get; private set; }
        public string? LastName { get; private set; }
        public Task<IReadOnlyList<DbTableKnowledgeSummary>> ListTablesAsync(CancellationToken ct)
        { LastSql = null; return Task.FromResult<IReadOnlyList<DbTableKnowledgeSummary>>(Tables); }
        public Task<DbTableKnowledgeDetail?> GetTableAsync(string name, CancellationToken ct)
        { LastName = name; return Task.FromResult(TableDetail); }
        public Task<SqlQueryKnowledgeResult> ExecuteReadAsync(string sql, CancellationToken ct)
        { LastSql = sql; return Task.FromResult(QueryResult); }
    }

    private static KnowledgeChatToolRegistry Registry(out FakeDocs docs, out FakeSource src, out FakeSql sql)
    {
        docs = new FakeDocs();
        src = new FakeSource();
        sql = new FakeSql();
        return new KnowledgeChatToolRegistry(docs, src);
    }

    private static KnowledgeToolContext Ctx(
        bool docs = true, bool op = true, bool src = true, bool priv = true, bool db = true,
        FakeOperationalKnowledgeReader? opReader = null, ISettingsKnowledgeReader? settings = null,
        ISqlKnowledgeReader? sql = null)
        => new(AccessibleFolderSet.Unrestricted, priv, docs, op, src, db,
               op ? (opReader ?? new FakeOperationalKnowledgeReader()) : null,
               priv ? (settings ?? new FakeSettings()) : null,
               db && priv ? (sql ?? new FakeSql()) : null);

    private static HashSet<string> ToolNames(KnowledgeChatToolRegistry reg, KnowledgeToolContext ctx)
        => reg.GetTools(ctx).Select(t => t.Name).ToHashSet();

    // ---- GetTools gating matrix ----------------------------------------------------------------

    [Fact]
    public void GetTools_AllEnabledPrivileged_ExposesEveryTool()
    {
        var reg = Registry(out _, out _, out _);
        var names = ToolNames(reg, Ctx());
        names.Should().Contain(new[]
        {
            "search_docs", "read_doc",
            "get_workflow_definition", "analyze_workflow",
            "get_next_scheduled_fires",
            "search_source", "read_source",
            "read_settings",
            "list_db_tables", "get_db_table", "execute_readonly_sql",
        });
        // The four list tools (workflows / executions / machines) were retired in favour of the
        // database text2sql tools — they must never be offered, even with everything enabled.
        names.Should().NotContain(new[] { "list_workflows", "list_recent_executions", "list_workflow_executions", "list_machines" });
    }

    [Fact]
    public void GetTools_DocsDisabled_OmitsDocTools()
    {
        var reg = Registry(out _, out _, out _);
        var names = ToolNames(reg, Ctx(docs: false));
        names.Should().NotContain("search_docs").And.NotContain("read_doc");
        names.Should().Contain("get_workflow_definition");
    }

    [Fact]
    public void GetTools_OperationalDisabled_OmitsAllOperationalTools()
    {
        var reg = Registry(out _, out _, out _);
        var names = ToolNames(reg, Ctx(op: false));
        names.Should().NotContain(new[] { "get_workflow_definition", "analyze_workflow", "get_next_scheduled_fires" });
        names.Should().Contain("search_docs");
    }

    [Fact]
    public void GetTools_OperationalButNotPrivileged_HidesWorkflowContentTools()
    {
        var reg = Registry(out _, out _, out _);
        var names = ToolNames(reg, Ctx(priv: false, src: false));
        // Non-privileged keeps the non-sensitive operational tool (next-fire forecast) but NOT the
        // workflow-content tools (definition/analysis need privilege).
        names.Should().Contain("get_next_scheduled_fires");
        names.Should().NotContain("get_workflow_definition").And.NotContain("analyze_workflow");
    }

    [Fact]
    public void GetTools_SourceRequiresEnabledAndPrivilege()
    {
        var reg = Registry(out _, out _, out _);
        ToolNames(reg, Ctx(src: true, priv: true)).Should().Contain("search_source").And.Contain("read_source");
        ToolNames(reg, Ctx(src: false, priv: true)).Should().NotContain("search_source");
        ToolNames(reg, Ctx(src: true, priv: false)).Should().NotContain("search_source");
    }

    [Fact]
    public void GetTools_ReadSettings_RequiresPrivilege()
    {
        var reg = Registry(out _, out _, out _);
        // Available to Admin/Operator regardless of the docs/operational/source toggles …
        ToolNames(reg, Ctx(docs: false, op: false, src: false, priv: true)).Should().Contain("read_settings");
        // … and never to a non-privileged (Viewer) session.
        ToolNames(reg, Ctx(priv: false, src: false)).Should().NotContain("read_settings");
    }

    // ---- ExecuteAsync gating (defense in depth) ------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_UnknownTool_ReturnsErrorJson()
    {
        var reg = Registry(out _, out _, out _);
        var r = await reg.ExecuteAsync("delete_everything", "{}", Ctx(), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_GatedOffTool_ReturnsErrorWithoutTouchingReader()
    {
        var reg = Registry(out _, out _, out _);
        // search_source is not permitted when source is disabled — even if the model calls it.
        var r = await reg.ExecuteAsync("search_source", """{"query":"x"}""", Ctx(src: false), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().Contain("nicht verfügbar");
    }

    [Fact]
    public async Task ExecuteAsync_WorkflowContentTool_NotPrivileged_IsDenied()
    {
        var reg = Registry(out _, out _, out _);
        var r = await reg.ExecuteAsync("get_workflow_definition", """{"idOrName":"wf"}""", Ctx(priv: false, src: false), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().Contain("nicht verfügbar");
    }

    [Fact]
    public async Task ExecuteAsync_ReadSettings_NotPrivileged_IsDenied()
    {
        var reg = Registry(out _, out _, out _);
        var r = await reg.ExecuteAsync("read_settings", "{}", Ctx(priv: false, src: false), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().Contain("nicht verfügbar");
    }

    // ---- ExecuteAsync read_settings ------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ReadSettings_ReturnsRedactedSections()
    {
        var reg = Registry(out _, out _, out _);
        var r = await reg.ExecuteAsync("read_settings", "{}", Ctx(), CancellationToken.None);
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
        // Values are embedded as nested JSON (not string-escaped) and keep the masked secret.
        r.Should().Contain("minRunspaces").And.Contain("********");
        doc.RootElement.GetProperty("sections")[0].GetProperty("values").GetProperty("runspace")
            .GetProperty("minRunspaces").GetInt32().Should().Be(256);
    }

    [Fact]
    public async Task ExecuteAsync_ReadSettings_SectionFilter_NarrowsResult()
    {
        var reg = Registry(out _, out _, out _);
        var r = await reg.ExecuteAsync("read_settings", """{"section":"Engine"}""", Ctx(), CancellationToken.None);
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("sections")[0].GetProperty("section").GetString().Should().Be("Engine");
    }

    // ---- ExecuteAsync happy paths --------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_SearchDocs_ReturnsHits()
    {
        var reg = Registry(out _, out _, out _);
        var r = await reg.ExecuteAsync("search_docs", """{"query":"trigger"}""", Ctx(), CancellationToken.None);
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("hits")[0].GetProperty("path").GetString().Should().Be("getting-started.md");
    }

    [Fact]
    public async Task ExecuteAsync_SearchDocs_RootUnavailable_ReturnsError()
    {
        var reg = Registry(out var docs, out _, out _);
        docs.Available = false;
        var r = await reg.ExecuteAsync("search_docs", """{"query":"x"}""", Ctx(), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().Contain("nicht verfügbar");
    }

    [Fact]
    public async Task ExecuteAsync_GetWorkflowDefinition_PassesNameAndReturnsDetail()
    {
        var reg = Registry(out _, out _, out _);
        var op = new FakeOperationalKnowledgeReader
        {
            Definition = new WorkflowKnowledgeDetail(Guid.NewGuid(), "Nightly Backup", "desc", true, """{"nodes":[],"edges":[]}"""),
        };
        var r = await reg.ExecuteAsync("get_workflow_definition", """{"idOrName":"Nightly Backup"}""", Ctx(opReader: op), CancellationToken.None);

        op.LastIdOrName.Should().Be("Nightly Backup");
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("name").GetString().Should().Be("Nightly Backup");
        doc.RootElement.GetProperty("definition").GetString().Should().Contain("nodes");
    }

    [Fact]
    public async Task ExecuteAsync_GetWorkflowDefinition_NotFound_ReturnsError()
    {
        var reg = Registry(out _, out _, out _);
        var op = new FakeOperationalKnowledgeReader { Definition = null };
        var r = await reg.ExecuteAsync("get_workflow_definition", """{"idOrName":"ghost"}""", Ctx(opReader: op), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_AnalyzeWorkflow_RunsAnalyzerOnRedactedDefinition()
    {
        var reg = Registry(out _, out _, out _);
        var op = new FakeOperationalKnowledgeReader
        {
            // A trigger + an orphan (unconnected) activity → the analyzer flags the orphan.
            Definition = new WorkflowKnowledgeDetail(Guid.NewGuid(), "wf", null, true, """
                {"nodes":[
                  {"id":"t1","type":"activity","data":{"activityType":"scheduleTrigger","config":{}}},
                  {"id":"lonely","type":"activity","data":{"activityType":"log","config":{}}}
                ],"edges":[]}
                """),
        };
        var r = await reg.ExecuteAsync("analyze_workflow", """{"idOrName":"wf"}""", Ctx(opReader: op), CancellationToken.None);

        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        r.Should().Contain("orphan-node");
    }

    [Fact]
    public async Task ExecuteAsync_GetNextScheduledFires_PassesArgsAndReturnsUtcSchedules()
    {
        var reg = Registry(out _, out _, out _);
        var op = new FakeOperationalKnowledgeReader();
        op.ScheduledFires.Add(new ScheduledFireForecast(
            Guid.NewGuid(), "Nightly Backup", "0 0 2 * * ?", "at 02:00",
            new[] { new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc) }));

        var r = await reg.ExecuteAsync("get_next_scheduled_fires", """{"idOrName":"Nightly Backup","count":3}""", Ctx(opReader: op), CancellationToken.None);

        op.LastIdOrName.Should().Be("Nightly Backup");
        op.LastPerWorkflow.Should().Be(3);
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("schedules")[0].GetProperty("workflowName").GetString().Should().Be("Nightly Backup");
        // Serialized UTC instant must carry the 'Z' designator so the model reads it as UTC.
        doc.RootElement.GetProperty("schedules")[0].GetProperty("nextFiresUtc")[0].GetString().Should().EndWith("Z");
    }

    [Fact]
    public async Task ExecuteAsync_GetNextScheduledFires_NotPrivileged_StillAllowed()
    {
        var reg = Registry(out _, out _, out _);
        var op = new FakeOperationalKnowledgeReader();
        // Schedule times are non-privileged operational data (like list_workflows) — a Viewer may ask.
        var r = await reg.ExecuteAsync("get_next_scheduled_fires", "{}", Ctx(priv: false, src: false, opReader: op), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.TryGetProperty("error", out _).Should().BeFalse();
    }

    // ---- SQL (text2sql) tools ------------------------------------------------------------------

    [Fact]
    public void GetTools_DbRequiresEnabledAndPrivilegeAndReader()
    {
        var reg = Registry(out _, out _, out _);
        // All three: toggle on, privileged, reader present.
        ToolNames(reg, Ctx(db: true, priv: true)).Should().Contain("execute_readonly_sql");
        // Toggle off → no DB tools, even for an admin.
        ToolNames(reg, Ctx(db: false, priv: true)).Should().NotContain("execute_readonly_sql").And.NotContain("list_db_tables").And.NotContain("get_db_table");
        // Non-privileged (Viewer) → never, even with toggle on.
        ToolNames(reg, Ctx(db: true, priv: false)).Should().NotContain("execute_readonly_sql");
        // Reader absent (service mis-wired) → gate stays closed.
        var ctx = new KnowledgeToolContext(AccessibleFolderSet.Unrestricted, true, true, true, true, true, new FakeOperationalKnowledgeReader(), new FakeSettings(), Sql: null);
        ToolNames(reg, ctx).Should().NotContain("execute_readonly_sql");
    }

    [Fact]
    public async Task ExecuteAsync_ListDbTables_DelegatesToReader()
    {
        var reg = Registry(out _, out _, out var sql);
        sql.Tables = new List<DbTableKnowledgeSummary>
        {
            new("Workflow", "Workflows", Array.Empty<string>(), new[] { "Id", "Name" }),
        };
        var r = await reg.ExecuteAsync("list_db_tables", "{}", Ctx(sql: sql), CancellationToken.None);
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("provider").GetString().Should().Be("postgres");
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("tables")[0].GetProperty("dbTableName").GetString().Should().Be("Workflows");
    }

    [Fact]
    public async Task ExecuteAsync_GetDbTable_NotFound_ReturnsError()
    {
        var reg = Registry(out _, out _, out var sql);
        sql.TableDetail = null;
        var r = await reg.ExecuteAsync("get_db_table", """{"name":"Nope"}""", Ctx(sql: sql), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().Contain("Nope");
    }

    [Fact]
    public async Task ExecuteAsync_ExecuteReadonlySql_PassesSqlAndReturnsRedactedRows()
    {
        var reg = Registry(out _, out _, out var sql);
        sql.QueryResult = new SqlQueryKnowledgeResult(
            new[] { "Name", "PasswordHash" },
            new IReadOnlyList<string?>[] { new[] { "admin", "***" } },
            false, 42, null);
        var r = await reg.ExecuteAsync("execute_readonly_sql", """{"sql":"SELECT Name, PasswordHash FROM Users"}""", Ctx(sql: sql), CancellationToken.None);
        sql.LastSql.Should().Be("SELECT Name, PasswordHash FROM Users");
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("columns")[0].GetString().Should().Be("Name");
        doc.RootElement.GetProperty("rows")[0].GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("rows")[0][1].GetString().Should().Be("***"); // reader already redacted
    }

    [Fact]
    public async Task ExecuteAsync_ExecuteReadonlySql_SqlError_SurfacesAsErrorField()
    {
        var reg = Registry(out _, out _, out var sql);
        sql.QueryResult = new SqlQueryKnowledgeResult(Array.Empty<string>(), Array.Empty<IReadOnlyList<string?>>(), false, 0, "syntax error near 'SELEC'");
        var r = await reg.ExecuteAsync("execute_readonly_sql", """{"sql":"SELEC * FROM Workflows"}""", Ctx(sql: sql), CancellationToken.None);
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("syntax error");
    }

    [Fact]
    public async Task ExecuteAsync_ExecuteReadonlySql_EmptySql_ReturnsError()
    {
        var reg = Registry(out _, out _, out var sql);
        var r = await reg.ExecuteAsync("execute_readonly_sql", """{"sql":""}""", Ctx(sql: sql), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        // Reader must not have been called for empty input.
        sql.LastSql.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ExecuteReadonlySql_NotPrivileged_IsDenied()
    {
        var reg = Registry(out _, out _, out var sql);
        var r = await reg.ExecuteAsync("execute_readonly_sql", """{"sql":"SELECT 1"}""", Ctx(db: true, priv: false, src: false), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().Contain("nicht verfügbar");
        sql.LastSql.Should().BeNull(); // gate stops execution before the reader.
    }

    [Fact]
    public async Task ExecuteAsync_OversizedSqlResult_ReturnsValidExplicitTruncationEnvelope()
    {
        var reg = Registry(out _, out _, out var sql);
        sql.QueryResult = new SqlQueryKnowledgeResult(
            new[] { "Payload" },
            Enumerable.Range(0, 200)
                .Select(_ => (IReadOnlyList<string?>)new[] { new string('x', 500) })
                .ToArray(),
            true, 1, null);

        var r = await reg.ExecuteAsync(
            "execute_readonly_sql", """{"sql":"SELECT Payload FROM LargeTable"}""",
            Ctx(sql: sql), CancellationToken.None);

        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("resultPreview").GetString().Should().NotBeNullOrEmpty();
        r.Length.Should().BeLessThan(AiKnowledgeOptions.MaxToolResultChars);
    }
}
