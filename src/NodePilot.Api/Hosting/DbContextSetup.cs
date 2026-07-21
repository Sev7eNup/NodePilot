using Microsoft.EntityFrameworkCore;
using NodePilot.Data;
using Serilog;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Centralises the DbContext registration. Handles the SQL Server / PostgreSQL fork,
/// pooling, retry policies, batch-size / command-timeout overrides, and the inline-
/// password warning so <c>Program.cs</c> stays a thin composition-root.
///
/// SQLite is <b>not</b> supported as an application database provider — it is used only as
/// an in-memory test backend in the test projects (see CLAUDE.md "Database").
/// </summary>
internal static class DbContextSetup
{
    /// <summary>
    /// Registers <see cref="NodePilotDbContext"/> as a pooled DbContext with the
    /// configured provider (Postgres default, SQL Server alternative).
    /// </summary>
    public static IServiceCollection AddNodePilotDbContext(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Perf: AddDbContextPool recycles DbContext instances across requests / scopes instead of
        // allocating a fresh one per resolution. The engine creates a scope per step; without
        // pooling, a 100-step workflow allocates 100 DbContexts. Default pool size (1024) is plenty
        // for our workload — exposed as `Database:PoolSize` for tuning under memory pressure.
        // Safe here because NodePilotDbContext has no OnConfiguring override, no per-call state,
        // and no model-customisation that depends on constructor args — the exact contract EF Core
        // requires for pool reuse.
        var dbProvider = (configuration["Database:Provider"] ?? "postgres").ToLowerInvariant();
        var dbPoolSize = configuration.GetValue<int?>("Database:PoolSize") ?? 1024;

        services.AddDbContextPool<NodePilotDbContext>(options =>
        {
            // Defaults shared between SQL Server and Postgres branches:
            //   - MaxBatchSize 200: EF default is 42 (SqlServer) / unbounded (Npgsql, but
            //     practically server-protocol-limited). The engine bursts StepExecution INSERTs
            //     in tight loops; on SQL Server with 1ms RTT and 1000 steps the default packs
            //     into ~24 round-trips, 200 cuts that to ~5. Cap is conservative — well below
            //     the server-side parameter limit (2100 for SQL Server, 65535 for Postgres).
            //   - CommandTimeout 120s: default is 30s globally. WorkflowStatsRefresher does
            //     four full-table GROUP BYs every 5 min — at 1M+ rows that 30s ceiling starts
            //     tripping. 120s is a safety buffer; per-query Cancel via CancellationToken
            //     remains the primary control.
            var dbCommandTimeoutSeconds = configuration.GetValue<int?>("Database:CommandTimeoutSeconds") ?? 120;
            var dbMaxBatchSize = configuration.GetValue<int?>("Database:MaxBatchSize") ?? 200;

            if (dbProvider == "sqlserver")
                options.UseSqlServer(
                    configuration.GetConnectionString("DefaultConnection"),
                    // EnableRetryOnFailure absorbs transient SQL Server errors (deadlocks, brief
                    // network blips, login timeouts during restart). Without it, EF Core throws
                    // "An exception has been raised that is likely due to a transient failure"
                    // and the step fails permanently. Combined with the PK-violation catch in
                    // WorkflowDbWriteMetrics.SaveChangesMeasuredAsync, retries become idempotent:
                    // if the original INSERT actually committed before the network blip, the
                    // retry's PK violation is silently absorbed.
                    sqlOpts =>
                    {
                        sqlOpts.EnableRetryOnFailure(
                            maxRetryCount: 5,
                            maxRetryDelay: TimeSpan.FromSeconds(10),
                            errorNumbersToAdd: null);
                        sqlOpts.CommandTimeout(dbCommandTimeoutSeconds);
                        sqlOpts.MaxBatchSize(dbMaxBatchSize);
                    });
            else if (dbProvider is "postgres" or "postgresql" or "npgsql")
                options.UseNpgsql(
                    configuration.GetConnectionString("Postgres"),
                    // Same rationale as the SQL Server branch above: under load (e.g. 500-parallel
                    // stress test) Postgres can reject connections with SQLSTATE 53300
                    // ("remaining connection slots are reserved for roles with the SUPERUSER
                    // attribute"). Without retry, EF Core surfaces the generic
                    // "An exception has been raised that is likely due to a transient failure"
                    // and the step fails permanently — even though the activity itself succeeded.
                    // The PK-violation absorber in WorkflowDbWriteMetrics.SaveChangesIdempotentAsync
                    // also catches Npgsql 23505 so retries that replay an already-committed INSERT
                    // become idempotent.
                    npgOpts =>
                    {
                        npgOpts.EnableRetryOnFailure(
                            maxRetryCount: 5,
                            maxRetryDelay: TimeSpan.FromSeconds(10),
                            errorCodesToAdd: null);
                        npgOpts.CommandTimeout(dbCommandTimeoutSeconds);
                        npgOpts.MaxBatchSize(dbMaxBatchSize);
                    });
            else
                throw new InvalidOperationException(
                    $"Database:Provider '{dbProvider}' wird nicht unterstützt. " +
                    "Erlaubt: 'sqlserver' oder 'postgres' (Aliase: 'postgresql', 'npgsql').");

            // EF Core 9+ promotes PendingModelChangesWarning from Warning to Error by default. Our
            // migrations are deliberately provider-agnostic (the same set runs on both SQL Server
            // and Postgres - see CLAUDE.md "Database"), but the ModelSnapshot file was generated
            // against only one of the two providers. At runtime, EF compares the snapshot against the active
            // provider's type mapping and raises false-positive pending-changes (Guid →
            // uniqueidentifier vs uuid), even though the migration produces the correct schema.
            // Downgrade to a no-op so MigrationBootstrapper can apply migrations cleanly.
            options.ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        }, poolSize: dbPoolSize);

        return services;
    }

