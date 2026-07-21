using System.Text.Json;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Triggers;

/// <summary>
/// Manual trigger — allows a user to start a workflow with custom input parameters.
/// Config defines the parameter schema (name, type, required, default).
/// Output is a JSON object of all parameter values, making them accessible
/// to downstream steps via {{varName.output}} (full JSON) or {{varName.param.paramName}} (individual).
/// </summary>
public class ManualTrigger : IActivityExecutor
{
    public string ActivityType => "manualTrigger";

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        var paramValues = new Dictionary<string, string>();

        if (config.TryGetProperty("parameters", out var paramsArray) && paramsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var param in paramsArray.EnumerateArray())
            {
                var paramName = param.TryGetProperty("name", out var n) ? n.GetString() ?? "unknown" : "unknown";
                var defaultValue = param.TryGetProperty("default", out var dv) ? dv.ToString() : "";
                var required = param.TryGetProperty("required", out var r) && r.GetBoolean();

                var value = context.Variables.GetValueOrDefault($"manual.{paramName}", defaultValue ?? "");

                if (required && string.IsNullOrWhiteSpace(value))
                {
                    return Task.FromResult(new ActivityResult
                    {
                        Success = false,
                        ErrorOutput = $"Required parameter '{paramName}' is missing or empty"
                    });
                }

                paramValues[paramName] = value;
            }
        }

        // Output as JSON so downstream steps can parse individual values
        var jsonOutput = JsonSerializer.Serialize(paramValues, JsonSerializerDefaults.Indented);

        return Task.FromResult(new ActivityResult
        {
            Success = true,
            Output = jsonOutput,
            // Store individual params for {{varName.param.xxx}} access
            OutputParameters = paramValues
        });
    }
}
