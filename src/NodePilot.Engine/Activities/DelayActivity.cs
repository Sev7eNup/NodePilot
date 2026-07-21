using System.Text.Json;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Activities;

public class DelayActivity : IActivityExecutor
{
    public string ActivityType => "delay";

    // Workflow-time delay is bounded to 24 hours. Anything longer should be a schedule
    // trigger, not a `delay` step pinning a step-runner slot. Bare `GetInt32()` previously
    // threw on a JSON string ("5"), a null, or a non-int — now we accept those gracefully
    // and clamp before passing to Task.Delay (which would throw on negatives).
    internal const int MinDelaySeconds = 0;
    internal const int MaxDelaySeconds = 24 * 60 * 60;
    internal const int DefaultDelaySeconds = 5;

    public async Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        var seconds = ReadSeconds(config);
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);

        return new ActivityResult
        {
            Success = true,
            Output = $"Delayed for {seconds} seconds",
            Duration = TimeSpan.FromSeconds(seconds)
        };
    }

    internal static int ReadSeconds(JsonElement config)
    {
        var raw = DefaultDelaySeconds;
        // TryGetInt32 throws InvalidOperationException on non-Number JsonElements
        // (string, null, bool…), so gate on ValueKind first. The point of the clamp
        // is to be forgiving with whatever shape the workflow JSON carries.
        if (config.TryGetProperty("seconds", out var s)
            && s.ValueKind == JsonValueKind.Number
            && s.TryGetInt32(out var parsed))
            raw = parsed;
        return Math.Clamp(raw, MinDelaySeconds, MaxDelaySeconds);
    }
}
