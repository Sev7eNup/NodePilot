using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Activities;

/// <summary>
/// In-process default for <see cref="IWorkflowConcurrencyGate"/>. One entry per workflow that
/// currently has running or waiting executions; entries are dropped once a workflow goes idle,
/// so the map stays bounded by the number of concurrently active workflows.
/// </summary>
public sealed class InMemoryWorkflowConcurrencyGate : IWorkflowConcurrencyGate
{
    /// <summary>
    /// How long a "workflow is at its limit" entry stays in <see cref="BlockedWorkflowIds"/>
    /// without being re-confirmed. The dispatcher skips blocked workflows when claiming, so
    /// without an expiry a limit raised outside <see cref="SetLimit"/> would never be re-read.
    /// The cost of expiry is one refused claim per interval per blocked workflow.
    /// </summary>
    internal static readonly TimeSpan BlockedEntryTtl = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, State> _states = [];
    private readonly TimeProvider _time;

    public InMemoryWorkflowConcurrencyGate() : this(null) { }

    public InMemoryWorkflowConcurrencyGate(TimeProvider? timeProvider)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    public bool TryAcquire(Guid workflowId, int? observedLimit)
    {
        lock (_gate)
        {
            var state = GetOrCreate(workflowId, observedLimit);
            var acquired = HasRoom(state);
            if (acquired) state.Active++;
            Settle(workflowId, state);
            return acquired;
        }
    }

    public Task AcquireAsync(Guid workflowId, int? observedLimit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_gate)
        {
            var state = GetOrCreate(workflowId, observedLimit);
            if (HasRoom(state))
            {
                state.Active++;
                Settle(workflowId, state);
                return Task.CompletedTask;
            }

            waiter = new Waiter();
            state.Waiters.Enqueue(waiter);
            Settle(workflowId, state);
        }

        return WaitForSlotAsync(waiter, cancellationToken);
    }

    public void Release(Guid workflowId)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(workflowId, out var state)) return;

            // Decrement first, then hand out what the CURRENT limit allows. Passing the slot
            // straight to the next waiter would keep Active at its old level, so a lowered
            // limit would never take effect while anyone is queued.
            if (state.Active > 0) state.Active--;
            Wake(state);
            Settle(workflowId, state);
        }
    }

    public void SetLimit(Guid workflowId, int? limit)
    {
        lock (_gate)
        {
            // Idle workflows hold no entry: nothing is counted or blocked, and the next acquire
            // seeds the fresh value from its own row read.
            if (!_states.TryGetValue(workflowId, out var state)) return;

            state.Limit = limit;
            state.LimitIsPushed = true;
            // A raise releases every waiter the new capacity allows at once, not one per
            // completing run.
            Wake(state);
            Settle(workflowId, state);
        }
    }

    public Guid[] BlockedWorkflowIds
    {
        get
        {
            var cutoff = _time.GetUtcNow() - BlockedEntryTtl;
            lock (_gate)
            {
                if (_states.Count == 0) return [];
                return _states
                    .Where(entry => entry.Value.IsBlocked && entry.Value.BlockedSince > cutoff)
                    .Select(entry => entry.Key)
                    .ToArray();
            }
        }
    }

    /// <summary>Active count for a workflow. Tests and diagnostics only.</summary>
    internal int ActiveCount(Guid workflowId)
    {
        lock (_gate)
            return _states.TryGetValue(workflowId, out var state) ? state.Active : 0;
    }

    /// <summary>Number of tracked workflows. Guards against entries leaking. Tests only.</summary>
    internal int TrackedWorkflowCount
    {
        get { lock (_gate) return _states.Count; }
    }

    private State GetOrCreate(Guid workflowId, int? observedLimit)
    {
        if (!_states.TryGetValue(workflowId, out var state))
        {
            state = new State { Limit = observedLimit };
            _states[workflowId] = state;
            return state;
        }

        // A value pushed by a write path wins over an observation, which may come from a row
        // read before that write. The pushed value lives only as long as the entry, and an idle
        // workflow drops its entry, so the next observation reseeds from a fresh read.
        if (!state.LimitIsPushed) state.Limit = observedLimit;
        return state;
    }

    /// <summary>
    /// Treats a non-positive limit as unlimited, matching how the engine-wide caps in
    /// <c>WorkflowEngine.CheckCapacityCaps</c> read their configuration. The API rejects 0, so
    /// this only covers a value that reached the database by another route.
    /// </summary>
    private static bool HasRoom(State state)
        => state.Limit is not { } max || max <= 0 || state.Active < max;

    private static void Wake(State state)
    {
        while (state.Waiters.Count > 0 && HasRoom(state))
        {
            // A waiter that lost the race to its own cancellation does not consume the slot.
            if (state.Waiters.Dequeue().Completion.TrySetResult(true))
                state.Active++;
        }
    }

    /// <summary>
    /// Refreshes the blocked flag and drops the entry once the workflow is idle. Re-stamps
    /// <c>BlockedSince</c> on every confirmation, which is what lets an expired entry return to
    /// <see cref="BlockedWorkflowIds"/> after the next refused acquire.
    /// </summary>
    private void Settle(Guid workflowId, State state)
    {
        while (state.Waiters.Count > 0 && state.Waiters.Peek().Completion.Task.IsCompleted)
            state.Waiters.Dequeue();

        if (HasRoom(state))
        {
            state.IsBlocked = false;
        }
        else
        {
            state.IsBlocked = true;
            state.BlockedSince = _time.GetUtcNow();
        }

        if (state.Active == 0 && state.Waiters.Count == 0)
            _states.Remove(workflowId);
    }

    private async Task WaitForSlotAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            static (state, token) => ((Waiter)state!).Completion.TrySetCanceled(token),
            waiter);
        await waiter.Completion.Task.ConfigureAwait(false);
    }

    private sealed class Waiter
    {
        public readonly TaskCompletionSource<bool> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class State
    {
        public int Active;
        public int? Limit;
        public bool LimitIsPushed;
        public bool IsBlocked;
        public DateTimeOffset BlockedSince;
        public readonly Queue<Waiter> Waiters = new();
    }
}
