using System.ComponentModel.DataAnnotations;
using NodePilot.Ai;

namespace NodePilot.Api.Dtos.Settings;

/// <summary>
/// One stored LLM connection in the Admin Settings API. Mirrors
/// <see cref="NodePilot.Ai.LlmProfileOptions"/> plus the <see cref="Id"/> that the persisted
/// dictionary is keyed by.
///
/// <para><c>ApiKey</c> follows the standard Secret-handling rules: <c>"********"</c> on read when
/// set, <c>"__unchanged__"</c> sentinel on write to keep the persisted value, new plaintext to
/// rotate, or <c>null</c>/empty to clear.</para>
/// </summary>
public sealed class LlmProfileSettingsDto
{
    /// <summary>
    /// Immutable id, assigned once at creation. Everything references profiles by this — the
    /// secret-preserving save, <c>Llm:ActiveProfileId</c>, and the
    /// <c>Llm__Profiles__{id}__ApiKey</c> environment override — so it stays stable across
    /// renames. Restricted to a slug shape because it becomes a configuration key segment.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[a-z0-9][a-z0-9-]{0,31}$",
        ErrorMessage = "Profile id must be 1-32 chars of lowercase letters, digits or '-', starting with a letter or digit.")]
    public string Id { get; set; } = "";

    /// <summary>Operator-facing label. Free text and renameable — the id is what persists.</summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(64)]
    public string Name { get; set; } = "";

    [Required(AllowEmptyStrings = false)]
    [Url]
    [StringLength(2048)]
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// Read response: <c>"********"</c> when a value is configured, <c>null</c> otherwise.
    /// Write request: <c>"__unchanged__"</c> sentinel keeps it, new plaintext rotates,
    /// null/empty clears.
    /// </summary>
    public string? ApiKey { get; set; }

    [Required(AllowEmptyStrings = false)]
    [StringLength(255)]
    public string Model { get; set; } = "";

    /// <summary>
    /// Output-token cap per LLM call. 256–128k matches what real-world OpenAI-compatible
    /// endpoints accept; values outside this range are almost always operator typos
    /// (e.g. 40 instead of 4000) that would cause every LLM call to truncate or 400.
    /// </summary>
    [Range(256, 128_000)]
    public int MaxTokens { get; set; } = 4096;

    [Range(5, 3600)]
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Opt-in: lets the chat assistants call read-only analysis tools (function-calling).
    /// Per profile because reliable tool-calling is a property of the model, not the installation.
    /// </summary>
    public bool EnableToolCalling { get; set; }

    /// <summary>Max LLM rounds with tool calls per chat turn (loop guard). 1–10.</summary>
    [Range(1, 10)]
    public int ToolCallMaxDepth { get; set; } = 6;

    /// <summary>
    /// Response-only. The configuration source that owns this profile besides the runtime
    /// overrides file (<c>appsettings</c>, <c>production</c>, <c>env</c>, <c>cli</c>), or
    /// <c>null</c> when the Settings UI fully owns it.
    ///
    /// <para>A non-null value means the profile <b>cannot be deleted</b> through the API: the
    /// runtime file is just another configuration provider layered on top, so dropping the entry
    /// there would let the underlying definition resurface on the next reload. Edits still work —
    /// they win as overrides. Ignored on write.</para>
    /// </summary>
    public string? ManagedBy { get; set; }
}

/// <summary>
/// The connection fields of a single profile as sent to the LLM test probe — the draft the
/// operator currently has open, which may not be saved yet.
///
/// <para>Deliberately carries <b>no</b> <c>Id</c>: the request's <c>ProfileId</c> is the one
/// authoritative id (used solely to look up a stored <c>ApiKey</c> for the <c>__unchanged__</c>
/// sentinel). With an id on both sides they could disagree, and the probe would happily test
/// profile B's endpoint using profile A's key.</para>
/// </summary>
public sealed class LlmProfileProbeDto
{
    [Required(AllowEmptyStrings = false)]
    [Url]
    [StringLength(2048)]
    public string BaseUrl { get; set; } = "";