    /// <summary>
    /// Reject plaintext SQL passwords that come from JSON-backed configuration in non-Development
    /// environments. Passwords supplied through environment variables still resolve to a normal
    /// connection string at runtime, but they stay out of appsettings.json, backups, and Git.
    /// </summary>
    public static void WarnAboutInlinePasswords(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment()) return;

        var dbProvider = (configuration["Database:Provider"] ?? "postgres").ToLowerInvariant();
        if (dbProvider != "sqlserver" && dbProvider is not ("postgres" or "postgresql" or "npgsql")) return;

        var connKey = dbProvider == "sqlserver" ? "DefaultConnection" : "Postgres";
        var conn = configuration.GetConnectionString(connKey) ?? "";
        var hasInlinePassword = System.Text.RegularExpressions.Regex.IsMatch(
            conn, @"(?i)(^|;)\s*(password|pwd)\s*=\s*[^;\s]", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        var hasIntegratedAuth = System.Text.RegularExpressions.Regex.IsMatch(
            conn, @"(?i)(trusted_connection|integrated\s*security)\s*=\s*(sspi|true|yes)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        if (hasInlinePassword && !hasIntegratedAuth)
        {
            var configKey = $"ConnectionStrings:{connKey}";
            var providerName = GetWinningProviderName(configuration, configKey);
            if (IsJsonProvider(providerName))
            {
                throw new InvalidOperationException(
                    $"{configKey} contains Password= in JSON-backed configuration. " +
                    $"Move the full connection string to the ConnectionStrings__{connKey} environment variable " +
                    "or to a secret store.");
            }

            if (providerName?.Contains("EnvironmentVariables", StringComparison.OrdinalIgnoreCase) == true)
                return;

            Log.Warning("Database:{ConnKey} contains a plaintext Password=. " +
                        "The active value is not JSON-backed, so startup continues. Prefer Trusted_Connection / " +
                        "Integrated Security where supported (SQL Server), an environment variable, or a secrets manager. " +
                        "Current provider: {Provider}.",
                        connKey, providerName ?? "unknown");
        }
    }

    internal static bool IsJsonBacked(IConfiguration configuration, string key)
        => IsJsonProvider(GetWinningProviderName(configuration, key));

    private static bool IsJsonProvider(string? providerName)
        => providerName?.Contains("Json", StringComparison.OrdinalIgnoreCase) == true;

    private static string? GetWinningProviderName(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root) return null;

        foreach (var provider in root.Providers.Reverse())
        {
            if (!provider.TryGet(key, out var value) || string.IsNullOrWhiteSpace(value))
                continue;

            return provider.GetType().Name;
        }

        return null;
    }
}
