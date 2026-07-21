using System.Text.Json;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Triggers;

/// <summary>
/// Schedule trigger — validates cron expression and passes schedule metadata.
/// Actual scheduling is handled by Quartz.NET in NodePilot.Scheduler.
/// When executed as a node, it outputs the current trigger time.
/// </summary>
public class ScheduleTrigger : IActivityExecutor
{
    public string ActivityType => "scheduleTrigger";

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        var cron = config.TryGetProperty("cronExpression", out var c) ? c.GetString() : null;
        var description = config.TryGetProperty("description", out var d) ? d.GetString() : null;

        if (string.IsNullOrWhiteSpace(cron))
        {
            return Task.FromResult(new ActivityResult
            {
                Success = false,
                ErrorOutput = "No cron expression configured"
            });
        }

        return Task.FromResult(new ActivityResult
        {
            Success = true,
            Output = $"Schedule trigger fired at {DateTime.UtcNow:O}\nCron: {cron}\nDescription: {description ?? "N/A"}",
            OutputParameters = ExtractManualParams(context.Variables),
        });
    }

    private static Dictionary<string, string> ExtractManualParams(Dictionary<string, string> vars)
    {
        var result = new Dictionary<string, string>();
        foreach (var (k, v) in vars)
            if (k.StartsWith("manual.", StringComparison.OrdinalIgnoreCase))
                result[k["manual.".Length..]] = v;
        return result;
    }
}
