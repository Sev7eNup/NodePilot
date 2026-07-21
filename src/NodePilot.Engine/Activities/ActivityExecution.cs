using System.Diagnostics;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Eliminates the repeated Stopwatch + try/catch boilerplate in engine-local activities.
/// Runs the body, stamps <see cref="ActivityResult.Duration"/> with the elapsed wall time
/// (overriding whatever the body returned so every early-return path gets a consistent
/// value), and converts any unhandled exception into a failed result via
/// <paramref name="formatError"/> (defaulting to <c>ex.Message</c>).
/// </summary>
internal static class ActivityExecution
{
    public static async Task<ActivityResult> RunAsync(
        Func<Task<ActivityResult>> body,
        Func<Exception, string>? formatError = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await body();
            sw.Stop();
            result.Duration = sw.Elapsed;
            return result;
        }
        catch (OperationCanceledException)
        {
            // Let cancellation propagate — the engine/scheduler turns OCE into a
            // Cancelled step status. Wrapping it as `Success=false, ErrorOutput=...`
            // would persist the step as Failed (wrong terminal status, breaks retry
            // semantics + lights up alerting that should stay quiet on user-initiated
            // cancels).
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = formatError?.Invoke(ex) ?? ex.Message,
                Duration = sw.Elapsed,
            };
        }
    }
}
