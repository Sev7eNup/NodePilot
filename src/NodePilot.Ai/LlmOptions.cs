using System.Diagnostics.CodeAnalysis;

namespace NodePilot.Ai;

/// <summary>
/// Configuration for the AI features: named connections (<see cref="Profiles"/>, keyed by an
/// immutable profile id) plus the two global knobs, the <see cref="Enabled"/> kill-switch and
/// <see cref="ActiveProfileId"/>. Everything connection-shaped lives on
/// <see cref="LlmProfileOptions"/>, including which wire dialect the endpoint speaks. The profile
/// id is the stable reference used by settings saves, the <c>Llm__Profiles__{id}__ApiKey</c>
/// environment override and <see cref="ActiveProfileId"/>.
/// </summary>
public class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// Cap on the number of upstream variables included in the script-generation prompt. Both the
    /// frontend (BFS ordering) and the backend trim to this value. A const because it is a
    /// token-budget value, not an operator knob.
    /// </summary>
    public const int MaxUpstreamVariables = 30;

    /// <summary>
    /// Number of retries for workflow generation when the LLM response is not parsable JSON. One
    /// retry with a json-only follow-up covers models that lack JSON mode; further retries only
    /// cost tokens.
    /// </summary>
    public const int MaxJsonRetries = 1;

    /// <summary>
    /// Master switch, default <c>false</c>. When off, every AI endpoint answers 503.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Id of the profile every AI feature uses. Must name an entry in <see cref="Profiles"/>. There
    /// is no fallback to the first profile: an unresolvable id yields a 503 instead of a call to an
    /// endpoint the operator did not select.
    /// </summary>
    public string ActiveProfileId { get; set; } = "";

    /// <summary>
    /// The stored connections, keyed by immutable profile id. Empty by default, so the first
    /// profile an operator creates lives entirely in the runtime overrides file and stays
    /// deletable through the Settings UI.
    /// </summary>
    public Dictionary<string, LlmProfileOptions> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How outbound LLM traffic reaches the network. Global rather than per profile; see
    /// <see cref="LlmProxyOptions"/>. Defaults to no proxy.
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
    /// <see cref="Enabled"/> plus a resolvable active profile, the condition every AI feature gates
    /// on. Kept separate from <see cref="TryResolveActiveProfile"/> so callers can tell the two
    /// failure modes apart (<c>LLM_DISABLED</c> vs. <c>LLM_NO_ACTIVE_PROFILE</c>).
    /// </summary>
    public bool IsUsable => Enabled && TryResolveActiveProfile(out _);
}
