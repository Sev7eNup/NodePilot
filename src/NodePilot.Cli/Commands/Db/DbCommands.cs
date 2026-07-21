using System.ComponentModel;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Db;

// `np db info` / `np db query` — admin-only access to the DbAdmin SQL console.
// Read-mode is default; --write opts into a mutation (gated server-side by
// DbAdmin:AllowWriteQueries plus an X-Confirm-Write header sent by the client).
//
// The CLI is the only non-UI surface that needs to reach this endpoint — useful
// for one-off operational queries from a shell, or for shipping a script with
// remediation SQL during incident response.

[SupportedOSPlatform("windows")]
public sealed class DbInfoCommand : BaseCommand<GlobalSettings>
{
    public DbInfoCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var info = await api.GetDbAdminInfoAsync(ct);
        writer.WriteData(info, (console, i) =>
        {
            var t = new Table().Border(TableBorder.Rounded).HideHeaders();
            t.AddColumn(""); t.AddColumn("");
            t.AddRow("Provider", Markup.Escape(i.Provider));
            t.AddRow("AllowWriteQueries", i.AllowWriteQueries ? "true" : "false");
            t.AddRow("QueryTimeoutSeconds", i.QueryTimeoutSeconds.ToString());
            t.AddRow("QueryMaxRows", i.QueryMaxRows.ToString());
            console.Write(t);
        });
        return ExitCodes.Success;
    }
}

public sealed class DbQuerySettings : GlobalSettings
{
    [CommandOption("--sql <SQL>")]
    [Description("SQL statement to execute. Use --file to load from disk instead.")]
    public string? Sql { get; set; }

    [CommandOption("--file <PATH>")]
    [Description("Read the SQL statement from a file (UTF-8).")]
    public string? File { get; set; }

    [CommandOption("--write")]
    [Description("Switch to write mode. Requires DbAdmin:AllowWriteQueries=true on the server.")]
    public bool Write { get; set; }

    [CommandOption("--yes")]
    [Description("Skip the interactive confirmation prompt in write mode (for scripts).")]
    public bool Yes { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class DbQueryCommand : BaseCommand<DbQuerySettings>
{
    public DbQueryCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, DbQuerySettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var sql = ResolveSql(settings);
        if (string.IsNullOrWhiteSpace(sql))
        {
            writer.Error("--sql <SQL> oder --file <PATH> ist Pflicht.");
            return ExitCodes.Error;
        }

        if (settings.Write && !settings.Yes && !Console.IsInputRedirected)
        {
            // Interactive guard — same idea as the UI's "type ALLOW WRITE" gesture, only
            // shaped for a CLI. Non-interactive shells (CI, piped stdin) skip this and rely
            // on `--yes` being passed explicitly.
            AnsiConsole.MarkupLine("[yellow]Write mode rewrites database state. Continue?[/]");
            if (!AnsiConsole.Confirm("Run the statement?", defaultValue: false))
            {
                writer.Error("Aborted.");
                return ExitCodes.Error;
            }
        }

        var api = ClientFactory.Create(session);
        var resp = await api.ExecuteDbAdminQueryAsync(sql, settings.Write, ct);

        writer.WriteData(resp, (console, r) =>
        {
            if (r.RowsAffected.HasValue)
                console.MarkupLine($"[green]{r.RowsAffected} row(s) affected[/] in {r.DurationMs} ms ({r.Mode}).");

            if (r.Columns.Count == 0)
            {
                console.MarkupLine($"[grey](no result set, {r.DurationMs} ms)[/]");
                return;
            }

            var t = new Table().Border(TableBorder.Rounded);
            foreach (var col in r.Columns) t.AddColumn(Markup.Escape($"{col.Name} ({col.Type})"));
            foreach (var row in r.Rows)
            {
                var cells = new string[row.Count];
                for (var i = 0; i < row.Count; i++) cells[i] = Markup.Escape(FormatCell(row[i]));
                t.AddRow(cells);
            }
            console.Write(t);
            console.MarkupLine($"[grey]{r.Rows.Count} row(s){(r.Truncated ? " [yellow](truncated)[/]" : "")} in {r.DurationMs} ms.[/]");
        });
        return ExitCodes.Success;
    }

    private static string ResolveSql(DbQuerySettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.File))
            return File.ReadAllText(settings.File);
        return settings.Sql ?? string.Empty;
    }

    /// <summary>
    /// JsonElement cells in the row payload need a short, human-readable representation —
    /// numbers/strings render as themselves, objects/arrays fall back to their JSON form.
    /// </summary>
    private static string FormatCell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "NULL",
        JsonValueKind.Undefined => "",
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText(),
    };
}
