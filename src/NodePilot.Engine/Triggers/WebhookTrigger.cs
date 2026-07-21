using System.Text.Json;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Triggers;

/// <summary>
/// Webhook trigger — exposes an HTTP endpoint that starts the workflow. The actual
/// HTTP listener is <c>WebhooksController</c>; when the workflow runs this node
/// surfaces the request payload as OutputParameters for downstream use.
/// </summary>
public class WebhookTrigger : IActivityExecutor
{
    public string ActivityType => "webhookTrigger";

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        var path = config.TryGetProperty("path", out var p) ? p.GetString() : null;
        var method = config.TryGetProperty("method", out var m) ? m.GetString() : "POST";

        var outputParams = new Dictionary<string, string>();
        foreach (var (k, v) in context.Variables)
            if (k.StartsWith("manual.", StringComparison.OrdinalIgnoreCase))
                outputParams[k["manual.".Length..]] = v;

        var body = outputParams.GetValueOrDefault("webhookBody", "(no body)");
        return Task.FromResult(new ActivityResult
        {
            Success = true,
            Output = $"Webhook received on {method} {path ?? "/api/webhooks/" + context.WorkflowExecutionId}\nBody: {body}",
            OutputParameters = outputParams,
        });
    }
}
