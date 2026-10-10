using System.Text.Json;
using NodePilot.Ai.Agents;
using NodePilot.Core.Agents;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Api.Security;

internal static class AgentPublishValidation
{
    internal static string? ValidateDefinition(string definitionJson)
    {
        if (!WorkflowDefinitionDocument.TryParse(definitionJson, out var definition) || definition is null) return null;
        foreach (var node in definition.Nodes.Where(node => AgentConfiguration.IsAgent(node.Type)))
        {
            try
            {
                var config = AgentConfiguration.Parse(node.Data.Config, node.Type == "aiAgentTeam");
                if (config.ResultFormat == "json") AgentJsonSchema.Compile(config.ResultSchema!.Value);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
            { return $"Agent step '{node.Id}' has an invalid configuration: {ex.Message}"; }
        }
        return null;
    }
}
