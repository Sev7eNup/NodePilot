using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NodePilot.Ai;

/// <summary>
/// Creates an <see cref="ILlmClient"/> bound to an effective connection: the global
/// <see cref="LlmOptions"/> with optional per-call <see cref="LlmConnection"/> overrides applied.
/// The single entry point for per-node endpoint/model/apiKey overrides (the <c>llmQuery</c>
/// activity). The global singleton <see cref="ILlmClient"/> is itself just <c>Create(null)</c>,
/// so every LLM call — assistant, script/workflow-gen, and the activity — resolves through here
/// and through the same guarded named HttpClient + <see cref="LlmEndpointGuard"/>.
/// </summary>
public interface ILlmClientFactory
{
    /// <summary>Builds a client for the effective connection. <paramref name="overrides"/> null ⇒ the global config.</summary>
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
        // Hot-reload: read the live LlmOptions per Create() so a config edit (Admin-Settings-UI
        // save or appsettings.runtime.json) takes effect without a restart. The factory is
        // singleton — IOptionsMonitor is the correct live source.
        var g = _options.CurrentValue;
        // Validate/normalize the effective BaseUrl HERE — the factory is the central override
        // entry point and must never trust callers to have pre-checked it.
        var baseUrl = LlmEndpointGuard.NormalizeAndValidateBaseUrl(overrides?.BaseUrl ?? g.BaseUrl);

        var config = new LlmClientConfig(
            BaseUrl: baseUrl,
            ApiKey: overrides?.ApiKey ?? g.ApiKey,
            Model: overrides?.Model ?? g.Model,
            MaxTokens: overrides?.MaxTokens ?? g.MaxTokens,
            Temperature: overrides?.Temperature, // per-call only; no global default
            TimeoutSeconds: overrides?.TimeoutSeconds ?? g.TimeoutSeconds);

        return new OpenAiCompatibleLlmClient(
            _httpClientFactory, config, _loggerFactory.CreateLogger<OpenAiCompatibleLlmClient>());
    }
}
