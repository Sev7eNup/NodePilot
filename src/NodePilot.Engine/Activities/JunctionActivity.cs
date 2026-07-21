using System.Text.Json;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Junction / Merge activity — synchronizes multiple parallel branches.
///
/// Modes:
///   - waitAll (default): Wait for all incoming branches (same as implicit DAG behavior)
///   - waitAny: Fires as soon as one branch completes; remaining branch subtrees are skipped
///   - waitNofM: Fires after N succeeded branches
///
/// Engine handles the "when to fire" logic via config.mode. This activity itself is a
/// pass-through aggregator — it collects all completed upstream OutputParameters (already
/// injected via context.Variables by WorkflowEngine) and exposes them as structured
/// OutputParameters for downstream steps.
/// </summary>
public class JunctionActivity : IActivityExecutor
{
    public string ActivityType => "junction";

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        var mode = config.GetString("mode", "waitAll");

        // Whitelist-based aggregation: pull values exclusively from upstream OutputParameters
        // via context.PreviousResults. The old approach scanned context.Variables (the flat
        // resolver dict), which mixed in `globals.*` (incl. secret globals), every previous
        // step's `.output`/`.error` (large blobs), `manual.*` trigger inputs, and the denylisted
        // short-name param aliases (bare `{{paramKey}}` without a step prefix — blocked for
        // sensitive names like Authorization/Password/Token, see VariableResolver). Junction is
        // supposed to bubble up the converging branches' explicit outputs — nothing more.
        var aggregated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (context.PreviousResults is not null)
        {
            foreach (var (_, result) in context.PreviousResults)
            {
                foreach (var (paramKey, paramVal) in result.OutputParameters)
                {
                    // Skip reserved engine bookkeeping keys (e.g. __callDepth) and never
                    // overwrite a previously aggregated value — sibling branches that emit
                    // the same param-name keep the first-arrived deterministic-by-iteration
                    // value rather than racing on dict order.
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
