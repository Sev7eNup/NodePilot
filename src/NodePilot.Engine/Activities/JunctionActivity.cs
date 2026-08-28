using System.Text.Json;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Junction / Merge activity — synchronizes multiple parallel branches.
///
/// Modes: waitAll (default, wait for all branches), waitAny (fires on the first branch,
/// skipping the rest), waitNofM (fires after N branches succeed). The engine decides when to
/// fire based on config.mode; this activity aggregates the completed upstream OutputParameters
/// into its own OutputParameters for downstream steps.
/// </summary>
public class JunctionActivity : IActivityExecutor
{
    public string ActivityType => "junction";

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        var mode = config.GetString("mode", "waitAll");

        // Pulls values only from upstream OutputParameters via context.PreviousResults, not from
        // context.Variables (the flat resolver dict), which also holds `globals.*` (including
        // secret globals), full step `.output`/`.error` blobs, `manual.*` trigger inputs, and
        // denylisted short-name param aliases (see VariableResolver). Junction should bubble up
        // only the converging branches' explicit outputs.
        var aggregated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (context.PreviousResults is not null)
        {
            foreach (var (_, result) in context.PreviousResults)
            {
                foreach (var (paramKey, paramVal) in result.OutputParameters)
                {
                    // Skip reserved engine bookkeeping keys (e.g. __callDepth). Never overwrite an
                    // already aggregated value, so sibling branches with the same param name keep
                    // the first one encountered instead of racing on dictionary order.
                    if (WorkflowRecursion.IsReservedParameterName(paramKey)) continue;
                    if (!aggregated.ContainsKey(paramKey))
                        aggregated[paramKey] = paramVal;
                }
            }
        }

        var outputJson = JsonSerializer.Serialize(new
        {
            mode,
            branchCount = aggregated.Count,
            values = aggregated,
        }, JsonSerializerDefaults.Indented);

        return Task.FromResult(new ActivityResult
        {
            Success = true,
            Output = outputJson,
            OutputParameters = aggregated,
        });
    }
}