    /// <summary>Plaintext key, or the <c>__unchanged__</c>/<c>"********"</c> marker to use the stored one.</summary>
    public string? ApiKey { get; set; }

    [Range(5, 3600)]
    public int TimeoutSeconds { get; set; } = 90;
}

/// <summary>
/// Outbound-proxy settings for every LLM call. Mirrors <see cref="NodePilot.Ai.LlmProxyOptions"/>.
/// One block per installation, not per profile — the "cloud through the proxy, local Ollama
/// direct" case is what <see cref="BypassList"/> is for.
/// </summary>
public sealed class LlmProxyDto : IValidatableObject
{
    /// <summary>Upper bound on bypass entries. Generous — the point is to stop runaway payloads.</summary>
    public const int MaxBypassEntries = 128;

    /// <summary>
    /// <c>off</c> (default, direct connection) / <c>system</c> (the OS proxy of the service
    /// account) / <c>custom</c> (<see cref="Address"/>). Compared case-insensitively against
    /// <see cref="NodePilot.Ai.LlmProxyMode"/>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(16)]
    public string Mode { get; set; } = nameof(LlmProxyMode.Off).ToLowerInvariant();

    /// <summary>Proxy URL. Required when <see cref="Mode"/> is <c>custom</c>, ignored otherwise.</summary>
    [StringLength(2048)]
    public string Address { get; set; } = "";

    /// <summary>Host patterns that skip the proxy (shell globs). Only used in <c>custom</c> mode.</summary>
    public List<string> BypassList { get; set; } = new();

    [StringLength(255)]
    public string? Username { get; set; }

    /// <summary>SecretField semantics — <c>"__unchanged__"</c> keeps, plaintext rotates, null/empty clears.</summary>
    public string? Password { get; set; }

    /// <summary>Authenticate against the proxy with the service account's Windows credentials.</summary>
    public bool UseDefaultCredentials { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.TryParse<LlmProxyMode>(Mode?.Trim() ?? "", ignoreCase: true, out var mode))
        {
            yield return new ValidationResult(
                "Proxy mode must be one of 'off', 'system', or 'custom'.", new[] { nameof(Mode) });
            yield break;
        }

        // Null-guarded: a literal "BypassList": null in the body nulls the property, and an NRE
        // here would turn an operator typo into a 500 instead of a field-level 400.
        if ((BypassList?.Count ?? 0) > MaxBypassEntries)
        {
            yield return new ValidationResult(
                $"At most {MaxBypassEntries} proxy bypass entries are supported.", new[] { nameof(BypassList) });
        }

        // Only meaningful for 'custom'; validating it in the other modes would reject a parked
        // address the operator kept around while temporarily switching to 'system'.
        if (mode != LlmProxyMode.Custom) yield break;

        var address = Address?.Trim() ?? "";
        if (address.Length == 0)
        {
            yield return new ValidationResult(
                "Proxy mode 'custom' requires a proxy address (e.g. http://proxy.corp.local:8080).",
                new[] { nameof(Address) });
            yield break;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult(
                $"'{address}' is not a valid http(s) proxy URL.", new[] { nameof(Address) });
        }
    }
}

/// <summary>
/// LLM section DTO for the Admin Settings API. Mirrors <see cref="NodePilot.Ai.LlmOptions"/> —
/// the operator-tunable knobs only, the per-feature constants (<c>MaxUpstreamVariables</c>,
/// <c>MaxJsonRetries</c>) stay code-side and aren't exposed through the UI.
///
/// <para><see cref="Profiles"/> is a list rather than a dictionary purely so the UI order is
/// stable; persistence keys each entry by its <see cref="LlmProfileSettingsDto.Id"/>.</para>
/// </summary>
public sealed class LlmSettingsDto : IValidatableObject
{
    /// <summary>Upper bound on stored profiles. Generous — the point is to stop runaway payloads.</summary>
    public const int MaxProfiles = 20;

