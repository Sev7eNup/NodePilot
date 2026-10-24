using System.Text;

namespace NodePilot.Core.Execution;

/// <summary>A shared character budget for all output streams of one script invocation.</summary>
public sealed class ScriptOutputBudget : IDisposable
{
    public const string ConfigurationKey = "Engine:MaxCapturedOutputChars";
    // Fits Start Program's two capped 1 MiB streams, including JSON escape expansion.
    public const int DefaultLimit = 16 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _limitReached = new();
    private readonly int _limit;
    private int _captured;
    private bool _disposed;

    public ScriptOutputBudget(int limit = DefaultLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
    }

    public CancellationToken LimitReached => _limitReached.Token;
    public bool Exceeded { get; private set; }
    public string Error => $"Script output limit of {_limit} characters exceeded ({ConfigurationKey}).";
    public Buffer CreateBuffer() => new(this);

    public sealed class Buffer(ScriptOutputBudget budget)
    {
        private readonly StringBuilder _text = new();
        public string Text { get { lock (budget._gate) return _text.ToString(); } }

        public void Append(ReadOnlySpan<char> value)
        {
            lock (budget._gate)
            {
                if (budget._disposed || budget.Exceeded) return;
                var count = Math.Min(value.Length, budget._limit - budget._captured);
                _text.Append(value[..count]);
                budget._captured += count;
                if (count < value.Length)
                {
                    budget.Exceeded = true;
                    budget._limitReached.Cancel();
                }
            }
        }

        public void AppendLine(string value)
        {
            Append(value);
            Append(Environment.NewLine);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _limitReached.Dispose();
        }
    }
}
