using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace NodePilot.Data.Availability;

/// <summary>
/// Connection-string surgery shared by the pooled DbContext and the availability probe.
/// </summary>
public static class DatabaseConnectionString
{
    /// <summary>SQL Server's spelling.</summary>
    private const string SqlServerConnectTimeoutKey = "Connect Timeout";

    /// <summary>
    /// Npgsql's spelling. Verified against Npgsql 10.0.3 by dumping the driver's keyword table:
    /// <c>Timeout</c>, <c>Command Timeout</c>, <c>Minimum/Maximum Pool Size</c> and
    /// <c>Connection Lifetime</c> exist; <b><c>Connect Timeout</c> and <c>Connection Timeout</c> do
    /// not</b>. Writing SQL Server's spelling into a PostgreSQL string throws.
    /// </summary>
    private const string NpgsqlConnectTimeoutKey = "Timeout";

    /// <summary>
    /// Adds a connect timeout when the operator has not set one, leaving an explicit value alone.
    ///
    /// <para>This matters more than it looks. Measured on 2026-08-06: against a hung-but-listening
    /// server a command timeout costs <c>CommandTimeout + 2 x ConnectTimeout</c> in wall clock,
    /// because the driver answers a timeout by sending a cancel request over a <i>new</i>
    /// connection
    /// that runs into the same wedge. The connect timeout is therefore on the critical path twice,
    /// and
    /// the shipped default of 15 s is what turns a 3 s budget into a 33 s hang.</para>
    /// </summary>
    /// <returns>The adjusted connection string, or the input unchanged if it cannot be
    /// parsed.</returns>
    public static string EnsureConnectTimeout(string? provider, string? connectionString, int seconds)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return connectionString ?? string.Empty;
        if (seconds <= 0) return connectionString;

        var isSqlServer = string.Equals(provider, "sqlserver", StringComparison.OrdinalIgnoreCase);
        var key = isSqlServer ? SqlServerConnectTimeoutKey : NpgsqlConnectTimeoutKey;

        try
        {
            // The presence test deliberately uses the RAW builder rather than the typed one:
            // SqlConnectionStringBuilder.ContainsKey returns true for every supported keyword
            // whether
            // it was set or not, so asking the typed builder would silently no-op this whole
            // method.
            var raw = new DbConnectionStringBuilder { ConnectionString = connectionString };
            if (raw.ContainsKey(key)) return connectionString;
            if (isSqlServer && raw.ContainsKey("Connection Timeout")) return connectionString; // synonym

            raw[key] = seconds;
            return raw.ConnectionString;
        }
        catch (ArgumentException)
        {
            // An unparseable connection string is not this method's problem to report - the
            // provider
            // raises a far better error moments later. Deliberately swallowed without logging the
            // string, which carries the password. Same reasoning as DatabaseTlsBootValidator.
            return connectionString;
        }
    }

    /// <summary>
    /// Derives the availability probe's own connection string: pooling off, its own short timeouts,
    /// and
    /// a distinct application name so it is identifiable in <c>pg_stat_activity</c> /
    /// <c>sys.dm_exec_sessions</c>.
    ///
    /// <para>Pooling is off on purpose. The probe must never queue behind the very pool it is
    /// adjudicating — under <c>too_many_connections</c> or a pool exhausted by 800 blocked callers,
    /// a
    /// pooled probe would report an outage caused by the callers rather than by the server.</para>
    /// </summary>
    public static string ForProbe(string? provider, string? connectionString, int connectTimeoutSeconds, int commandTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return connectionString ?? string.Empty;

        var isSqlServer = string.Equals(provider, "sqlserver", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (isSqlServer)
            {
                var b = new SqlConnectionStringBuilder(connectionString)
                {
                    Pooling = false,
                    ConnectTimeout = Math.Max(1, connectTimeoutSeconds),
                    ApplicationName = "NodePilot-Probe",
                };
                return b.ConnectionString;
            }

            var n = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
                Timeout = Math.Max(1, connectTimeoutSeconds),
                CommandTimeout = Math.Max(1, commandTimeoutSeconds),
                ApplicationName = "NodePilot-Probe",
            };
            return n.ConnectionString;
        }
        catch (ArgumentException)
        {
            return connectionString;
        }
    }
}
