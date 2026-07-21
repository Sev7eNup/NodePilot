using Microsoft.Extensions.Options;

namespace NodePilot.TestCommons;

/// <summary>
/// Minimal immutable <see cref="IOptionsMonitor{T}"/> test double for consumers that swapped from
/// <c>IOptions&lt;T&gt;</c> to <c>IOptionsMonitor&lt;T&gt;</c> for hot-reload. <see cref="OnChange"/>
/// returns null and never fires — sufficient for tests that just need a fixed <c>CurrentValue</c>.
/// For tests that exercise live mutation, use <see cref="MutableOptionsMonitor{T}"/> instead.
///
/// <para>Single shared copy for every test suite. Previously duplicated as a Helpers class in
/// Engine.Tests + Ai.Tests and re-declared inline as a private nested class in ~11 Api.Tests
/// files — all collapsed here (consolidated during a July 2026 codebase-consistency
/// cleanup).</para>
/// </summary>
public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) { CurrentValue = value; }
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
