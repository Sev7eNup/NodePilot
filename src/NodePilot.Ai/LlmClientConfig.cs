namespace NodePilot.Ai;

/// <summary>
/// Per-call connection overrides for a single LLM request (the <c>llmQuery</c> activity's
/// per-node config). Every field is optional; a null field falls back to the global
/// <see cref="LlmOptions"/> default. Resolved into an effective <see cref="LlmClientConfig"/>
/// by <see cref="ILlmClientFactory.Create"/>.
/// </summary>
public sealed record LlmConnection(
    string? BaseUrl = null,
    string? ApiKey = null,
    string? Model = null,
    int? MaxTokens = null,
    double? Temperature = null,
    int? TimeoutSeconds = null);

/// <summary>
/// The fully-resolved connection an <see cref="ILlmClient"/> instance is bound to: the global
/// <see cref="LlmOptions"/> with any per-call <see cref="LlmConnection"/> overrides applied.
/// <see cref="Endpoint"/> is already validated and dialect-resolved via
/// <see cref="LlmEndpointGuard.ResolveEndpoint"/>. <see cref="Temperature"/> is per-call only and
/// has no global default; when it is null, <c>temperature</c> is omitted from the request body.
/// </summary>
public sealed record LlmClientConfig(
    LlmEndpointTarget Endpoint,
    string? ApiKey,
    string Model,
    int MaxTokens,
    double? Temperature,
    int TimeoutSeconds);
