using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Api.Security;

/// <summary>
/// The checks a workflow definition must pass before it goes live. Publish runs them on the
/// submitted body, enable on the stored definition; both share this list so they cannot drift.
/// </summary>
internal static class WorkflowActivationValidation
{
    internal sealed record Failure(string Code, string Message);

    internal static Failure? Validate(string definitionJson)
    {
        if (WorkflowResourceReferences.UnresolvedError(definitionJson) is { } dependencyError)
            return new Failure("unresolved_workflow_dependency", dependencyError);
        if (AgentPublishValidation.ValidateDefinition(definitionJson) is { } agentError)
            return new Failure("invalid_agent_configuration", agentError);
        if (WebhookHmacSecurity.ValidateDefinition(definitionJson) is { } hmacError)
            return new Failure("weak_webhook_hmac_secret", hmacError);
        // A cron Quartz rejects would otherwise fail at trigger registration and retry forever.
        if (ScheduleCronValidation.ValidateDefinition(definitionJson) is { } cronError)
            return new Failure("invalid_cron_expression", cronError);
        if (JunctionRequiredCountValidation.ValidateDefinition(definitionJson) is { } junctionError)
            return new Failure("invalid_junction_required_count", junctionError);
        return null;
    }
}
