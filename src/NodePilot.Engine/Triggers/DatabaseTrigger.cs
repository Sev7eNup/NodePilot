using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Triggers;

namespace NodePilot.Engine.Triggers;

/// <summary>
/// Database trigger — the node-executor half of the trigger.
///
/// <para>When the orchestrator's poll loop fires the workflow, this node surfaces the sentinel
/// change it detected. On a manual run it executes the same query once and previews what the poll
/// loop would see: the sentinel value (first column of the first row) plus a sample of rows.</para>
///
/// <para>Config parsing, defaults, validation and connection resolution live in
/// <see cref="DatabaseTriggerSettings"/>, shared with
/// <c>NodePilot.Scheduler.Sources.DatabaseTriggerSource</c> — so the interval, provider and
/// connection rules an author sees here are the ones the poll loop actually applies.</para>
/// </summary>
public class DatabaseTrigger : IActivityExecutor
{
    private const int MaxPreviewRows = 100;

    private readonly IConfiguration _config;

    public string ActivityType => "databaseTrigger";

    public DatabaseTrigger(IConfiguration config)
    {
        _config = config;
    }

    public async Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        // If the orchestrator's polling source fired this trigger, it already ran the query and
        // just needs to surface the change-detection data to downstream steps.
        var orchestratorParams = TriggerVariables.ExtractManualParams(context.Variables);
        if (orchestratorParams.TryGetValue("dbSentinel", out var dbSentinel))
        {
            return new ActivityResult
            {
                Success = true,
                Output = $"Database trigger fired: {orchestratorParams.GetValueOrDefault("dbPrevious", "?")} → {dbSentinel}",
                OutputParameters = orchestratorParams,
            };
        }

        // Manual run: execute the query inline and preview what the poll loop would compare.
        DatabaseTriggerSettings settings;
        string connString;
        try
        {
            settings = DatabaseTriggerSettings.Parse(config);
            connString = settings.ResolveConnectionString(
                name => _config[$"Trigger:Database:Connections:{name}"],
                RequireConnectionRef());
        }
        catch (InvalidOperationException ex)
        {
            return new ActivityResult { Success = false, ErrorOutput = ex.Message };
        }

        try
        {
            await using var connection = CreateConnection(settings.Provider, connString);
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText = settings.Query;
            command.CommandTimeout = 30;

            await using var reader = await command.ExecuteReaderAsync(ct);

            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(ct) && rows.Count < MaxPreviewRows)
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            // The poll loop compares the first column of the first row against the previous poll.
            // Spell that out here so a manual run cannot leave the author believing the trigger
            // fires per returned row.
            var sentinel = rows.Count > 0 ? rows[0].Values.FirstOrDefault()?.ToString() ?? "" : "";
            var header =
                $"Poll interval: {settings.PollingIntervalSeconds}s (provider: {settings.Provider})\n" +
                $"Sentinel (first column of first row): {(rows.Count > 0 ? $"'{sentinel}'" : "(no rows)")}\n" +
                "The trigger fires when this value CHANGES between polls.\n" +
                $"Rows returned: {rows.Count}\n";

            if (rows.Count == 0)
                return new ActivityResult { Success = true, Output = header.TrimEnd() };

            var output = header + "\n" + JsonSerializer.Serialize(
                rows.Take(10), new JsonSerializerOptions { WriteIndented = true });

            return new ActivityResult { Success = true, Output = output };
        }
        catch (Exception ex)
        {
            return new ActivityResult { Success = false, ErrorOutput = $"Database error: {ex.Message}" };
        }
    }

    private static DbConnection CreateConnection(string provider, string connStr) => provider switch
    {
        "sqlite" => new SqliteConnection(connStr),
        _ => new SqlConnection(connStr),
    };

    private bool RequireConnectionRef()
    {
        var raw = _config["Trigger:Database:RequireConnectionRef"];
        return string.IsNullOrWhiteSpace(raw)
            || !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
    }
}
