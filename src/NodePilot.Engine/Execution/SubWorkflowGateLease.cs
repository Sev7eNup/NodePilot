using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Execution;

/// <summary>Counts runnable child workflows while suspended ancestors lend their slot (ADR 0018).</summary>
internal static class SubWorkflowGateLease
{
    private static readonly AsyncLocal<Lease?> CurrentLease = new();
    private static readonly AsyncLocal<Participant?> CurrentParticipant = new();

    internal static async Task<T> RunAsync<T>(ISubWorkflowGate gate, Func<Task<T>> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var lease = new Lease(gate);
        var participant = new Participant(lease);
        var previousLease = CurrentLease.Value;
        var previousParticipant = CurrentParticipant.Value;
        CurrentLease.Value = lease;
        CurrentParticipant.Value = participant;
        try { return await work().ConfigureAwait(false); }
        finally
        {
            CurrentLease.Value = previousLease;
            CurrentParticipant.Value = previousParticipant;
            await participant.SuspendAsync().ConfigureAwait(false);
        }
    }

    internal static async Task RunSchedulerAsync(Func<Task> work, CancellationToken ct)
        => await RunWithCurrentSlotReleasedAsync(async () =>
        {
            await work().ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

    internal static async Task<T> RunStepAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (CurrentLease.Value is not { } lease) return await work().ConfigureAwait(false);
        var participant = new Participant(lease, active: false);
        await participant.ResumeAsync(ct).ConfigureAwait(false);
        var previous = CurrentParticipant.Value;
        CurrentParticipant.Value = participant;
        try { return await work().ConfigureAwait(false); }
        finally
        {
            CurrentParticipant.Value = previous;
            await participant.SuspendAsync().ConfigureAwait(false);
        }
    }

    internal static async Task<T> RunWithCurrentSlotReleasedAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (CurrentParticipant.Value is not { } participant) return await work().ConfigureAwait(false);
        await participant.SuspendAsync().ConfigureAwait(false);
        CurrentParticipant.Value = null;
        try { return await work().ConfigureAwait(false); }
        finally
        {
            CurrentParticipant.Value = participant;
            await participant.ResumeAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class Lease(ISubWorkflowGate gate)
    {
        private readonly SemaphoreSlim _state = new(1, 1);
        private int _active = 1;

        internal async Task EnterAsync(CancellationToken ct)
        {
            await _state.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_active == 0) await gate.WaitAsync(ct).ConfigureAwait(false);
                _active++;
            }
            finally { _state.Release(); }
        }

        internal async Task LeaveAsync()
        {
            await _state.WaitAsync().ConfigureAwait(false);
            try
            {
                if (--_active == 0) gate.Release();
            }
            finally { _state.Release(); }
        }
    }

    private sealed class Participant(Lease lease, bool active = true)
    {
        private bool _active = active;

        internal async Task SuspendAsync()
        {
            if (!_active) return;
            _active = false;
            await lease.LeaveAsync().ConfigureAwait(false);
        }

        internal async Task ResumeAsync(CancellationToken ct)
        {
            if (_active) return;
            await lease.EnterAsync(ct).ConfigureAwait(false);
            _active = true;
        }
    }
}