    public bool Enabled { get; set; }

    /// <summary>Id of the profile every AI feature uses. Must name an entry in <see cref="Profiles"/>.</summary>
    public string ActiveProfileId { get; set; } = "";

    public List<LlmProfileSettingsDto> Profiles { get; set; } = new();

    /// <summary>How outbound LLM traffic reaches the network. Defaults to no proxy.</summary>
    [Required] public LlmProxyDto Proxy { get; set; } = new();

    /// <summary>
    /// <para><b>Why this exists:</b> <c>Validator.TryValidateObject</c> — which the generic settings
    /// adapter calls — does <i>not</i> recurse into collection elements. Without validating each
    /// profile explicitly here, every <c>[Url]</c>/<c>[Range]</c>/<c>[Required]</c> on
    /// <see cref="LlmProfileSettingsDto"/> would be dead metadata.</para>
    ///
    /// <para>Member names are reported as <c>Profiles[i].Field</c> so the UI can point at the
    /// offending row. The same applies to the nested <see cref="Proxy"/> object, whose members are
    /// reported as <c>Proxy.Field</c>.</para>
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Proxy is null)
        {
            yield return new ValidationResult("Proxy is required.", new[] { nameof(Proxy) });
        }
        else
        {
            var proxyResults = new List<ValidationResult>();
            Validator.TryValidateObject(Proxy, new ValidationContext(Proxy), proxyResults, validateAllProperties: true);
            foreach (var r in proxyResults)
            {
                var members = r.MemberNames.Any()
                    ? r.MemberNames.Select(m => $"{nameof(Proxy)}.{m}").ToArray()
                    : new[] { nameof(Proxy) };
                yield return new ValidationResult(r.ErrorMessage, members);
            }
        }

        if (Profiles.Count > MaxProfiles)
        {
            yield return new ValidationResult(
                $"At most {MaxProfiles} LLM profiles are supported.", new[] { nameof(Profiles) });
            yield break;
        }

        for (var i = 0; i < Profiles.Count; i++)
        {
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(Profiles[i], new ValidationContext(Profiles[i]), results, validateAllProperties: true);
            foreach (var r in results)
            {
                var members = r.MemberNames.Any()
                    ? r.MemberNames.Select(m => $"{nameof(Profiles)}[{i}].{m}").ToArray()
                    : new[] { $"{nameof(Profiles)}[{i}]" };
                yield return new ValidationResult(r.ErrorMessage, members);
            }
        }

        var duplicateIds = Profiles
            .GroupBy(p => p.Id?.Trim() ?? "", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        foreach (var id in duplicateIds)
        {
            yield return new ValidationResult(
                $"Duplicate LLM profile id '{id}'.", new[] { nameof(Profiles) });
        }

        var duplicateNames = Profiles
            .GroupBy(p => p.Name?.Trim() ?? "", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        foreach (var name in duplicateNames)
        {
            yield return new ValidationResult(
                $"Duplicate LLM profile name '{name}'.", new[] { nameof(Profiles) });
        }

        var hasActive = !string.IsNullOrWhiteSpace(ActiveProfileId)
                        && Profiles.Any(p => string.Equals(p.Id?.Trim(), ActiveProfileId.Trim(), StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(ActiveProfileId) && !hasActive)
        {
            yield return new ValidationResult(
                $"ActiveProfileId '{ActiveProfileId}' does not match any configured profile.",
                new[] { nameof(ActiveProfileId) });
        }

        // Enabled without a usable profile would boot fine but answer 503 on every AI endpoint —
        // reject it at the point where the operator can still see why.
        if (Enabled && !hasActive)
        {
            yield return new ValidationResult(
                "Enabling the LLM integration requires at least one profile and an active profile selection.",
                new[] { nameof(ActiveProfileId) });
        }
    }
}
