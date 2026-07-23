using System.Text.Json;
using NodePilot.Core.Interfaces;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Ai.Knowledge;

/// <summary>
/// Per-request context for the knowledge tools. Carries the pre-resolved folder access, the
/// caller's privilege level, the live per-source toggles, and the (scoped) operational reader —
/// non-null only when operational data is enabled for this request. The docs/source readers are
/// singletons injected into the registry itself.
/// </summary>
public sealed record KnowledgeToolContext(
    AccessibleFolderSet Accessible,
    bool IsPrivileged,
    bool DocsEnabled,
    bool OperationalEnabled,
    bool SourceCodeEnabled,
    bool DbEnabled,
    IOperationalKnowledgeReader? Operational,
    ISettingsKnowledgeReader? Settings,
    ISqlKnowledgeReader? Sql);

/// <summary>Read-only tool registry for the global "AI Chat" knowledge assistant.</summary>
public interface IKnowledgeToolRegistry
{
    /// <summary>The tool definitions offered to the LLM, filtered by which knowledge sources are enabled/permitted.</summary>
    IReadOnlyList<LlmToolDefinition> GetTools(KnowledgeToolContext context);

    /// <summary>Executes a tool and returns its result as a JSON string. Every gate is re-checked here
    /// (defense-in-depth); unknown/disallowed tools and errors return <c>{ "error": … }</c>.</summary>
    Task<string> ExecuteAsync(string name, string argumentsJson, KnowledgeToolContext context, CancellationToken ct);
}

