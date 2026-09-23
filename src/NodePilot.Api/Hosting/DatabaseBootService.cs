using Microsoft.EntityFrameworkCore;
using NodePilot.Data;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Waits for the database, migrates it and runs the one-time boot checks before the web server
/// starts.
/// <para>
/// Runs in <see cref="StartingAsync"/>: by then the Windows service lifetime has already reported
/// the service as running, so waiting for a late database no longer runs into the service control
/// manager's 30-second start timeout. Every hosted service's <c>StartAsync</c>, Kestrel included,
/// runs after it. A failure here still stops the host, so boot stays fail-closed.
/// </para>
/// </summary>
public sealed class DatabaseBootService(
    IServiceProvider services,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<DatabaseBootService> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await BootAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "Database boot failed; NodePilot does not start.");
            throw;
        }
    }

    private async Task BootAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var bootstrapDbLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        // Wait for the database to accept connections before migrating. Both deployment modes race
        // the same way at boot and neither service dependency closes it: Desktop against the bundled
        // Postgres service, Server against a remote SQL Server or PostgreSQL still recovering its
        // databases. Only reachability is awaited — a schema/migration error is never retried and
        // surfaces immediately from Bootstrap below.
        await DatabaseReadinessGate.WaitForDatabaseAsync(
            canConnectAsync: token => DatabaseReadinessGate.OpenAndCloseAsync(db, token),
            timeout: DatabaseReadinessGate.ResolveStartupWait(configuration),
            pollInterval: TimeSpan.FromSeconds(2),
            logger: bootstrapDbLogger,
            ct: cancellationToken);

        MigrationBootstrapper.Bootstrap(db, bootstrapDbLogger);

        // Surface the active secret protector so operators see "DPAPI" vs "AES-GCM" in the
        // boot log without grepping config.
        scope.ServiceProvider.GetRequiredService<NodePilot.Data.Security.SecretProtectorRegistry.IStartupLogger>().Log();

        // WorkflowVersions predate at-rest protection. Legacy rows are not rewritten at startup:
        // the updater's rollback restores binaries, not database contents, and an upgraded HA
        // passive node must stay data-compatible with the old active node.
        await scope.ServiceProvider
            .GetRequiredService<NodePilot.Api.Services.WorkflowVersionDefinitionProtector>()
            .WarnIfExplicitMigrationRequiredAsync(db, CancellationToken.None);

        // Sweep Running executions left over from a previous process instance. In cluster mode a
        // starting follower must not touch the leader's rows; recovery then runs on leadership
        // acquisition instead.
        if (!configuration.GetValue<bool>("Cluster:Enabled"))
        {
            await NodePilot.Engine.Execution.StartupRecovery.RecoverOrphanedExecutionsAsync(db, bootstrapDbLogger);
        }
        else
        {
            bootstrapDbLogger.LogInformation(
                "Cluster:Enabled=true — skipping boot-time orphan recovery. Will run on first leadership acquisition.");
        }

        // A seeded backup brings its own users, including the break-glass Admin the recovery
        // invariant requires, so seeding runs before both checks below.
        await NodePilot.Api.Security.ProvisioningSeeder.SeedIfEmptyAsync(
            db,
            configuration,
            scope.ServiceProvider.GetRequiredService<NodePilot.Api.Services.Backup.BackupRestoreService>(),
            bootstrapDbLogger);

        // Admin bootstrap: without users, the first login must present a one-shot token, or the
        // first caller of /api/auth/login would become Admin.
        var usersExist = await db.Users.AnyAsync(CancellationToken.None);
        await NodePilot.Api.Security.EnterpriseRecoveryInvariant.EnsureAsync(db, configuration);
        NodePilot.Api.Security.AdminBootstrap.EnsureBootstrapTokenIfNeeded(
            environment, usersExist, bootstrapDbLogger, configuration);

        // Only now may the availability breaker react to failures. Before this point the failed
        // readiness probes would open it and the migration would run with retries disabled.
        scope.ServiceProvider.GetRequiredService<NodePilot.Data.Availability.IDatabaseAvailability>().MarkBootComplete();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
