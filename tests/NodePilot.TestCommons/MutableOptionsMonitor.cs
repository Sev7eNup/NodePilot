using Microsoft.Extensions.Options;

namespace NodePilot.TestCommons;

/// <summary>
/// Settable <see cref="IOptionsMonitor{T}"/> test double for hot-reload tests. Unlike
/// <see cref="StaticOptionsMonitor{T}"/>, <see cref="CurrentValue"/> is writable and changing it
/// (via <see cref="Set"/> or the property setter) fans out to every registered
/// <see cref="OnChange"/> listener — mirroring how a real <c>reloadOnChange</c> config reload
/// notifies <c>IOptionsMonitor&lt;T&gt;</c> consumers. Use this to drive live-mutation tests
/// (mutate the monitor between two acts and assert the consumer picked up the new value).
///
/// <para>Single shared copy for every test suite (consolidated during a July 2026
/// codebase-consistency cleanup) — was duplicated in Engine.Tests + Ai.Tests Helpers and inline
/// in Api.Tests.</para>
/// </summary>
public sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = new();
    private T _current;

    public MutableOptionsMonitor(T initial) { _current = initial; }

    public T CurrentValue
    {
        get => _current;
        set => Set(value);
    }

    public T Get(string? name) => _current;

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Unsub(() => _listeners.Remove(listener));
    }

    /// <summary>Replaces the current value and notifies every registered listener.</summary>
    public void Set(T value, string? name = null)
    {
        _current = value;
        // Snapshot so a listener unsubscribing during fan-out doesn't mutate the iterated list.
        foreach (var listener in _listeners.ToArray())
            listener(value, name);
    }

    private sealed class Unsub : IDisposable
    {
        private Action? _remove;
        public Unsub(Action remove) { _remove = remove; }
        public void Dispose() { _remove?.Invoke(); _remove = null; }
    }
}
