using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NodePilot.Ai;

/// <summary>
/// Creates an <see cref="ILlmClient"/> bound to an effective connection: the active
/// <see cref="LlmProfileOptions"/> with optional per-call <see cref="LlmConnection"/> overrides
/// applied. The single entry point for per-node endpoint/model/apiKey overrides (the
/// <c>llmQuery</c> activity). Every LLM call resolves through here and through the same guarded
/// named HttpClient plus <see cref="LlmEndpointGuard"/>.
///
/// <para>Callers resolve lazily. <see cref="Create"/> throws when no active profile is configured,
/// so it belongs inside the call, not in a constructor: services take this factory rather than a
/// pre-built <see cref="ILlmClient"/>, so a half-configured instance surfaces as a 503 from the
/// endpoint gate instead of failing during DI construction.</para>
/// </summary>
public interface ILlmClientFactory
{
    /// <summary>
    /// Builds a client for the effective connection: the active profile, with any non-null
    /// <paramref name="overrides"/> field taking precedence. The implementation follows the wire
    /// dialect <see cref="LlmEndpointGuard.ResolveEndpoint"/> derives from the effective BaseUrl:
    /// chat completions by default, the OpenAI Responses API for a <c>/responses</c> endpoint.
    /// </summary>
    /// <exception cref="LlmException">
    /// No profile is configured, <c>Llm:ActiveProfileId</c> names none, or the effective BaseUrl
    /// fails <see cref="LlmEndpointGuard.ResolveEndpoint"/>.
    /// </exception>
    ILlmClient Create(LlmConnection? overrides = null);
}

/// <inheritdoc cref="ILlmClientFactory"/>
public sealed class LlmClientFactory : ILlmClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<LlmOptions> _options;
    private readonly ILoggerFactory _loggerFactory;

    public LlmClientFactory(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<LlmOptions> options,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public ILlmClient Create(LlmConnection? overrides = null)
    {
        // Read the live LlmOptions per Create() so a config edit takes effect without a restart.
        // The factory is a singleton, so IOptionsMonitor is the correct live source.
        if (!_options.CurrentValue.TryResolveActiveProfile(out var profile))
        {
            throw new LlmException(LlmErrorKind.Unreachable,
                "No active LLM profile is configured. Add a profile under Settings → System → "
                + "Integrations → LLM and select it as the active profile.");
        }

        // Validate the effective BaseUrl and resolve its wire dialect here: the factory is the
        // central override entry point and cannot assume callers have pre-checked it.
        var endpoint = LlmEndpointGuard.ResolveEndpoint(overrides?.BaseUrl ?? profile.BaseUrl);

        var config = new LlmClientConfig(
            Endpoint: endpoint,
            ApiKey: overrides?.ApiKey ?? profile.ApiKey,
            Model: overrides?.Model ?? profile.Model,
            MaxTokens: overrides?.MaxTokens ?? profile.MaxTokens,
            Temperature: overrides?.Temperature, // per-call only; no profile default
            TimeoutSeconds: overrides?.TimeoutSeconds ?? profile.TimeoutSeconds);

        return endpoint.Flavor switch
        {
            LlmApiFlavor.Responses => new OpenAiResponsesLlmClient(
                _httpClientFactory, config, _loggerFactory.CreateLogger<OpenAiResponsesLlmClient>()),
            _ => new OpenAiCompatibleLlmClient(
                _httpClientFactory, config, _loggerFactory.CreateLogger<OpenAiCompatibleLlmClient>()),
        };
    }
}
