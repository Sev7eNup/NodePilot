using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Triggers;

/// <summary>
/// Database trigger — polls a SQL query and fires when rows are returned.
/// Supports SQL Server. The query should return rows only when the trigger condition is met.
/// Example: SELECT * FROM Orders WHERE ProcessedAt IS NULL
///
/// Connection resolution mirrors the scheduler-side source (DatabaseTriggerSource):
/// preferred mode is <c>connectionRef</c> → name lookup under
/// <c>Trigger:Database:Connections:{name}</c>. Inline <c>connectionString</c> is only
/// honoured when <c>Trigger:Database:RequireConnectionRef=false</c>; otherwise it is
/// rejected so workflow JSON cannot smuggle plaintext credentials.
/// </summary>
public class DatabaseTrigger : IActivityExecutor
{
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
        var orchestratorParams = new Dictionary<string, string>();
        foreach (var (k, v) in context.Variables)
            if (k.StartsWith("manual.", StringComparison.OrdinalIgnoreCase))
                orchestratorParams[k["manual.".Length..]] = v;
        if (orchestratorParams.TryGetValue("dbSentinel", out var dbSentinel))
        {
            return new ActivityResult
            {
                Success = true,
                Output = $"Database trigger fired: {orchestratorParams.GetValueOrDefault("dbPrevious", "?")} → {dbSentinel}",
                OutputParameters = orchestratorParams,
            };
        }

        // Manual run: execute the query inline and report row count
        var connectionRef = config.TryGetProperty("connectionRef", out var cr) ? cr.GetString() : null;
        var connectionInline = config.TryGetProperty("connectionString", out var cs) ? cs.GetString() : null;
        var query = config.TryGetProperty("query", out var q) ? q.GetString() : null;
        var pollingIntervalSeconds = config.TryGetProperty("pollingIntervalSeconds", out var pi) ? pi.GetInt32() : 60;

        if (string.IsNullOrWhiteSpace(query))
        {
            return new ActivityResult { Success = false, ErrorOutput = "No SQL query specified" };
        }

        if (query.Contains("{{", StringComparison.Ordinal) && query.Contains("}}", StringComparison.Ordinal))
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = "DatabaseTrigger: 'query' must not contain {{...}} templates. Trigger queries run before "
                    + "any workflow step exists and have no variable context. Use literal SQL in the trigger query.",
            };
        }

        string? connString;
        try
        {
            connString = ResolveConnectionString(connectionRef, connectionInline);
        }
        catch (InvalidOperationException ex)
        {
            return new ActivityResult { Success = false, ErrorOutput = ex.Message };
        }
        if (string.IsNullOrWhiteSpace(connString))
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = "DatabaseTrigger: provide 'connectionRef' (preferred) or 'connectionString'",
            };
        }

        try
        {
            // Execute the polling query using ADO.NET
            using var connection = new Microsoft.Data.SqlClient.SqlConnection(connString);
            await connection.OpenAsync(ct);

            using var command = new Microsoft.Data.SqlClient.SqlCommand(query, connection);
            command.CommandTimeout = 30;

            using var reader = await command.ExecuteReaderAsync(ct);

            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(ct) && rows.Count < 100) // Cap at 100 rows
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            if (rows.Count == 0)
            {
                return new ActivityResult
                {
                    Success = true,
                    Output = $"Query returned 0 rows (trigger not fired)\nPolling interval: {pollingIntervalSeconds}s"
                };
            }

            var output = $"Query returned {rows.Count} row(s)\n";
            // Serialize first few rows as output
            output += System.Text.Json.JsonSerializer.Serialize(rows.Take(10), new JsonSerializerOptions { WriteIndented = true });

            return new ActivityResult { Success = true, Output = output };
        }
        catch (Exception ex)
        {
            return new ActivityResult { Success = false, ErrorOutput = $"Database error: {ex.Message}" };
        }
    }

    private string ResolveConnectionString(string? connectionRef, string? connectionInline)
    {
        // connectionRef wins. Same whitelist model as DatabaseTriggerSource so manual
        // runs cannot smuggle inline credentials when the source-side would refuse.
        if (!string.IsNullOrWhiteSpace(connectionRef))
        {
            var fromConfig = _config[$"Trigger:Database:Connections:{connectionRef}"];
            if (string.IsNullOrWhiteSpace(fromConfig))
                throw new InvalidOperationException(
                    $"DatabaseTrigger: connectionRef '{connectionRef}' is not configured under " +
                    "Trigger:Database:Connections.");
            return fromConfig;
        }

        if (!string.IsNullOrWhiteSpace(connectionInline))
        {
            if (RequireConnectionRef())
                throw new InvalidOperationException(
                    "DatabaseTrigger: this deployment requires a named connectionRef. " +
                    "Add the target under Trigger:Database:Connections:{name} and reference it via 'connectionRef'.");
            return connectionInline;
        }

        return string.Empty;
    }

    private bool RequireConnectionRef()
    {
        var raw = _config["Trigger:Database:RequireConnectionRef"];
        return string.IsNullOrWhiteSpace(raw)
            || !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
    }
}
