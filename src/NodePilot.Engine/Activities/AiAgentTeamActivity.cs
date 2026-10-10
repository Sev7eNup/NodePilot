using System.Text.Json;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Agents;

namespace NodePilot.Engine.Activities;

public sealed class AiAgentTeamActivity(AgentActivityRunner runner) : IActivityExecutor
{
    public string ActivityType => "aiAgentTeam";
    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
        => runner.ExecuteAsync(context, config, true, ct);
}