/// <summary>
/// Source-gated tool registry. Docs tools require <see cref="KnowledgeToolContext.DocsEnabled"/>;
/// operational read tools require the operational reader; workflow-<b>content</b> tools
/// (<c>get_workflow_definition</c>, <c>analyze_workflow</c>) additionally require
/// <see cref="KnowledgeToolContext.IsPrivileged"/>; source-code tools require
/// <see cref="KnowledgeToolContext.SourceCodeEnabled"/> and privilege. Stateless singleton — the
/// per-request bits live in the context; the docs/source readers are injected singletons.
/// </summary>
public sealed class KnowledgeChatToolRegistry : IKnowledgeToolRegistry
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonElement NoParams = ParseParams("""{"type":"object","properties":{}}""");

    private delegate Task<object> Handler(JsonElement args, KnowledgeToolContext ctx, CancellationToken ct);
    private sealed record Tool(LlmToolDefinition Def, Handler Run, Func<KnowledgeToolContext, bool> Gate);

    private readonly Dictionary<string, Tool> _tools;

    private static bool DocsGate(KnowledgeToolContext c) => c.DocsEnabled;
    private static bool OpGate(KnowledgeToolContext c) => c.OperationalEnabled && c.Operational is not null;
    private static bool OpPrivGate(KnowledgeToolContext c) => OpGate(c) && c.IsPrivileged;
    private static bool SourceGate(KnowledgeToolContext c) => c.SourceCodeEnabled && c.IsPrivileged;
    private static bool SettingsGate(KnowledgeToolContext c) => c.IsPrivileged && c.Settings is not null;
    private static bool SqlGate(KnowledgeToolContext c) => c.DbEnabled && c.IsPrivileged && c.Sql is not null;

    public KnowledgeChatToolRegistry(IDocsKnowledgeReader docs, ISourceCodeKnowledgeReader source)
    {
        _tools = new Dictionary<string, Tool>(StringComparer.Ordinal)
        {
            ["search_docs"] = new(
                new LlmToolDefinition("search_docs",
                    "Durchsucht die NodePilot-Produktdokumentation (Keyword) und liefert die relevantesten Doku-Dateien "
                    + "mit Kontext-Snippet. Nutze es für How-to-/Konzept-/Feature-Fragen zu NodePilot.",
                    StringParam("query", "Suchbegriffe (Stichworte).")),
                (a, _, _) => Task.FromResult(SearchDocs(docs, a)),
                DocsGate),

            ["read_doc"] = new(
                new LlmToolDefinition("read_doc",
                    "Liest ein komplettes Doku-Dokument (Pfad aus search_docs, z.B. 'concepts/triggers.md').",
                    StringParam("path", "Relativer Doku-Pfad aus search_docs.", required: true)),
                (a, _, _) => Task.FromResult(ReadFile(docs.Read(GetString(a, "path")))),
                DocsGate),

            ["list_workflows"] = new(
                new LlmToolDefinition("list_workflows",
                    "Listet die im System eingespielten Workflows (nur die der User sehen darf), optional nach "
                    + "Namensteil gefiltert: Name, aktiviert?, Aktivitätenzahl, Trigger-Typen.",
                    ParseParams("""
                        {"type":"object","properties":{
                          "nameFilter":{"type":"string","description":"Optionaler Namensteil-Filter."},
                          "take":{"type":"integer","minimum":1,"maximum":50,"description":"Max. Anzahl (Default 25)."}}}
                        """)),
                ListWorkflowsAsync, OpGate),

            ["get_workflow_definition"] = new(
                new LlmToolDefinition("get_workflow_definition",
                    "Liefert die (secret-redigierte) Definition EINES Workflows (nodes/edges/config) per Name oder ID — "
                    + "die Basis, um zu erklären, WAS ein Workflow tut und WIE er aufgebaut ist.",
                    StringParam("idOrName", "Workflow-Name oder GUID.", required: true)),
                GetWorkflowDefinitionAsync, OpPrivGate),

            ["analyze_workflow"] = new(
                new LlmToolDefinition("analyze_workflow",
                    "Deterministische Static-Analyse eines installierten Workflows (per Name/ID): fehlender Trigger, "
                    + "Orphan-Steps, Zyklen, Remote-Steps ohne Ziel-Maschine. Rufe es bei 'prüfe Workflow X'.",
                    StringParam("idOrName", "Workflow-Name oder GUID.", required: true)),
                AnalyzeWorkflowAsync, OpPrivGate),

            ["list_recent_executions"] = new(
                new LlmToolDefinition("list_recent_executions",
                    "Jüngste Ausführungen über die ganze Instanz (folder-gescoped), optional nach Status gefiltert "
                    + "(z.B. 'Failed' für Fehlschläge). Status/Zeiten/Fehlermeldung sind redigiert.",
                    ParseParams("""
                        {"type":"object","properties":{
                          "status":{"type":"string","description":"Optional: Pending|Running|Succeeded|Failed|Cancelled|Paused."},
                          "take":{"type":"integer","minimum":1,"maximum":50,"description":"Max. Anzahl (Default 20)."}}}
                        """)),
                ListRecentExecutionsAsync, OpGate),

            ["list_workflow_executions"] = new(
                new LlmToolDefinition("list_workflow_executions",
                    "Jüngste Läufe EINES bestimmten Workflows (per Name/ID) — für 'warum schlägt Workflow X fehl?'.",
                    ParseParams("""
                        {"type":"object","properties":{
                          "idOrName":{"type":"string","description":"Workflow-Name oder GUID."},
                          "take":{"type":"integer","minimum":1,"maximum":50,"description":"Max. Anzahl (Default 20)."}},
                          "required":["idOrName"]}
                        """)),
                ListWorkflowExecutionsAsync, OpGate),

            ["list_machines"] = new(
                new LlmToolDefinition("list_machines",
                    "Listet die verwalteten Ziel-Maschinen (Name, Host, WinRM-Port, Erreichbarkeit) — nie Credentials.",
                    NoParams),
                ListMachinesAsync, OpGate),

            ["get_next_scheduled_fires"] = new(
                new LlmToolDefinition("get_next_scheduled_fires",
                    "Berechnet die nächsten geplanten Ausführungszeitpunkte (UTC) aktivierter Workflows mit "
                    + "scheduleTrigger — die verlässliche Quelle für 'wann läuft der/ein Workflow als Nächstes'. "
                    + "Nicht aus vergangenen Läufen raten. Optional per idOrName auf einen Workflow eingrenzen.",
                    ParseParams("""
                        {"type":"object","properties":{
                          "idOrName":{"type":"string","description":"Optional: Workflow-Name oder GUID, um nur dessen Fires zu liefern."},
                          "count":{"type":"integer","minimum":1,"maximum":5,"description":"Fires pro Workflow (Default 3)."}}}
                        """)),
                GetNextScheduledFiresAsync, OpGate),

            ["search_source"] = new(
                new LlmToolDefinition("search_source",
                    "Durchsucht den NodePilot-Quellcode (Keyword) und liefert die relevantesten Dateien mit Snippet. "
                    + "Für Fragen, WIE etwas im Code implementiert ist.",
                    StringParam("query", "Suchbegriffe (Symbol/Stichworte).")),
                (a, _, _) => Task.FromResult(SearchSource(source, a)),
                SourceGate),

            ["read_source"] = new(
                new LlmToolDefinition("read_source",
                    "Liest eine komplette Quellcode-Datei (Pfad aus search_source, z.B. 'src/NodePilot.Api/Program.cs').",
                    StringParam("path", "Relativer Quellcode-Pfad aus search_source.", required: true)),
                (a, _, _) => Task.FromResult(ReadFile(source.Read(GetString(a, "path")))),
                SourceGate),

            ["list_db_tables"] = new(
                new LlmToolDefinition("list_db_tables",
                    "Listet die Tabellen der NodePilot-App-Datenbank mit ihren (nicht versteckten) Spalten — "
                    + "die Basis für SQL-Fragen. Jede Tabelle nennt ihren echten DB-Tabellennamen (DbTableName), "
                    + "den du in SQL verwenden musst, Primärschlüssel und Spaltennamen. Secret-Spalten fehlen.",
                    NoParams),
                ListDbTablesAsync, SqlGate),

            ["get_db_table"] = new(
                new LlmToolDefinition("get_db_table",
                    "Liefert die vollständige Spaltenliste einer Tabelle (Name, Typ, Nullable, Primärschlüssel) "
                    + "per Entity-Name oder DbTableName — für gezielte Joins/Filter. Secret-Spalten fehlen.",
                    StringParam("name", "Tabellenname (Entity-Name oder DbTableName).", required: true)),
                GetDbTableAsync, SqlGate),

            ["execute_readonly_sql"] = new(
                new LlmToolDefinition("execute_readonly_sql",
                    "Führt EIN einzelnes Read-Only-SQL-Statement gegen die NodePilot-App-DB aus (nur "
                    + "SELECT/WITH/EXPLAIN/SHOW/VALUES/TABLE — Schreibvorgänge werden serverseitig abgelehnt). "
                    + "Nutze DbTableName aus list_db_tables. Result-Spalten, die Secret-Spalten heißen, sind "
                    + "redigiert (\"***\") — wähle sie nicht aktiv. Fehler (Bad SQL/Timeout) kommen als Error-Feld zurück.",
                    StringParam("sql", "Ein einzelnes Read-Only-SQL-Statement.", required: true)),
                ExecuteReadonlySqlAsync, SqlGate),

            ["read_settings"] = new(
                new LlmToolDefinition("read_settings",
                    "Liefert die aktuelle NodePilot-Systemkonfiguration (Admin-Einstellungen, secret-redigiert) je "
                    + "Sektion als JSON — die EFFEKTIVEN Werte inkl. Runtime-Overrides: u.a. Engine/Runspaces, "
                    + "Retention, Logging, Remote/WinRM, Threading, Authentifizierung. Die verlässliche Quelle für "
                    + "Fragen nach eingestellten Werten oder Defaults ('wie viele Runspaces werden beim Start vorab "
                    + "allokiert', 'welches Log-Format', 'wie lange werden Executions aufbewahrt') — nicht raten. "
                    + "Optional per 'section' auf eine Sektion eingrenzen.",
                    ParseParams("""
                        {"type":"object","properties":{
                          "section":{"type":"string","description":"Optionaler Sektions-Teilname (z.B. 'Engine', 'Retention', 'Remote'). Leer = alle Sektionen."}}}
                        """)),
                (a, ctx, _) => Task.FromResult(ReadSettings(ctx, a)),
                SettingsGate),
        };
    }

    public IReadOnlyList<LlmToolDefinition> GetTools(KnowledgeToolContext context) =>
        _tools.Values.Where(t => t.Gate(context)).Select(t => t.Def).ToList();

    public async Task<string> ExecuteAsync(string name, string argumentsJson, KnowledgeToolContext context, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var tool))
            return Error($"Unbekanntes Tool: {name}");
        if (!tool.Gate(context))
            return Error($"Tool '{name}' ist in dieser Sitzung nicht verfügbar.");

        try
        {
            JsonElement args;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                args = doc.RootElement.Clone();
            }
            catch (JsonException) { args = NoParams; }

            var result = await tool.Run(args, context, ct);
            return Truncate(JsonSerializer.Serialize(result, Json));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    // ---- docs / source ------------------------------------------------------------------------

    private static object SearchDocs(IDocsKnowledgeReader docs, JsonElement args)
    {
        if (!docs.IsAvailable()) return new { error = "Dokumentations-Quelle ist nicht verfügbar (Root-Pfad fehlt)." };
        var hits = docs.Search(GetString(args, "query"));
        return new { count = hits.Count, hits = hits.Select(h => new { path = h.Path, snippet = h.Snippet }).ToArray() };
    }

    private static object SearchSource(ISourceCodeKnowledgeReader source, JsonElement args)
    {
        if (!source.IsAvailable()) return new { error = "Quellcode-Quelle ist nicht verfügbar (Root-Pfad fehlt)." };
        var hits = source.Search(GetString(args, "query"));
        return new { count = hits.Count, hits = hits.Select(h => new { path = h.Path, snippet = h.Snippet }).ToArray() };
    }

    private static object ReadFile(KnowledgeFileResult result) =>
        result.Ok
            ? new { path = result.Path, content = result.Content }
            : new { error = result.Error };

    // ---- settings -----------------------------------------------------------------------------

    private static object ReadSettings(KnowledgeToolContext ctx, JsonElement args)
    {
        var filter = GetOptionalString(args, "section");
        IReadOnlyList<Core.Interfaces.SettingsSectionKnowledge> sections = ctx.Settings!.GetRedactedSnapshot();
        if (!string.IsNullOrWhiteSpace(filter))
            sections = sections
                .Where(s => s.Section.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        return new
        {
            note = "Secrets sind redigiert (\"********\"). Werte sind die effektive Konfiguration inkl. Runtime-Overrides.",
            count = sections.Count,
            sections = sections.Select(s => new
            {
                section = s.Section,
                displayName = s.DisplayName,
                hotReloadable = s.HotReloadable,
                values = s.Values,
            }).ToArray(),
        };
    }

    // ---- operational --------------------------------------------------------------------------

    private static async Task<object> ListWorkflowsAsync(JsonElement args, KnowledgeToolContext ctx, CancellationToken ct)
    {
        var items = await ctx.Operational!.ListWorkflowsAsync(ctx.Accessible, GetOptionalString(args, "nameFilter"), GetIntOr(args, "take", 25), ct);
        return new { count = items.Count, workflows = items };
    }

    private static async Task<object> GetWorkflowDefinitionAsync(JsonElement args, KnowledgeToolContext ctx, CancellationToken ct)
    {
        var detail = await ctx.Operational!.GetWorkflowDefinitionAsync(ctx.Accessible, GetString(args, "idOrName"), ct);
        return detail is null
            ? new { error = "Workflow nicht gefunden, mehrdeutig oder keine Berechtigung." }
            : new
            {
                id = detail.Id,
                name = detail.Name,
                description = detail.Description,
                isEnabled = detail.IsEnabled,
                definition = detail.RedactedDefinitionJson,
            };
    }

    private static async Task<object> AnalyzeWorkflowAsync(JsonElement args, KnowledgeToolContext ctx, CancellationToken ct)
    {
        var detail = await ctx.Operational!.GetWorkflowDefinitionAsync(ctx.Accessible, GetString(args, "idOrName"), ct);
        if (detail is null)
            return new { error = "Workflow nicht gefunden, mehrdeutig oder keine Berechtigung." };
        try
        {
            using var doc = JsonDocument.Parse(detail.RedactedDefinitionJson);
            var findings = WorkflowReviewAnalyzer.Analyze(doc.RootElement);
            return new
            {
                workflow = detail.Name,
                ok = findings.Count == 0,
                count = findings.Count,
                findings = findings.Select(f => new
                {
                    severity = f.Severity.ToString().ToLowerInvariant(),
                    code = f.Code,
                    message = f.Message,
                    nodeId = f.NodeId,
                }).ToArray(),
            };
        }
        catch (JsonException)
        {
            return new { error = "Workflow-Definition ist kein gültiges JSON." };
        }
    }

    private static async Task<object> ListRecentExecutionsAsync(JsonElement args, KnowledgeToolContext ctx, CancellationToken ct)
    {
        var items = await ctx.Operational!.ListRecentExecutionsAsync(ctx.Accessible, GetOptionalString(args, "status"), GetIntOr(args, "take", 20), ct);
        return new { count = items.Count, executions = items };
    }

    private static async Task<object> ListWorkflowExecutionsAsync(JsonElement args, KnowledgeToolContext ctx, CancellationToken ct)
    {
        var items = await ctx.Operational!.GetWorkflowExecutionsAsync(ctx.Accessible, GetString(args, "idOrName"), GetIntOr(args, "take", 20), ct);
        return new { count = items.Count, executions = items };
    }

    private static async Task<object> ListMachinesAsync(JsonElement args, KnowledgeToolContext ctx, CancellationToken ct)
    {
        var items = await ctx.Operational!.ListMachinesAsync(50, ct);
        return new { count = items.Count, machines = items };
    }

    private static async Task<object> GetNextScheduledFiresAsync(JsonElement args, KnowledgeToolContext ctx, CancellationToken ct)
    {
        var items = await ctx.Operational!.ListScheduledFiresAsync(
            ctx.Accessible, GetOptionalString(args, "idOrName"), GetIntOr(args, "count", 3), 25, ct);
        return new { count = items.Count, note = "Alle Zeiten sind UTC.", schedules = items };
    }

    // ---- sql (text2sql) -----------------------------------------------------------------------

    private static async Task<object> ListDbTablesAsync(JsonElement args, KnowledgeToolContext ctx, CancellationToken ct)
    {
        var tables = await ctx.Sql!.ListTablesAsync(ct);
        return new { count = tables.Count, tables };
    }

    private static async Task<object> GetDbTableAsync(JsonElement args, KnowledgeToolContext ctx, CancellationToken ct)
    {
        var name = GetString(args, "name");
        var table = await ctx.Sql!.GetTableAsync(name, ct);
        return table is null
            ? new { error = $"Tabelle '{name}' nicht gefunden." }
            : new { table };
    }

    private static async Task<object> ExecuteReadonlySqlAsync(JsonElement args, KnowledgeToolContext ctx, CancellationToken ct)
    {
        var sql = GetString(args, "sql");
        if (string.IsNullOrWhiteSpace(sql))
            return new { error = "Parameter 'sql' fehlt." };
        var result = await ctx.Sql!.ExecuteReadAsync(sql, ct);
        return result.Error is null
            ? new { columns = result.Columns, rows = result.Rows, truncated = result.Truncated, durationMs = result.DurationMs }
            : new { error = result.Error, durationMs = result.DurationMs };
    }

    // ---- arg helpers --------------------------------------------------------------------------

    private static string GetString(JsonElement args, string name) => GetOptionalString(args, name) ?? "";

    private static string? GetOptionalString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var el)
        && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int GetIntOr(JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return fallback;
    }

    private static string Truncate(string json)
    {
        if (json.Length <= AiKnowledgeOptions.MaxToolResultChars) return json;
        return json[..AiKnowledgeOptions.MaxToolResultChars]
               + "…[Ergebnis gekürzt — enger fragen oder gezielter suchen]";
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json);

    private static JsonElement StringParam(string name, string description, bool required = false)
    {
        var desc = description.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var req = required ? $",\"required\":[\"{name}\"]" : "";
        return ParseParams(
            $"{{\"type\":\"object\",\"properties\":{{\"{name}\":{{\"type\":\"string\",\"description\":\"{desc}\"}}}}{req}}}");
    }

    private static JsonElement ParseParams(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
