using System.Text.Json;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Agents;

namespace NodePilot.Engine.Activities;

public sealed class AiAgentActivity(AgentActivityRunner runner) : IActivityExecutor
{
    public string ActivityType => "aiAgent";
    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
        => runner.ExecuteAsync(context, config, false, ct);
}
