using System.Data;
using System.Data.Common;
using System.Diagnostics.Metrics;
using Microsoft.Data.SqlClient;
using NodePilot.Api.Telemetry;
using NodePilot.Data;
using NodePilot.Data.Availability;
using Npgsql;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Small provider boundary around the probe connection. The production implementation remains raw
/// ADO.NET; the boundary lets the timeout and lifetime rules be tested with a provider that
/// deliberately ignores cancellation.
/// </summary>
internal interface IDatabaseProbeTransport : IAsyncDisposable
{
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken cancellationToken);
    Task ExecuteProbeAsync(int commandTimeoutSeconds, CancellationToken cancellationToken);
}

internal sealed class AdoNetDatabaseProbeTransport(DbConnection connection)
    : IDatabaseProbeTransport
{
    public bool IsOpen => connection.State is ConnectionState.Open;

    public Task OpenAsync(CancellationToken cancellationToken) =>
        connection.OpenAsync(cancellationToken);

    public async Task ExecuteProbeAsync(
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = commandTimeoutSeconds;
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

/// <summary>Resolved once at host construction; tests may provide sub-second hard limits.</summary>
internal sealed record DatabaseProbeRuntime(
    string Provider,
    string ConnectionString,
    int ProviderCommandTimeoutSeconds,
    TimeSpan OpenTimeout,
    TimeSpan CommandTimeout,
    TimeSpan CleanupTimeout,
    TimeSpan IdleInterval,
    TimeSpan OutageInterval,
    Func<IDatabaseProbeTransport> CreateTransport,
    Func<CancellationToken, Task> ClearApplicationPool,
    Func<TimeSpan, CancellationToken, Task> Delay);

/// <summary>
/// The only writer that may declare the database available again. It uses a dedicated unpooled
/// connection and executes a real <c>SELECT 1</c>; a pool checkout or <c>CanConnectAsync</c> is not
/// evidence that a hung-but-listening database can execute commands.
/// </summary>
public sealed class DatabaseAvailabilityProbe : BackgroundService
{
    private const string ConnectionDisposeOperation = "connection_dispose";
    private const string ApplicationPoolClearOperation = "application_pool_clear";

    private static readonly Counter<long> CleanupTimeouts = ApiMetrics.Meter.CreateCounter<long>(
        "nodepilot.database.probe_cleanup_timeouts",
        unit: "1",
        description: "Probe cleanup operations abandoned after their hard deadline.");

    private readonly IDatabaseAvailability _availability;
    private readonly ILogger<DatabaseAvailabilityProbe> _logger;
    private readonly DatabaseProbeRuntime _runtime;
    private readonly string _providerTag;

    private IDatabaseProbeTransport? _connection;
    private long _poolClearAttemptedEpisode;

    public DatabaseAvailabilityProbe(
        IDatabaseAvailability availability,
        IConfiguration configuration,
        DatabaseAvailabilityOptions options,
        ILogger<DatabaseAvailabilityProbe> logger)
        : this(availability, logger, CreateRuntime(configuration, options))
    {
    }

    internal DatabaseAvailabilityProbe(
        IDatabaseAvailability availability,
        ILogger<DatabaseAvailabilityProbe> logger,
        DatabaseProbeRuntime runtime)
    {
        _availability = availability;
        _logger = logger;
        _runtime = runtime;
        _providerTag = runtime.Provider is "sqlserver" ? "sqlserver" : "postgres";

        // Singleton subscribing to a singleton: both live for the process. The tracker owns the
        // monotonic episode identity; this subscriber only counts real outage transitions.
        _availability.StateChanged += OnAvailabilityStateChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_runtime.ConnectionString))
        {
            // Boot validation owns the actionable configuration error. Avoid a hot failure loop if a
            // host is nevertheless assembled without a connection string (notably in unit tests).
            _logger.LogWarning("Database availability probe disabled: no connection string configured.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // This loop is the only path back to Available, so no single provider, logger, or
                // subscriber failure may terminate it.
                _logger.LogDebug(ex, "Database availability probe iteration failed unexpectedly.");
                _availability.ReportProbeFailed(DatabaseOutageReason.Unknown);
            }

            var interval = _availability.State is DatabaseAvailabilityState.Unavailable
                ? _runtime.OutageInterval
                : _runtime.IdleInterval;

            try
            {
                await WaitForNextProbeAsync(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Waiting for the next database probe tick failed; continuing.");
            }
        }
    }

    private async Task WaitForNextProbeAsync(TimeSpan interval, CancellationToken stoppingToken)
    {
        // A token scoped to this one WhenAny is the ownership boundary for both tasks. Whichever
        // finishes first cancels the loser; awaiting both tears down its timer/token registration and
        // observes a concurrent fault before another tick is allowed to allocate a waiter.
        using var iteration = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delay = _runtime.Delay(interval, iteration.Token);
        var requested = _availability.WaitForProbeRequestAsync(iteration.Token);

        await Task.WhenAny(delay, requested).ConfigureAwait(false);
        iteration.Cancel();
        await ObserveIterationTaskAsync(delay, iteration.Token).ConfigureAwait(false);
        await ObserveIterationTaskAsync(requested, iteration.Token).ConfigureAwait(false);
    }

    private static async Task ObserveIterationTaskAsync(Task task, CancellationToken iterationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (iterationToken.IsCancellationRequested)
        {
            // Expected for the loser. Awaiting it is what releases its cancellation registration.
        }
    }

    private async Task ProbeOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            await RunBoundedAsync(
                    ct => connection.ExecuteProbeAsync(
                        _runtime.ProviderCommandTimeoutSeconds, ct),
                    _runtime.CommandTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            // Only a real Unavailable transition constitutes an outage episode. Armed is deliberately
            // servable adjudication and must not churn a healthy application pool.
            var observed = _availability.Snapshot;
            if (observed.CurrentOutage is { } outage)
            {
                if (TryClaimPoolClear(outage.EpisodeId))
                {
                    await ClearApplicationPoolAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            _availability.ReportProbeSucceeded(observed.OutageEpisodeId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Publish evidence before touching a provider object that may itself block forever while
            // disposing a half-open socket. Readiness and request shedding therefore change promptly
            // even if cleanup has to be abandoned at its deadline.
            var reason = ClassifyProbeFailure(ex);
            var previousReason = _availability.CurrentOutage?.Reason;
            try
            {
                _availability.ReportProbeFailed(reason);
                var currentReason = _availability.CurrentOutage?.Reason;

                if (currentReason is DatabaseOutageReason.RejectedByServer
                    && previousReason is not DatabaseOutageReason.RejectedByServer)
                {
                    _logger.LogError(ex,
                        "Database probe was rejected by the server. This is a configuration problem, " +
                        "not an outage: retrying will not fix it.");
                }
                else
                {
                    _logger.LogDebug(ex, "Database probe failed ({Reason}).", reason);
                }
            }
            finally
            {
                await DiscardConnectionAsync(
                        ConnectionDisposeOperation, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static DatabaseOutageReason ClassifyProbeFailure(Exception exception)
    {
        if (exception is DatabaseProbeOpenException openFailure)
            return openFailure.Reason;

        return DbErrorClassifier.Classify(exception) switch
        {
            DbFailureKind.ConnectionRejected => DatabaseOutageReason.RejectedByServer,
            DbFailureKind.CommandTimeout => DatabaseOutageReason.Wedged,
            DbFailureKind.ConnectionFailure => DatabaseOutageReason.Unreachable,
            // Capacity on the probe's own unpooled connection means the server cannot serve a new
            // session, even though application-pool capacity errors are merely local backpressure.
            DbFailureKind.CapacityBackpressure => DatabaseOutageReason.Unreachable,
            _ => DatabaseOutageReason.Unknown,
        };
    }

    private async Task<IDatabaseProbeTransport> EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true }) return _connection;

        await DiscardConnectionAsync(ConnectionDisposeOperation, cancellationToken)
            .ConfigureAwait(false);

        var connection = _runtime.CreateTransport();
        // Assign before Open: if Open fails or times out, the outer failure path can report first and
        // then dispose this exact half-built provider object under the cleanup deadline.
        _connection = connection;

        try
        {
            await RunBoundedAsync(
                    connection.OpenAsync,
                    _runtime.OpenTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            return connection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DatabaseProbeOpenException(ClassifyOpenFailure(ex), ex);
        }
    }

    private static DatabaseOutageReason ClassifyOpenFailure(Exception exception) =>
        DbErrorClassifier.ClassifyConnectionFailure(exception) switch
        {
            DbFailureKind.ConnectionRejected => DatabaseOutageReason.RejectedByServer,
            DbFailureKind.ConnectionFailure => DatabaseOutageReason.Unreachable,
            DbFailureKind.CapacityBackpressure => DatabaseOutageReason.Unreachable,
            _ => DatabaseOutageReason.Unknown,
        };

    private async Task ClearApplicationPoolAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunBoundedAsync(
                    _runtime.ClearApplicationPool,
                    _runtime.CleanupTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DatabaseProbeHardTimeoutException ex)
        {
            RecordCleanupTimeout(ApplicationPoolClearOperation);
            _logger.LogDebug(ex,
                "Clearing the application connection pool exceeded its hard deadline; ignoring.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown owns the deadline; recovery will not be published after the host is stopping.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Clearing the application connection pool after recovery failed; ignoring.");
        }
    }

    private async Task DiscardConnectionAsync(
        string operation,
        CancellationToken cancellationToken)
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null) return;

        try
        {
            await RunBoundedAsync(
                    _ => connection.DisposeAsync().AsTask(),
                    _runtime.CleanupTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DatabaseProbeHardTimeoutException ex)
        {
            RecordCleanupTimeout(operation);
            _logger.LogDebug(ex,
                "Discarding the database probe connection exceeded its hard deadline; ignoring.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Discarding the database probe connection was cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Discarding the database probe connection failed; ignoring.");
        }
    }

    private void RecordCleanupTimeout(string operation) => CleanupTimeouts.Add(
        1,
        new KeyValuePair<string, object?>("provider", _providerTag),
        new KeyValuePair<string, object?>("operation", operation));

    private bool TryClaimPoolClear(long episodeId)
    {
        while (true)
        {
            var claimed = Interlocked.Read(ref _poolClearAttemptedEpisode);
            if (claimed >= episodeId) return false;
            if (Interlocked.CompareExchange(
                    ref _poolClearAttemptedEpisode, episodeId, claimed) == claimed)
                return true;
        }
    }

    private void OnAvailabilityStateChanged(DatabaseAvailabilityState state)
    {
        if (state is not DatabaseAvailabilityState.Unavailable) return;
        ApiMetrics.DatabaseOutages.Add(1);
    }

    private static async Task RunBoundedAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task task;
        try
        {
            task = operation(operationCancellation.Token);
        }
        catch
        {
            operationCancellation.Dispose();
            throw;
        }

        try
        {
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            operationCancellation.Dispose();
        }
        catch (TimeoutException ex) when (!task.IsCompleted)
        {
            ObserveAbandoned(task);
            CancelAndDisposeInBackground(operationCancellation);
            throw new DatabaseProbeHardTimeoutException(timeout, ex);
        }
        catch
        {
            if (!task.IsCompleted) ObserveAbandoned(task);
            CancelAndDisposeInBackground(operationCancellation);
            throw;
        }
    }

    private static void CancelAndDisposeInBackground(CancellationTokenSource source)
    {
        Task cancellation;
        try
        {
            cancellation = source.CancelAsync();
        }
        catch
        {
            source.Dispose();
            return;
        }

        if (cancellation.IsCompleted)
        {
            _ = cancellation.Exception;
            source.Dispose();
            return;
        }

        _ = cancellation.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            source,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveAbandoned(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static DatabaseProbeRuntime CreateRuntime(
        IConfiguration configuration,
        DatabaseAvailabilityOptions options)
    {
        var provider = string.Equals(
            configuration["Database:Provider"], "sqlserver", StringComparison.OrdinalIgnoreCase)
            ? "sqlserver"
            : "postgres";
        var raw = provider is "sqlserver"
            ? configuration.GetConnectionString("DefaultConnection")
            : configuration.GetConnectionString("Postgres");
        var probeConnectionString = DatabaseConnectionString.ForProbe(
            provider,
            raw,
            options.ProbeConnectTimeoutSeconds,
            options.ProbeCommandTimeoutSeconds);
        var applicationConnectionString = DatabaseConnectionString.EnsureConnectTimeout(
            provider,
            raw,
            options.ConnectTimeoutSeconds);

        return new DatabaseProbeRuntime(
            Provider: provider,
            ConnectionString: probeConnectionString,
            ProviderCommandTimeoutSeconds: options.ProbeCommandTimeoutSeconds,
            OpenTimeout: TimeSpan.FromSeconds(options.ProbeConnectTimeoutSeconds),
            CommandTimeout: TimeSpan.FromSeconds(options.ProbeCommandTimeoutSeconds),
            CleanupTimeout: TimeSpan.FromSeconds(options.CleanupTimeoutSeconds),
            IdleInterval: TimeSpan.FromSeconds(options.IdleIntervalSeconds),
            OutageInterval: TimeSpan.FromSeconds(options.OutageIntervalSeconds),
            CreateTransport: () => new AdoNetDatabaseProbeTransport(
                provider is "sqlserver"
                    ? new SqlConnection(probeConnectionString)
                    : new NpgsqlConnection(probeConnectionString)),
            ClearApplicationPool: cancellationToken =>
                string.IsNullOrWhiteSpace(applicationConnectionString)
                    ? Task.CompletedTask
                    : Task.Run(
                        () => ClearApplicationPool(provider, applicationConnectionString),
                        cancellationToken),
            Delay: static (interval, cancellationToken) =>
                Task.Delay(interval, cancellationToken));
    }

    private static void ClearApplicationPool(string provider, string connectionString)
    {
        if (provider is "sqlserver")
        {
            using var poolKey = new SqlConnection(connectionString);
            SqlConnection.ClearPool(poolKey);
            return;
        }

        using var postgresPoolKey = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(postgresPoolKey);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await DiscardConnectionAsync(ConnectionDisposeOperation, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed class DatabaseProbeOpenException(
        DatabaseOutageReason reason,
        Exception innerException)
        : Exception("Opening the dedicated database probe connection failed.", innerException)
    {
        public DatabaseOutageReason Reason { get; } = reason;
    }

    private sealed class DatabaseProbeHardTimeoutException(
        TimeSpan timeout,
        Exception innerException)
        : TimeoutException(
            $"The database probe operation exceeded its hard deadline of {timeout}.",
            innerException);
}
