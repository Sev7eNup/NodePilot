using System.Text.Json;

namespace NodePilot.Engine.Execution;

/// <summary>
/// Per-step retry configuration. Parsed out of the node's <c>config.retry</c> block.
///
/// <para>
/// Default is "no retry" — a single attempt with zero delay. Callers use
/// <see cref="Parse"/> which returns <see cref="Disabled"/> for any missing or
/// malformed config rather than throwing, so a bad retry block never prevents a
/// workflow from running at all.
/// </para>
///
/// <para>Schema:</para>
/// <code>
/// "retry": {
///   "maxAttempts":   3,             // int, inclusive of first attempt; 1 = no retry
///   "backoff":       "exponential", // "fixed" | "linear" | "exponential"
///   "initialDelayMs": 1000,         // int, delay before attempt 2 (and base for grow)
///   "maxDelayMs":     30000         // int, cap for linear/exponential
/// }
/// </code>
/// </summary>
public readonly record struct RetryPolicy(
    int MaxAttempts,
    RetryBackoff Backoff,
    int InitialDelayMs,
    int MaxDelayMs)
{
    public static readonly RetryPolicy Disabled = new(1, RetryBackoff.Fixed, 0, 0);

    public bool IsEnabled => MaxAttempts > 1;

    /// <summary>
    /// Delay before retry #<paramref name="attemptNumber"/>. Attempt 1 is the first call
    /// (no delay); attempt 2 is the first retry and pays <c>InitialDelayMs</c>; further
    /// retries follow <see cref="Backoff"/>. Returns 0 when retries are disabled.
    /// </summary>
    public TimeSpan DelayFor(int attemptNumber)
    {
        if (!IsEnabled || attemptNumber <= 1) return TimeSpan.Zero;
        int zeroBased = attemptNumber - 2;     // 0 for attempt 2, 1 for attempt 3, …
        long ms = Backoff switch
        {
            RetryBackoff.Fixed       => InitialDelayMs,
            RetryBackoff.Linear      => (long)InitialDelayMs * (zeroBased + 1),
            RetryBackoff.Exponential => (long)InitialDelayMs << Math.Min(zeroBased, 20), // cap shift at 20 to avoid overflow
            _                        => InitialDelayMs,
        };
        if (MaxDelayMs > 0 && ms > MaxDelayMs) ms = MaxDelayMs;
        return TimeSpan.FromMilliseconds(Math.Max(0, ms));
    }

    /// <summary>
    /// Extracts a <see cref="RetryPolicy"/> from a step's <c>config</c> object.
    /// Safe-by-default: any parse error, missing field, or out-of-range value yields
    /// <see cref="Disabled"/> so a typo in the JSON never breaks a workflow run.
    /// </summary>
    public static RetryPolicy Parse(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object) return Disabled;
        if (!config.TryGetProperty("retry", out var r) || r.ValueKind != JsonValueKind.Object) return Disabled;

        int maxAttempts = GetInt(r, "maxAttempts", 1);
        // Clamp aggressively so a fat-finger `"maxAttempts": 1000000` does not let a
        // misconfigured workflow monopolize an executor for hours.
        if (maxAttempts < 1) maxAttempts = 1;
        if (maxAttempts > 20) maxAttempts = 20;

        int initialDelayMs = GetInt(r, "initialDelayMs", 1000);
        if (initialDelayMs < 0) initialDelayMs = 0;

        int maxDelayMs = GetInt(r, "maxDelayMs", 30_000);
        if (maxDelayMs < 0) maxDelayMs = 0;

        var backoffStr = r.TryGetProperty("backoff", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString() : null;
        var backoff = backoffStr?.ToLowerInvariant() switch
        {
            "linear"      => RetryBackoff.Linear,
            "exponential" => RetryBackoff.Exponential,
            _             => RetryBackoff.Fixed,
        };

        return new RetryPolicy(maxAttempts, backoff, initialDelayMs, maxDelayMs);
    }

    private static int GetInt(JsonElement obj, string name, int fallback)
    {
        if (!obj.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed)) return parsed;
        return fallback;
    }
}

public enum RetryBackoff { Fixed, Linear, Exponential }
