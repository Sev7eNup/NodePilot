namespace NodePilot.Api.Dtos;

/// <summary>
/// Snapshot for the live-ops Mission-Control view at <c>/operations</c>. RBAC-folder-scoped: only
/// workflows the caller can read appear as nodes, and call edges resolve only against that
/// scoped set (so a reference to a workflow outside the caller's folders shows as unresolved,
/// never leaking its existence). Live status deltas arrive separately over the SignalR
/// <c>ops-feed</c> group; this is the initial paint: nodes, static call topology, currently
/// running executions and the recently finished ones for the live timeline.
/// <para>
/// <c>Density</c> carries per-workflow run counts over the window and is populated ONLY when
/// <c>Recent</c> was capped. It is what makes the window selector mean something on a busy system:
/// individual bars cannot cover four hours (that is thousands of rows re-sent every poll and
/// re-positioned every clock tick), so the stretch the raw list does not reach comes back as an
/// aggregate instead of being left blank. Empty on a quiet system — nothing is computed and
/// nothing is shipped when the raw list already covers the window.
/// </para>
/// </summary>
public record OperationsGraphDto(
    IReadOnlyList<OpsNode> Nodes,
    IReadOnlyList<OpsEdge> Edges,
    IReadOnlyList<OpsRunningExecution> Running,
    IReadOnlyList<OpsRecentExecution> Recent,
    IReadOnlyList<OpsDensityLane> Density,
    OpsSnapshotMeta Meta);

/// <summary>Snapshot-wide settings the console needs to render consistently with the backend.</summary>
/// <param name="OverdueSeconds">
/// A <c>Running</c> execution older than this counts as overdue in the timeline. Sourced from
/// <c>Alerting:LongRunningSeconds</c> and floored with <c>Math.Max(1, …)</c> — byte-for-byte the
/// same derivation as <c>LongRunningExecutionCollector</c>, so the console highlights a run at
/// exactly the moment the alerting rule would fire for it. Two disagreeing "long-running"
/// numbers would be worse than none. Read raw per request: the section is hot-reloadable.
/// </param>
/// <param name="WindowMinutes">The clamped window this snapshot was built for (20 | 60 | 240).</param>
/// <param name="RecentSinceUtc">
/// Left edge the caller ASKED for (<c>now - WindowMinutes</c>). Not necessarily the edge of the
/// data actually returned — see <paramref name="OldestReturnedCompletedAt"/>.
/// </param>
/// <param name="OldestReturnedCompletedAt">
/// Completion time of the oldest settled run actually in <c>Recent</c>, or null when the list is
/// empty. When the cap bit, this sits to the RIGHT of <paramref name="RecentSinceUtc"/> — the
/// console draws its "no history" band from here, not from the requested edge, otherwise the
/// band would cover exactly the stretch the truncation did NOT lose.
/// </param>
/// <param name="RecentTruncated">
/// True when more settled runs existed in the window than the cap returns — i.e. exactly when
/// <c>Density</c> is populated. Surfaced so an empty stretch of track is never silently read as
/// "nothing ran".
/// </param>
/// <param name="DensityBucketSeconds">
/// Width of one <see cref="OpsDensityBucket"/>, so the console can turn a bucket index back into
/// a time range: bucket <c>i</c> spans <c>[RecentSinceUtc + i·s, RecentSinceUtc + (i+1)·s)</c>.
/// Zero when no density was computed. Sized to hit a fixed bucket count at every window, which is
/// what keeps the aggregate's cost independent of how busy the system is.
/// </param>
/// <param name="DensityCapped">
/// The density scan hit its own row cap, so the aggregate describes the newest N settled runs in
/// the window rather than all of them. Separate from <paramref name="RecentTruncated"/> on
/// purpose: that one says "not every run is a bar", this one says "not every run is even counted".
/// </param>
public record OpsSnapshotMeta(
    int OverdueSeconds,
    int WindowMinutes,
    DateTime RecentSinceUtc,
    DateTime? OldestReturnedCompletedAt,
    bool RecentTruncated,
    int DensityBucketSeconds,
    bool DensityCapped);

/// <param name="RunningCount">Live count of Running/Pending executions at snapshot time.</param>
/// <param name="LastStatus">Status of the most recent execution (from WorkflowStats), or null if never run.</param>
/// <param name="CallFrequency">Run count in the stats window — drives node-size-by-throughput. Null if no stats row.</param>
/// <param name="CanRun">
/// Caller may cancel / retry / cancel-all on THIS workflow: folder <c>ResourceOp.Run</c>, already
/// ANDed with the global role by <c>GetWorkflowCapabilitiesAsync</c>. Per-node on purpose — the
/// global role alone is not the answer, and a single snapshot-wide flag made the console offer
/// buttons that the endpoints then 403'd for a global Operator holding only folder-Viewer.
/// </param>
/// <param name="CanEdit">
/// Caller may disable / quarantine THIS workflow: folder <c>ResourceOp.Edit</c>. Strictly
/// different from <see cref="CanRun"/> — <c>POST /workflows/{id}/disable</c> requires Edit
/// while cancel requires only Run.
/// </param>
public record OpsNode(
    Guid WorkflowId,
    string Name,
    Guid FolderId,
    string FolderPath,
    bool IsEnabled,
    int RunningCount,
    string? LastStatus,
    int? CallFrequency,
    bool CanRun,
    bool CanEdit);

