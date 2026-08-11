using System.Diagnostics.CodeAnalysis;

namespace NodePilot.Ai;

/// <summary>
/// Configuration for the AI features. Holds a set of named connections
/// (<see cref="Profiles"/>, keyed by a stable id) plus the two knobs that are genuinely global:
/// the <see cref="Enabled"/> kill-switch and <see cref="ActiveProfileId"/>, which picks the one
/// profile every AI feature uses. Everything connection-shaped lives on
/// <see cref="LlmProfileOptions"/>.
///
/// <para>
/// The transport is OpenAI-compatible, so the same code works against OpenAI Cloud, Ollama, LM
/// Studio, vLLM, LocalAI, and llama.cpp servers. Which of the two wire dialects (chat completions
/// or OpenAI's Responses API) a profile speaks follows from its
/// <see cref="LlmProfileOptions.BaseUrl"/>. Local endpoints are the preferred use case — this
/// whole feature is opt-in (<c>Enabled=false</c> by default).
/// </para>
///
/// <para>
/// The dictionary key (the profile id) is what everything else references — the secret-preserving
/// settings save, the <c>Llm__Profiles__{id}__ApiKey</c> environment override, and
/// <see cref="ActiveProfileId"/>. It is immutable once created; <see cref="LlmProfileOptions.Name"/>
/// is the renameable label.
/// </para>
/// </summary>
public class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// Cap on the number of upstream variables included in the script-generation prompt. Both the
    /// frontend (BFS ordering) and the backend trim to this value. A const because it's a
    /// token-budget tuning value, not an operator knob.
    /// </summary>
    public const int MaxUpstreamVariables = 30;

    /// <summary>
    /// Number of retries for workflow generation when the LLM response isn't parsable JSON. 1 is
    /// enough — more retries cost tokens and don't help with models that already support JSON
    /// mode. For local models without JSON mode, one retry with a "reply with ONLY JSON"
    /// follow-up is the best trade-off.
    /// </summary>
    public const int MaxJsonRetries = 1;

    /// <summary>Master switch. Default <c>false</c> — operator opt-in. When off, every AI endpoint responds with 503.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Id of the profile every AI feature uses. Must name an entry in <see cref="Profiles"/>;
    /// there is deliberately no "fall back to the first one" rule, because silently talking to a
    /// different endpoint than the operator selected is worse than a clear 503.
    /// </summary>
    public string ActiveProfileId { get; set; } = "";

    /// <summary>
    /// The stored connections, keyed by immutable profile id. Empty by default — a fresh install
    /// ships no profile at all, so an operator's first profile is fully owned by the runtime
    /// overrides file and stays deletable through the Settings UI.
    /// </summary>
    public Dictionary<string, LlmProfileOptions> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How outbound LLM traffic reaches the network. Global rather than per profile — see
    /// <see cref="LlmProxyOptions"/>. Defaults to no proxy, which is the pre-existing behaviour.
    /// </summary>
    public LlmProxyOptions Proxy { get; set; } = new();

    /// <summary>
    /// Resolves <see cref="ActiveProfileId"/> against <see cref="Profiles"/>. Returns false when no
    /// profile is configured or the active id doesn't exist — callers turn that into a 503
    /// (<c>LLM_NO_ACTIVE_PROFILE</c>) rather than guessing a connection.
    /// </summary>
    public bool TryResolveActiveProfile([NotNullWhen(true)] out LlmProfileOptions? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(ActiveProfileId)) return false;
        return Profiles.TryGetValue(ActiveProfileId.Trim(), out profile) && profile is not null;
    }

    /// <summary>
    /// <see cref="Enabled"/> AND a resolvable active profile — the condition every AI feature gates
    /// on. Split from <see cref="TryResolveActiveProfile"/> so callers can tell the two failure
    /// modes apart (<c>LLM_DISABLED</c> vs. <c>LLM_NO_ACTIVE_PROFILE</c>).
    /// </summary>
    public bool IsUsable => Enabled && TryResolveActiveProfile(out _);
}
