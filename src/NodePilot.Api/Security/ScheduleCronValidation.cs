using System.Text.Json;
using NodePilot.Core.WorkflowDefinitions;
using Quartz;

namespace NodePilot.Api.Security;

/// <summary>
/// Publish/import validation for scheduleTrigger cron expressions.
///
/// <para>Three surfaces answered "is this cron usable?" and only the one the author never sees —
/// the scheduler source — used Quartz. The node executor accepted any non-blank string and the
/// designer preview parses Unix cron, which accepts expressions Quartz rejects (Quartz requires
/// exactly one of day-of-month / day-of-week to be "?"). The result was green everywhere, a
/// registration exception in the orchestrator's silent backoff, and a workflow that displayed
/// itself as active and never fired. This closes that gap with the same parser the scheduler
/// uses, at the point where the definition is stored.</para>
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

            if (!CronExpression.IsValidExpression(cron))
            {
                return $"scheduleTrigger '{trigger.Id}' has a cron expression Quartz cannot parse: '{cron}'. "
                    + "Quartz uses 6 or 7 fields and requires exactly one of day-of-month / day-of-week to be '?' "
                    + "(for example '0 0 2 * * ?' for daily at 02:00).";
            }
        }

        return null;
    }
}