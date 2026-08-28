namespace NodePilot.Data.Availability;

/// <summary>
/// What the process currently believes about the database it depends on.
///
/// <para><b>Why four states and not two.</b> A hung-but-listening server can leave a pooled
/// connection producing <i>only</i> command timeouts — a pool checkout succeeds without touching
/// the server, so nothing ever reports a failed connection. A two-state machine whose only opening
/// edge starts at "connection failed" therefore never opens in the scenario it exists for.
/// <see cref="Armed"/> is that missing edge.</para>
/// </summary>
public enum DatabaseAvailabilityState
{
    /// <summary>
    /// Process start until the inline boot block finishes migrating and seeding. Interceptors are
    /// inert here: <c>DatabaseReadinessGate</c> polls a database that is routinely late at boot,
    /// and letting those probes open the breaker would disable retries during migration.
    /// </summary>
    Booting = 0,

    /// <summary>The probe answered <c>SELECT 1</c>. Normal operation.</summary>
    Available,

    /// <summary>
    /// A command timed out somewhere and the probe is adjudicating. Still fully served — a single
    /// slow query is not an outage, and treating it as one is the failure mode this design exists
    /// to avoid.
    /// </summary>
    Armed,

    /// <summary>
    /// The breaker is open. <c>/api</c> answers 503 without touching the database.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Why the breaker opened, in terms an operator can act on. Deliberately coarser than
/// <see cref="DbFailureKind"/>: this value is serialised onto an anonymous health
/// endpoint, so it must not leak provider internals, error numbers or connection details.
/// </summary>
public enum DatabaseOutageReason
{
    /// <summary>
    /// No classification survived — treated as an outage, but nothing specific to report.
    /// </summary>
    Unknown = 0,

    /// <summary>Nothing is listening, or the transport died. Retrying is the right move.</summary>
    Unreachable,

    /// <summary>
    /// The server answered and declined: bad credentials, missing database, or failed TLS
    /// verification. <b>Not transient.</b> The probe keeps trying because there is no other way
    /// back once the configuration is fixed, but this must never be presented as "reconnecting,
    /// please wait".
    /// </summary>
    RejectedByServer,

    /// <summary>The server accepts connections but does not finish statements.</summary>
    Wedged,
}

/// <summary>
/// An open outage. <c>null</c> whenever the state is not
/// <see cref="DatabaseAvailabilityState.Unavailable"/>.
/// </summary>
/// <param name="SinceUtc">When the breaker opened.</param>
/// <param name="Reason">
/// Classified cause, shown in the log line, the health endpoint, and the banner's escalation copy.
/// </param>
/// <param name="ConsecutiveProbeFailures">
/// Consecutive failed probe attempts since the most recent successful probe; for diagnostics.
/// </param>
/// <param name="EpisodeId">
/// Process-local, monotonically increasing identity of this outage episode.
/// </param>
public sealed record DatabaseOutage(
    DateTime SinceUtc,
    DatabaseOutageReason Reason,
    int ConsecutiveProbeFailures,
    long EpisodeId);

/// <summary>
/// One lock-consistent view of the breaker. <see cref="OutageEpisodeId"/> keeps the most recently
/// assigned episode after recovery, so a successful probe can prove which state it observed
/// before publishing its result.
/// </summary>
public sealed record DatabaseAvailabilitySnapshot(
    DatabaseAvailabilityState State,
    DatabaseOutage? CurrentOutage,
    long OutageEpisodeId);

/// <summary>
/// The process-wide database availability breaker.
///
/// <para><b>The invariant:</b> after boot, <i>only the probe may publish
/// <see cref="DatabaseAvailabilityState.Available"/></i>. Interceptors may only degrade, and every
/// degrading transition is idempotent.</para>
///
/// <para>A pool checkout can succeed with zero contact with the server, so a "connection opened"
/// event is not evidence that the database is alive — wiring it to recovery would reset the
/// breaker on every operation and make it impossible to trip. The only honest liveness test is
/// a statement that came back, which is what the probe does and what nothing else in the
/// process may claim.</para>
/// </summary>
public interface IDatabaseAvailability
{
    /// <summary>
    /// Full truth. Prefer <see cref="IsServable"/> for "may I touch the database".
    /// </summary>
    DatabaseAvailabilityState State { get; }

    /// <summary>
    /// False only while the breaker is open.
    /// <see cref="DatabaseAvailabilityState.Armed"/> is servable on purpose: it means one query
    /// was slow, which must not stop the whole installation.
    /// </summary>
    bool IsServable { get; }

    /// <summary>Non-null exactly when the breaker is open.</summary>
    DatabaseOutage? CurrentOutage { get; }

    /// <summary>
    /// State, outage details and episode identity captured under one tracker lock.
    /// </summary>
    DatabaseAvailabilitySnapshot Snapshot { get; }

    /// <summary>
    /// Boot finished; the process may now serve.
    /// The one and only legal caller is <c>Program.cs</c>.
    /// </summary>
    void MarkBootComplete();

    /// <summary>
    /// Degrade. Called from the connection interceptor and from the probe. Never closes the
    /// breaker, never throws, idempotent while already open.
    /// </summary>
    void ReportUnreachable(DatabaseOutageReason reason);

    /// <summary>
    /// A command timed out. Moves <see cref="DatabaseAvailabilityState.Available"/> to
    /// <see cref="DatabaseAvailabilityState.Armed"/> and wakes the probe, which adjudicates. Does
    /// not
    /// open the breaker by itself — that is the whole point of the state.
    /// </summary>
    void Arm();

    /// <summary>
    /// The probe got an answer. The only path back to <see
    /// cref="DatabaseAvailabilityState.Available"/>.
    /// A result observed before a newer outage episode opened is ignored rather than resurrecting
    /// it.
    /// </summary>
    void ReportProbeSucceeded(long observedOutageEpisodeId = -1);

    /// <summary>The probe did not get an answer.</summary>
    void ReportProbeFailed(DatabaseOutageReason reason);

    /// <summary>
    /// Completes once the database is servable again. Returns <c>false</c> instead of throwing when
    /// <paramref name="cancellationToken"/> fires, because hosted-service call sites use it as
    /// <c>if (!await …) break;</c> and <c>BackgroundServiceExceptionBehavior</c> is left at its
    /// default
    /// <c>StopHost</c> — an escaping <c>OperationCanceledException</c> would take the host down on
    /// every shutdown.
    /// </summary>
    Task<bool> WaitUntilServableAsync(CancellationToken cancellationToken);

    /// <summary>Completes when someone arms the probe, so the probe can idle instead of
    /// spinning.</summary>
    Task WaitForProbeRequestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Raised once per <i>transition</i>, never per report. That distinction is what turns "an
    /// ERROR
    /// every 5 seconds for the whole outage" into two log lines.
    /// </summary>
    event Action<DatabaseAvailabilityState>? StateChanged;

    /// <summary>
    /// Raised once when a real <see cref="DatabaseAvailabilityState.Unavailable"/> episode
    /// recovers.
    /// The payload is captured under the transition lock and remains correct across a subsequent
    /// flap.
    /// </summary>
    event Action<long>? OutageRecovered;
}
