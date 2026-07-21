namespace NodePilot.Api.Security.Ldap;

/// <summary>
/// Three-state circuit breaker for the LDAP path. Without it, every login attempt during
/// a DC outage waits the full TCP-connect timeout (5 s default) before falling back to
/// local. A burst of 50 logins at the moment the DC dies would queue 250 s of dead work
/// even though the verdict is already known after the first three failures.
/// <para>
/// State transitions:
/// <list type="bullet">
/// <item><b>Closed</b> — normal operation. Failures increment a counter; reaching the
/// threshold flips to Open.</item>
/// <item><b>Open</b> — every attempt fast-fails. After the cooldown elapses we move to
/// HalfOpen on the next probe.</item>
/// <item><b>HalfOpen</b> — exactly one probe is allowed through. Success → Closed and the
/// failure counter resets; failure → Open with a fresh cooldown.</item>
/// </list>
/// </para>
/// Thread-safe via a single lock — contention is irrelevant at login rates and avoids the
/// subtle correctness bugs of a lock-free version (interleaved CAS races between
/// Closed→Open and HalfOpen→Closed are easy to miss).
/// </summary>
public sealed class LdapCircuitBreaker
{
    public enum State { Closed, Open, HalfOpen }

    private readonly object _gate = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly Func<DateTimeOffset> _now;

    private State _state = State.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;

    public LdapCircuitBreaker(int failureThreshold = 5, TimeSpan? openDuration = null, Func<DateTimeOffset>? now = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);
        _failureThreshold = failureThreshold;
        _openDuration = openDuration ?? TimeSpan.FromSeconds(30);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Current state — surfaced for tests + a future health-check endpoint.</summary>
    public State CurrentState
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>
    /// Returns <c>true</c> when the caller is allowed to attempt an LDAP operation.
    /// In Open state this is <c>false</c> until the cooldown elapses; the first call after
    /// cooldown returns <c>true</c> and silently transitions to HalfOpen so subsequent
    /// concurrent callers stay blocked until that probe reports back.
    /// </summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            switch (_state)
            {
                case State.Closed:
                    return true;
                case State.HalfOpen:
                    // Only one probe at a time. Subsequent callers see the open-style refusal.
                    return false;
                case State.Open:
                    if (_now() - _openedAt >= _openDuration)
                    {
                        _state = State.HalfOpen;
                        return true;
                    }
                    return false;
                default:
                    return false;
            }
        }
    }

    /// <summary>Must be called after every <see cref="TryAcquire"/> that returned true.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            _state = State.Closed;
            _consecutiveFailures = 0;
        }
    }

    /// <summary>Must be called after every <see cref="TryAcquire"/> that returned true.</summary>
    public void RecordFailure()
    {
        lock (_gate)
        {
            // A failure during HalfOpen re-opens the circuit immediately — we just learned
            // that the directory is still down.
            if (_state == State.HalfOpen)
            {
                _state = State.Open;
                _openedAt = _now();
                return;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
            {
                _state = State.Open;
                _openedAt = _now();
            }
        }
    }
}
