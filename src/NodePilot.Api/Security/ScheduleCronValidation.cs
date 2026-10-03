using System.Text.Json;
using NodePilot.Core.WorkflowDefinitions;
using Quartz;

namespace NodePilot.Api.Security;

/// <summary>
/// Publish/import validation for scheduleTrigger cron expressions.
///
/// <para>Uses the same Quartz parser as the scheduler source, at the point where the definition is
/// stored. The designer preview parses Unix cron, so without this an expression Quartz rejects
/// would look valid, fail registration in the orchestrator's silent backoff, and leave a workflow
/// that shows itself as active and never fires.</para>
/// </summary>
internal static class ScheduleCronValidation
{
    internal static string? ValidateDefinition(string definitionJson)
    {
        if (!WorkflowDefinitionDocument.TryParse(definitionJson, out var definition)
            || definition is null)
        {
            return null; // Structural validation owns malformed definitions.
        }

        // Disabled nodes are validated too: publish is where the definition is stored, and a
        // disabled trigger can be enabled later without touching its config.
        foreach (var trigger in definition.Nodes.Where(x =>
                     string.Equals(x.Type, "scheduleTrigger", StringComparison.Ordinal)))
        {
            var config = trigger.Data.Config;
            if (config.ValueKind != JsonValueKind.Object
                || !config.TryGetProperty("cronExpression", out var cronElement)
                || cronElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var cron = cronElement.GetString();
            if (string.IsNullOrWhiteSpace(cron)) continue;

            if (!CronExpression.TryParse(cron, out _))
            {
                return $"scheduleTrigger '{trigger.Id}' has a cron expression Quartz cannot parse: '{cron}'. "
                    + "Quartz uses 6 or 7 fields, starting with seconds "
                    + "(for example '0 0 2 * * ?' for daily at 02:00).";
            }
        }

        return null;
    }
}