/// <param name="Target">Resolved target workflow id; null for dynamic/unresolved/ambiguous refs.</param>
/// <param name="Kind"><c>startWorkflow</c> or <c>forEach</c>.</param>
/// <param name="RefStatus"><c>Resolved</c> | <c>Dynamic</c> | <c>Unresolved</c> | <c>Ambiguous</c>.</param>
/// <param name="RawRef">Original reference string — shown for non-resolved (dynamic/unresolved/ambiguous) edges.</param>
public record OpsEdge(
    string Id,
    Guid Source,
    Guid? Target,
    string Kind,
    string RefStatus,
    string RawRef,
    int CallCount);

/// <param name="ParentExecutionId">Parent run for sub-workflow executions (startWorkflow/forEach)
/// — lets the timeline draw call connectors between parent and child bars.</param>
/// <param name="StepsFinished">
/// Steps of this run that have reached a terminal state. Monotonic, and the only step figure that
/// is trustworthy for a LIVE run: <c>Engine:DeferRunningStateWrite</c> defaults to true, so a
/// step's row is written once, at its terminal state — an in-flight step has no row at all.
/// <para>
/// This is observed ACTIVITY, not progress. There is deliberately no percentage: the obvious
/// denominators are all wrong. Step rows include executed trigger nodes and later Skipped rows,
/// while <c>Workflow.ActivityCount</c> excludes triggers and disabled nodes — and loops re-run
/// nodes, so no fixed total exists. A bar that reads "100 %" and then falls back to 80 % on the
/// next loop iteration is worse than no bar.
/// </para>
/// <para>
/// NULL means "not enriched" (the run fell outside the enrichment cap), never "nothing happened".
/// Zero would be a false statement; the console renders nothing for null.
/// </para>
/// </param>
/// <param name="LastCompletedStepName">Label of the most recently EXECUTED step (falls back to its step id); null when none has finished yet or the run was not enriched.</param>
/// <param name="LastProgressAt">
/// When the most recent step finished. The actual answer to "is it working or is it hung?" —
/// a run whose last progress was eleven minutes ago is stuck on one step, which no step COUNT
/// can express. <b>Skipped rows are excluded</b>: a control-flow branch that never executed is
/// not progress, and letting it count would reset the stagnation clock for free. Null when not
/// enriched or nothing has executed yet.
/// </param>
/// <param name="ActiveStepCount">
/// How many of this run's steps are currently in flight, derived from started-minus-finished.
/// Present because a single "current step" is simply wrong under parallelism.
/// </param>
public record OpsRunningExecution(
    Guid ExecutionId,
    Guid WorkflowId,
    string Status,
    DateTime StartedAt,
    Guid? ParentExecutionId,
    int? StepsFinished,
    string? LastCompletedStepName,
    DateTime? LastProgressAt,
    int? ActiveStepCount);

/// <summary>
/// Terminal execution completed within the requested window, newest first and capped — on a busy
/// system the runs past the cap come back aggregated in <see cref="OpsDensityLane"/> instead of
/// being dropped. Slim on purpose: rich details (error, triggeredBy, parent) come from
/// <c>GET /api/executions/{id}</c> on drill-down.
/// </summary>
/// <param name="ParentExecutionId">Parent run for sub-workflow executions — see <see cref="OpsRunningExecution"/>.</param>
public record OpsRecentExecution(
    Guid ExecutionId,
    Guid WorkflowId,
    string Status,
    DateTime StartedAt,
    DateTime CompletedAt,
    Guid? ParentExecutionId);

/// <summary>
/// Settled-run counts for one workflow, bucketed over the window. Nested under the workflow rather
/// than carrying a <c>WorkflowId</c> per bucket: at ~48 buckets × N workflows the repeated GUID
/// would be the bulk of the payload, and the console wants them grouped by lane anyway.
/// </summary>
/// <param name="WorkflowId">The lane these counts belong to.</param>
/// <param name="Buckets">Only buckets with at least one run. Ascending by index; index 0 is the
/// oldest bucket, starting at <c>Meta.RecentSinceUtc</c>.</param>
public record OpsDensityLane(
    Guid WorkflowId,
    IReadOnlyList<OpsDensityBucket> Buckets);

/// <summary>
/// One time slice of one workflow's settled runs. Outcome is split rather than reduced to a single
/// "worst status": a slice holding nineteen successes and one failure is neither green nor red,
/// and collapsing it either way is a lie the operator would act on.
/// </summary>
/// <param name="BucketIndex">Slice offset from <c>Meta.RecentSinceUtc</c>, in <c>Meta.DensityBucketSeconds</c> steps.</param>
/// <param name="Total">All settled runs in the slice, whatever their status.</param>
/// <param name="Failed">Runs that ended <c>Failed</c>.</param>
/// <param name="Cancelled">Runs that ended <c>Cancelled</c>.</param>
public record OpsDensityBucket(
    int BucketIndex,
    int Total,
    int Failed,
    int Cancelled);

