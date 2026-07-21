using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using Npgsql;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Runs a SQL statement (SELECT / INSERT / UPDATE / DELETE / DDL) against SQL Server, SQLite,
/// or PostgreSQL.
///
/// Connection resolution (first non-empty wins):
///   1. <c>connectionRef</c> — named whitelist entry under
///      <c>SqlActivity:ConnectionStrings:{name}</c>.
///   2. Provider-specific builder fields — composed via the provider's own
///      <c>ConnectionStringBuilder</c> so escaping is correct:
///        * SQL Server: <c>server</c> (required), <c>database</c>, <c>authentication</c>
///          ("integrated"/"sql"), <c>username</c>, <c>password</c>, <c>encrypt</c> (default
///          true), <c>trustServerCertificate</c> (default false).
///        * Postgres:  <c>host</c> (required), <c>port</c> (default 5432), <c>database</c>,
///          <c>username</c>, <c>password</c>, <c>sslMode</c> (default "Require" — set
///          "Disable"/"Prefer" explicitly to allow a plaintext fallback).
///        * SQLite:    <c>dataSource</c> (required, file path).
///   3. <c>connectionString</c> — raw inline string. Rejected unless
///      <c>SqlActivity:RequireConnectionRef=false</c>.
///
/// Other config:
///   provider         string, "sqlserver" (default), "sqlite" or "postgres"
///                    (aliases: "postgresql" / "npgsql").
///   query            string, required.
///   parameters       object, optional — <c>{"name":"value"}</c>. Values are bound via
///                    parameterized commands; placeholders in the query are
///                    <c>@name</c> on SQL Server + Postgres and <c>$name</c> on SQLite.
///                    (Npgsql also accepts <c>:name</c> — <c>@name</c> works across all
///                    providers.)
///   timeoutSeconds   int, default 60 (overridable per activity via the config field).
///
/// Output shape (rows):
///   - SELECT rows → JSON array in <c>Output</c>, <c>rowCount</c> + first-row columns in
///     <c>OutputParameters</c>, plus <c>row{i}_{col}</c> for up to 20 rows.
///   - DML/DDL → <c>rowsAffected</c> in <c>OutputParameters</c>.
/// </summary>
public class SqlActivity : IActivityExecutor
{
    private readonly IConfiguration _configuration;

    // Per CLAUDE.md SQL-activity defaults: 60s aligns with the operational reality —
    // legitimate workflow queries are sub-second, anything beyond a minute is almost
    // always a runaway. Configurable per-activity via the `timeoutSeconds` config field.
    internal const int DefaultCommandTimeoutSeconds = 60;

    // D3: hard row cap on the materialised result set. Beyond this we set the
    // `truncated=true` output parameter so consumers see the cap was hit.
    internal const int MaxRowsReturned = 1000;

    // D4: bound on flat row{i}_{col} + first-row scalar keys exposed in OutputParameters.
    // 200 covers ergonomic single-row + a handful-of-rows access; wider/deeper consumers
    // parse the full JSON Output instead. flatKeysTruncated=true signals overflow.
    internal const int MaxFlatOutputKeys = 200;
    internal const int MaxRowsForFlatProjection = 20;

