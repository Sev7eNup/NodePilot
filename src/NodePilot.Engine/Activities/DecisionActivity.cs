using System.Text.Json;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Conditions;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Multi-way branch / switch — evaluates a list of named cases against upstream step results
/// and exposes the first match as <c>OutputParameters["case"]</c>. Outgoing edges then route
/// using <c>step.param.case == "myName"</c>-style edge conditions, leveraging the existing
/// edge-condition machinery instead of introducing a new routing primitive in the engine.
///
/// Config:
///   cases             array, required — each entry: { name: string, condition: &lt;expression&gt; }
///                     where <c>condition</c> uses the same AST as edge <c>conditionExpression</c>
///                     (see <see cref="ConditionEvaluator"/>).
///   defaultCaseName   string, default "default" — value emitted when no case matches.
///
/// Outputs:
///   case      — name of the matched case, or <c>defaultCaseName</c> when nothing matched.
///   matched   — "true" / "false" — quick discriminator for the default branch.
///   reason    — name of the first case considered + which side fired (for logs / debugging).
///
/// Why not a routing primitive in the engine: edge conditions already handle the variable-
/// resolution semantics (incl. legacy "stepId.success", template-resolution in literals, the
/// outputVariable-alias map). Reusing them keeps "decision" and "edge condition" semantically
/// identical — one expression library, one set of operators, one set of corner cases.
/// </summary>
public class DecisionActivity : IActivityExecutor
{
    public string ActivityType => "decision";

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        var defaultCaseName = config.GetString("defaultCaseName", "default");

        if (!config.TryGetProperty("cases", out var casesEl) || casesEl.ValueKind != JsonValueKind.Array)
        {
            return Task.FromResult(NoMatch(defaultCaseName, "no cases array"));
        }

        // Engine populates these for the production path; tests invoking this activity directly
        // can pass null. With null PreviousResults the evaluator returns "false-by-default" for
        // any reference to upstream output, so the default-case path stays correct.
        var results = context.PreviousResults
                      ?? (IReadOnlyDictionary<string, ActivityResult>)new Dictionary<string, ActivityResult>();
        // ConditionEvaluator's public Evaluate signature takes IReadOnlyDictionary; nothing to
        // adapt. The aliasMap is optional.
        var aliasMap = context.OutputVariableToStepId;
        // Global/manual context must be threaded through so `source:"global"`/`"manual"` operands
        // and {{globals.X}}/{{manual.X}} literals resolve identically to the edge-condition path.
        // Without these the evaluator returns "" for such operands → silent wrong branch.
        var conditionContext = new ConditionContext(results, aliasMap, context.GlobalVariables, context.InputParameters);

        var index = -1;
        foreach (var caseEl in casesEl.EnumerateArray())
        {
            index++;
            if (caseEl.ValueKind != JsonValueKind.Object) continue;

            var caseName = caseEl.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(caseName))
                continue; // unnamed cases are ignored — they couldn't be referenced from edges anyway

            if (!caseEl.TryGetProperty("condition", out var condEl) || condEl.ValueKind != JsonValueKind.Object)
            {
                // Missing/malformed condition → treat as never-matching, but keep iterating —
                // a sibling case can still match. The UI surfaces malformed cases via lint.
                continue;
            }

            // Stricter than edge conditions: there, a condition object without `type` means
            // "always pass" (the default for unconditional edges). Here that would be a
            // foot-gun — a case mangled by an editor bug would unintentionally override every
            // other case. So a `type` is mandatory on every decision case.
            if (!condEl.TryGetProperty("type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(typeEl.GetString()))
            {
                continue;
            }

            bool matched;
            try
            {
                matched = ConditionEvaluator.Evaluate(condEl, conditionContext);
            }
            catch (Exception ex)
            {
                // Defensive: ConditionEvaluator is expected to be exception-free, but if a
                // future operator throws (regex compile, etc.), we want the workflow author to
                // see the failure, not a silent skip.
                return Task.FromResult(new ActivityResult
                {
                    Success = false,
                    ErrorOutput = $"Decision: case '{caseName}' threw during evaluation: {ex.Message}",
                    OutputParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["case"] = string.Empty,
                        ["matched"] = "false",
                        ["reason"] = $"error in case '{caseName}'",
                    },
                });
            }

            if (matched)
            {
                return Task.FromResult(new ActivityResult
                {
                    Success = true,
                    Output = $"matched case '{caseName}' (#{index})",
                    OutputParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["case"] = caseName!,
                        ["matched"] = "true",
                        ["reason"] = $"case '{caseName}' matched at index {index}",
                    },
                });
            }
        }

        return Task.FromResult(NoMatch(defaultCaseName, "no case matched"));
    }

    private static ActivityResult NoMatch(string defaultCaseName, string reason)
    {
        return new ActivityResult
        {
            Success = true,
            Output = $"no case matched, using '{defaultCaseName}'",
            OutputParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["case"] = defaultCaseName,
                ["matched"] = "false",
                ["reason"] = reason,
            },
        };
    }
}
