using System.Data.Common;
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
///          <c>username</c>, <c>password</c>, <c>sslMode</c> (default "VerifyFull"; weaker
///          modes are accepted only for literal loopback hosts).
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
///   - SELECT rows -> JSON array in <c>Output</c>, <c>rowCount</c> + first-row columns in
///     <c>OutputParameters</c>, plus <c>row{i}_{col}</c> for up to 20 rows.
///   - DML/DDL -> <c>rowsAffected</c> in <c>OutputParameters</c>.
/// </summary>
public class SqlActivity : IActivityExecutor
{
    private readonly IConfiguration _configuration;

    // Default command timeout. Workflow queries are normally fast, so anything running
    // longer is treated as a runaway. Override per activity via the `timeoutSeconds` config field.
    internal const int DefaultCommandTimeoutSeconds = 60;

    // Hard row cap on the materialized result set. Beyond this, the `truncated=true`
    // output parameter is set so consumers know the cap was hit.
    internal const int MaxRowsReturned = 1000;

    // Cap on flat row{i}_{col} and first-row scalar keys exposed in OutputParameters.
    // Covers single-row and small multi-row access; consumers needing more should
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

            // Refuse query text that still carries {{var}} templates. The engine deliberately
            // excludes `query` from the template-resolution pass (see StepRunner +
            // VariableResolver.ResolveVariablesExcept) so dynamic values cannot be smuggled into
            // a raw CommandText. Bind dynamic values via `parameters` instead.
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
            // Default command timeout is 60 seconds. Override per activity by setting
            // `timeoutSeconds` in the node config (positive integer). ADO.NET treats 0 as
            // "no timeout", but that value is never set implicitly here.
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
            // Check whether more rows remain beyond the cap. Without this, a workflow expecting
            // N rows would receive a silently-capped result. Exposed as `truncated=true` so
            // callers can branch on it. Checked after the loop to avoid an extra read in the
            // common non-truncated case.
            var truncated = rows.Count >= MaxRowsReturned && await reader.ReadAsync(ct);

            var outputParams = new Dictionary<string, string>();
            string output;

            if (rows.Count > 0)
            {
                // SELECT-style result
                outputParams["rowCount"] = rows.Count.ToString();
                outputParams["truncated"] = truncated ? "true" : "false";

                // Bound flat-key growth. First-row scalars plus row{i}_{col} keys can otherwise
                // produce hundreds of keys for wide schemas, bloating the variable map for
                // downstream steps and the persisted OutputParametersJson. The cap applies
                // across both projections combined (first-row scalars + multi-row flat).
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
    ///   1. <c>connectionRef</c> -> look up <c>SqlActivity:ConnectionStrings:{name}</c>.
    ///   2. Builder fields (<c>server</c>/<c>host</c>/<c>dataSource</c> depending on provider)
    ///      -> composed via the provider's own <c>ConnectionStringBuilder</c>.
    ///   3. <c>connectionString</c> -> raw inline string. Rejected by default; set
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
            return ApplyProviderSecurityPolicy(fromConfig, provider);
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
            var built = BuildConnectionString(config, provider);
            return built.Error is null
                ? ApplyProviderSecurityPolicy(built.ConnStr!, provider)
                : built;
        }

        var raw = config.GetStringOrNull("connectionString");
        if (string.IsNullOrWhiteSpace(raw))
            return (null, "SQL: provide 'connectionRef', the builder fields (server/host/dataSource), or 'connectionString'");

        if (requireRef)
            return (null,
                "SQL: this deployment requires a named connectionRef. Add the target under " +
                "SqlActivity:ConnectionStrings:{name} and reference it via 'connectionRef'.");

        return ApplyProviderSecurityPolicy(raw, provider);
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

        // SQL Server's "authentication" knob can flip from integrated -> SQL auth without a
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

    /// <summary>
    /// Enforces provider-level transport rules after every resolution path, including named and
    /// raw connection strings. PostgreSQL defaults to full certificate/hostname verification;
    /// explicitly weaker modes fail closed for non-loopback hosts.
    /// </summary>
    private static (string? ConnStr, string? Error) ApplyProviderSecurityPolicy(
        string connectionString,
        string provider)
    {
        if (provider is not ("postgres" or "postgresql" or "npgsql"))
            return (connectionString, null);

        try
        {
            var supplied = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var sslModeWasSpecified = supplied.Keys
                .Cast<string>()
                .Any(key => string.Equals(
                    key.Replace(" ", "", StringComparison.Ordinal),
                    "sslmode",
                    StringComparison.OrdinalIgnoreCase));
            var trustServerCertificate = supplied.Keys
                .Cast<string>()
                .Any(key => string.Equals(
                        key.Replace(" ", "", StringComparison.Ordinal),
                        "trustservercertificate",
                        StringComparison.OrdinalIgnoreCase)
                    // Fail closed on anything except an explicit false. The Npgsql parser will
                    // reject malformed values later, but no new truthy spelling may bypass this
                    // policy if its converter grows more permissive.
                    && !string.Equals(supplied[key]?.ToString(), "false", StringComparison.OrdinalIgnoreCase));

            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!sslModeWasSpecified)
                builder.SslMode = Npgsql.SslMode.VerifyFull;

            var loopbackOnly = AllPostgresHostsAreLiteralLoopback(builder.Host);
            if (!loopbackOnly
                && (builder.SslMode != Npgsql.SslMode.VerifyFull || trustServerCertificate))
            {
                return (null,
                    "SQL: PostgreSQL connections to non-loopback hosts require SSL Mode=VerifyFull "
                    + "and Trust Server Certificate=false. Settings which bypass server identity "
                    + "validation are blocked.");
            }

            return (builder.ConnectionString, null);
        }
        catch (ArgumentException)
        {
            // Do not echo the source connection string or parser exception: either may contain a
            // password. The caller only needs a safe, actionable configuration error.
            return (null, "SQL: the PostgreSQL connection settings are invalid.");
        }
    }

    private static bool AllPostgresHostsAreLiteralLoopback(string? configuredHosts)
    {
        if (string.IsNullOrWhiteSpace(configuredHosts))
            return false;
        var hosts = configuredHosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return hosts.Length > 0 && hosts.All(IsLiteralLoopbackHost);
    }

    private static bool IsLiteralLoopbackHost(string configuredHost)
    {
        var host = configuredHost.Trim().Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!System.Net.IPAddress.TryParse(host, out var address))
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return System.Net.IPAddress.IsLoopback(address);
    }

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

        // Encryption alone is insufficient: Require accepts any server certificate. VerifyFull
        // authenticates both the issuing CA and the configured hostname, preventing a MITM from
        // collecting the database credential. The common plaintext dev case remains available
        // only for literal loopback hosts via the provider security policy above.
        var sslMode = config.GetStringOrNull("sslMode");
        if (string.IsNullOrWhiteSpace(sslMode))
            b.SslMode = Npgsql.SslMode.VerifyFull;
        else if (Enum.TryParse<Npgsql.SslMode>(sslMode, ignoreCase: true, out var parsedSslMode))
            b.SslMode = parsedSslMode;
        else
            return (null, "SQL: 'sslMode' is invalid for PostgreSQL.");

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
    /// Adds any <c>parameters</c> from config to the command. Real ADO.NET parameters prevent
    /// SQL injection even when the query text came from a webhook payload. The engine's
    /// <c>{{...}}</c> template substitution must not be used for SQL values; bind dynamic
    /// values via this <c>parameters</c> object instead.
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