    public SqlActivity(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string ActivityType => "sql";

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
        => ActivityExecution.RunAsync(async () =>
        {
            var provider = config.GetStringOrNull("provider")?.ToLowerInvariant() ?? "sqlserver";
            var query = config.GetStringOrNull("query");
            var timeoutSeconds = config.GetOptionalPositiveInt("timeoutSeconds");

            if (string.IsNullOrWhiteSpace(query))
                return new ActivityResult { Success = false, ErrorOutput = "SQL: 'query' is required" };

            // H-1 (security audit 2026-05-15): refuse query text that still carries
            // {{var}} templates. The engine deliberately excludes `query` from the
            // template-resolution pass (see StepRunner + VariableResolver.ResolveVariablesExcept)
            // so that dynamic values cannot be smuggled into a raw CommandText. Anything that
            // arrives here with residual placeholders is either the author bypassing the
            // intended `parameters` flow or a future code-path that forgot to opt into the
            // field guard — both must fail closed.
            if (query.Contains("{{", StringComparison.Ordinal) && query.Contains("}}", StringComparison.Ordinal))
                return new ActivityResult
                {
                    Success = false,
                    ErrorOutput = "SQL: 'query' must not contain {{...}} templates. Bind dynamic values via the "
                        + "'parameters' object (e.g. \"parameters\": {\"id\": \"{{manual.userId}}\"}) and reference "
                        + "them with @id / $id / :id in the query text.",
                };

            var (connStr, connErr) = ResolveConnectionString(config, provider);
            if (connErr is not null)
                return new ActivityResult { Success = false, ErrorOutput = connErr };

            await using var conn = CreateConnection(provider, connStr!);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = query;
            // C1: default 60 seconds — operationally the most useful tradeoff. A typical
            // workflow query is sub-second; runaway scans should be the exception. Override
            // per activity by setting `timeoutSeconds` in the node config (positive integer).
            // ADO.NET still treats 0 as "no timeout", but we never set 0 implicitly anymore.
            cmd.CommandTimeout = timeoutSeconds ?? DefaultCommandTimeoutSeconds;
            BindParameters(cmd, config);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var rows = new List<Dictionary<string, object?>>();
            while (rows.Count < MaxRowsReturned && await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            // D3: probe whether the result set had more rows than the cap allowed. Without
            // this signal, a workflow that expects N rows but receives the silently-capped
            // 1000 makes wrong business-logic decisions. Caller sees `truncated=true` and
            // can branch on it. We do this AFTER the loop so we don't waste a read on the
            // common (non-truncated) case.
            var truncated = rows.Count >= MaxRowsReturned && await reader.ReadAsync(ct);

            var outputParams = new Dictionary<string, string>();
            string output;

            if (rows.Count > 0)
            {
                // SELECT-style result
                outputParams["rowCount"] = rows.Count.ToString();
                outputParams["truncated"] = truncated ? "true" : "false";

                // D4: bound flat-key explosion. First-row scalars + row{i}_{col} can produce
                // hundreds of keys for wide schemas (50 cols × 20 rows = 1000+ keys), bloating
                // the variable map for downstream steps and the persisted OutputParametersJson.
                // Cap is enforced across BOTH projections (first-row scalars + multi-row flat).
                var flatKeyBudget = MaxFlatOutputKeys;
                // First row's scalar columns as plain keys (single-row-query ergonomics)
                foreach (var (col, val) in rows[0])
                {
                    if (flatKeyBudget <= 0) break;
                    outputParams[col] = val?.ToString() ?? "";
                    flatKeyBudget--;
                }
                // First 20 rows as row{i}_{col} for multi-row access
                for (int i = 0; i < Math.Min(MaxRowsForFlatProjection, rows.Count); i++)
                {
                    if (flatKeyBudget <= 0) break;
                    foreach (var (col, val) in rows[i])
                    {
                        if (flatKeyBudget <= 0) break;
                        outputParams[$"row{i}_{col}"] = val?.ToString() ?? "";
                        flatKeyBudget--;
                    }
                }
                if (flatKeyBudget <= 0)
                    outputParams["flatKeysTruncated"] = "true";

                output = JsonSerializer.Serialize(rows, JsonSerializerDefaults.Indented);
            }
            else
            {
                // DML / DDL
                var affected = reader.RecordsAffected;
                outputParams["rowsAffected"] = affected.ToString();
                outputParams["rowCount"] = "0";
                output = $"Statement executed. rowsAffected={affected}";
            }

            return new ActivityResult
            {
                Success = true,
                Output = output,
                OutputParameters = outputParams,
            };
        }, ex => $"SQL error: {ex.Message}");

    private static DbConnection CreateConnection(string provider, string connStr) => provider switch
    {
        "sqlite" => new SqliteConnection(connStr),
        "postgres" or "postgresql" or "npgsql" => new NpgsqlConnection(connStr),
        _ => new SqlConnection(connStr),
    };

    /// <summary>
    /// Resolves the connection string from the workflow config. Preference order:
    ///   1. <c>connectionRef</c> → look up <c>SqlActivity:ConnectionStrings:{name}</c>.
    ///   2. Builder fields (<c>server</c>/<c>host</c>/<c>dataSource</c> depending on provider)
    ///      → composed via the provider's own <c>ConnectionStringBuilder</c>.
    ///   3. <c>connectionString</c> → raw inline string. Rejected by default; set
    ///      <c>SqlActivity:RequireConnectionRef=false</c> only for explicit dev compatibility.
    /// Returns (connection-string, error) — exactly one is non-null.
    ///
    /// Strict mode (<c>SqlActivity:RequireConnectionRef=true</c>) blocks BOTH raw
    /// connectionString AND builder-mode-with-credentials. The whole point of
    /// RequireConnectionRef is "no DB secrets in workflow JSON" — accepting builder fields
    /// with a <c>password</c> would defeat that goal. Builder mode without credentials
    /// (SQL Server integrated auth, file-only SQLite) is still allowed in strict mode
    /// because there is no secret to leak.
    /// </summary>
    private (string? ConnStr, string? Error) ResolveConnectionString(JsonElement config, string provider)
    {
        var connectionRef = config.GetStringOrNull("connectionRef");
        if (!string.IsNullOrWhiteSpace(connectionRef))
        {
            var fromConfig = _configuration[$"SqlActivity:ConnectionStrings:{connectionRef}"];
            if (string.IsNullOrWhiteSpace(fromConfig))
                return (null, $"SQL: connectionRef '{connectionRef}' is not configured under SqlActivity:ConnectionStrings");
            return (fromConfig, null);
        }

        var requireRef = RequireConnectionRef();

        if (HasBuilderFields(config, provider))
        {
            if (requireRef && BuilderConfigCarriesCredentials(config, provider))
                return (null,
                    "SQL: this deployment requires a named connectionRef. The supplied builder fields " +
                    "include credentials (username/password), which would put DB secrets in the workflow " +
                    "JSON and defeat the strict-whitelist policy. Add the target under " +
                    "SqlActivity:ConnectionStrings:{name} and reference it via 'connectionRef'.");
            return BuildConnectionString(config, provider);
        }

        var raw = config.GetStringOrNull("connectionString");
        if (string.IsNullOrWhiteSpace(raw))
            return (null, "SQL: provide 'connectionRef', the builder fields (server/host/dataSource), or 'connectionString'");

        if (requireRef)
            return (null,
                "SQL: this deployment requires a named connectionRef. Add the target under " +
                "SqlActivity:ConnectionStrings:{name} and reference it via 'connectionRef'.");

        return (raw, null);
    }

    private bool RequireConnectionRef()
    {
        var configured = _configuration["SqlActivity:RequireConnectionRef"];
        return string.IsNullOrWhiteSpace(configured)
            || !string.Equals(configured, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the builder-mode config carries any credential field — username, password,
    /// or SQL Server's explicit <c>authentication=sql</c>. Strict-mode rejects these because
    /// the secret would otherwise live in the workflow JSON. Integrated-auth Builder configs
    /// (SQL Server, no username/password) and SQLite file paths pass through.
    /// </summary>
    private static bool BuilderConfigCarriesCredentials(JsonElement config, string provider)
    {
        if (!string.IsNullOrWhiteSpace(config.GetStringOrNull("username"))) return true;
        if (!string.IsNullOrWhiteSpace(config.GetStringOrNull("password"))) return true;

        // SQL Server's "authentication" knob can flip from integrated → SQL auth without a
        // password field at the top level (the password may be templated in via {{globals.X}}
        // and resolved later). Treat any non-integrated value as credential-bearing.
        if (provider is "sqlserver" or "")
        {
            var auth = config.GetStringOrNull("authentication")?.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(auth) && auth != "integrated") return true;
        }

        return false;
    }

    /// <summary>
    /// True when the config carries the provider's "main" builder field. Acts as the trigger
    /// to switch from raw-connection-string mode to builder mode.
    /// </summary>
    private static bool HasBuilderFields(JsonElement config, string provider)
    {
        var key = provider switch
        {
            "sqlite" => "dataSource",
            "postgres" or "postgresql" or "npgsql" => "host",
            _ => "server",
        };
        return !string.IsNullOrWhiteSpace(config.GetStringOrNull(key));
    }

    /// <summary>
    /// Composes a connection string from individual fields using the provider's own
    /// <c>*ConnectionStringBuilder</c>, so spaces, semicolons and quotes in values are escaped
    /// correctly. Returns an error if a required field is missing.
    /// </summary>
    private static (string? ConnStr, string? Error) BuildConnectionString(JsonElement config, string provider) =>
        provider switch
        {
            "sqlite" => BuildSqliteConnectionString(config),
            "postgres" or "postgresql" or "npgsql" => BuildPostgresConnectionString(config),
            _ => BuildSqlServerConnectionString(config),
        };

    private static (string? ConnStr, string? Error) BuildSqlServerConnectionString(JsonElement config)
    {
        var server = config.GetStringOrNull("server");
        if (string.IsNullOrWhiteSpace(server))
            return (null, "SQL: 'server' is required when using the builder for SQL Server");

        var b = new SqlConnectionStringBuilder
        {
            DataSource = server,
            Encrypt = config.GetBool("encrypt", defaultValue: true),
            TrustServerCertificate = config.GetBool("trustServerCertificate", defaultValue: false),
        };

        var database = config.GetStringOrNull("database");
        if (!string.IsNullOrWhiteSpace(database))
            b.InitialCatalog = database;

        var auth = config.GetStringOrNull("authentication")?.ToLowerInvariant() ?? "integrated";
        if (auth == "integrated")
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            var username = config.GetStringOrNull("username");
            var password = config.GetStringOrNull("password");
            if (string.IsNullOrWhiteSpace(username))
                return (null, "SQL: 'username' is required for SQL authentication");
            b.UserID = username;
            b.Password = password ?? "";
        }

        return (b.ConnectionString, null);
    }

    private static (string? ConnStr, string? Error) BuildPostgresConnectionString(JsonElement config)
    {
        var host = config.GetStringOrNull("host");
        if (string.IsNullOrWhiteSpace(host))
            return (null, "SQL: 'host' is required when using the builder for PostgreSQL");

        var b = new NpgsqlConnectionStringBuilder { Host = host };

        if (config.TryGetProperty("port", out var portEl) && portEl.TryGetInt32(out var port) && port > 0)
            b.Port = port;

        var database = config.GetStringOrNull("database");
        if (!string.IsNullOrWhiteSpace(database))
            b.Database = database;

        var username = config.GetStringOrNull("username");
        if (!string.IsNullOrWhiteSpace(username))
            b.Username = username;

        var password = config.GetStringOrNull("password");
        if (!string.IsNullOrWhiteSpace(password))
            b.Password = password;

        // M-4 (security audit 2026-05-15): default to SslMode.Require instead of Npgsql's
        // built-in Prefer. Prefer silently downgrades to a plaintext connection when the
        // server doesn't offer TLS, so DB credentials can be sniffed by a MITM on the wire.
        // Require forces an encrypted channel; operators who genuinely need plaintext (a
        // trusted local socket, a server without TLS) opt in explicitly via sslMode="Disable".
        var sslMode = config.GetStringOrNull("sslMode");
        if (string.IsNullOrWhiteSpace(sslMode))
            b.SslMode = Npgsql.SslMode.Require;
        else if (Enum.TryParse<Npgsql.SslMode>(sslMode, ignoreCase: true, out var parsedSslMode))
            b.SslMode = parsedSslMode;

        return (b.ConnectionString, null);
    }

    private static (string? ConnStr, string? Error) BuildSqliteConnectionString(JsonElement config)
    {
        var dataSource = config.GetStringOrNull("dataSource");
        if (string.IsNullOrWhiteSpace(dataSource))
            return (null, "SQL: 'dataSource' is required when using the builder for SQLite");

        var b = new SqliteConnectionStringBuilder { DataSource = dataSource };
        return (b.ConnectionString, null);
    }

    /// <summary>
    /// Adds any <c>parameters</c> from config to the command. Using real ADO.NET parameters
    /// defeats SQL injection even when the query text itself was assembled from a webhook
    /// payload — the engine's <c>{{...}}</c> template substitution should never have been
    /// used for SQL values, but historically it was. Keep the migration path open by still
    /// accepting a query string as-is.
    /// </summary>
    private static void BindParameters(DbCommand cmd, JsonElement config)
    {
        if (!config.TryGetProperty("parameters", out var paramsEl) || paramsEl.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in paramsEl.EnumerateObject())
        {
            var p = cmd.CreateParameter();
            p.ParameterName = prop.Name;
            p.Value = prop.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => l,
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.String => (object?)prop.Value.GetString() ?? DBNull.Value,
                _ => prop.Value.GetRawText(),
            };
            cmd.Parameters.Add(p);
        }
    }
}
