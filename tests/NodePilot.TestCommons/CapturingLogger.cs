using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NodePilot.TestCommons;

/// <summary>
/// <see cref="ILogger{T}"/> test double that records every log call (level, formatted message,
/// exception) for assertions. Backed by a <see cref="ConcurrentQueue{T}"/> because some
/// subjects such as the database availability probe log from background threads. Shared across
/// Engine.Tests, Api.Tests, and Data.Tests.
/// </summary>
public class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

    /// <summary>Snapshot of all recorded entries, in log order.</summary>
    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _entries.ToArray();

    /// <summary>Just the formatted messages — for tests that don't care about
    /// level/exception.</summary>
    public IReadOnlyList<string> Messages => _entries.Select(e => e.Message).ToArray();

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception), exception));
}

/// <summary>
/// Non-generic form for subjects that take a plain <see cref="ILogger"/>
/// (e.g. <c>MigrationBootstrapper.Bootstrap</c>). <see cref="ILogger{T}"/> extends
/// <see cref="ILogger"/>, so the generic base already satisfies the contract.
/// </summary>
public sealed class CapturingLogger : CapturingLogger<object>;
