using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;

namespace NodePilot.Ai;

/// <summary>
/// The single place that decides whether a <c>Llm:*</c> configuration block is acceptable. Both
/// entry points share it so a save can never be accepted that would block the next boot:
/// <see cref="LlmServiceCollectionExtensions.AddNodePilotAi"/> runs it at startup, and the API's
/// <c>LlmConfigBootValidator</c> runs it against the simulated merged config on every settings PUT.
///
/// <para><b>Scope rule for the metadata check:</b> when <c>Llm:Enabled=true</c>, <i>every</i>
/// profile's BaseUrl must pass — not just the active one. Switching the active profile is a plain
/// settings save with no restart, so a parked profile pointing at a metadata endpoint would be a
/// loaded gun that only fires on the switch. With <c>Enabled=false</c> nothing is checked, so an
/// untouched default block can never keep an instance from booting.</para>
/// </summary>
public static class LlmProfileValidation
{
    /// <summary>A rejected profile endpoint: the offending configuration key plus a ready-to-show message.</summary>
    public sealed record ProfileIssue(string ConfigKey, string Message);

    /// <summary>Configuration path of the profile dictionary (<c>Llm:Profiles</c>).</summary>
    public const string ProfilesKey = $"{LlmOptions.SectionName}:Profiles";

    /// <summary>
    /// Cloud-metadata check across all configured profiles. Returns an empty list when
    /// <c>Llm:Enabled=false</c>.
    /// </summary>
    public static IReadOnlyList<ProfileIssue> ValidateProfileEndpoints(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var issues = new List<ProfileIssue>();
        if (!configuration.GetValue<bool>($"{LlmOptions.SectionName}:Enabled"))
            return issues;

        foreach (var profile in configuration.GetSection(ProfilesKey).GetChildren())
        {
            var baseUrl = profile["BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl)) continue;
            if (!LlmEndpointGuard.IsCloudMetadataEndpoint(baseUrl)) continue;

            var name = string.IsNullOrWhiteSpace(profile["Name"]) ? profile.Key : profile["Name"];
            issues.Add(new ProfileIssue(
                $"{ProfilesKey}:{profile.Key}:BaseUrl",
                $"SECURITY: LLM profile '{name}' has a BaseUrl ('{baseUrl}') that points at a cloud-metadata "
                + "endpoint. This range (169.254.0.0/16, metadata.google.internal, metadata.azure.com) is "
                + "always blocked. Choose a real LLM endpoint, delete the profile, or disable Llm:Enabled."));
        }

        return issues;
    }

    /// <summary>
    /// Rules for the <c>Llm:Proxy:*</c> block. Same <c>Llm:Enabled</c> gate as
    /// <see cref="ValidateProfileEndpoints"/>: an untouched default block must never keep an
    /// instance from booting.
    ///
    /// <para>Checked here rather than only where the proxy is built, so a bad value is rejected by
    /// the settings PUT instead of detonating on the next restart — the failure mode
    /// <c>RestApi:Proxy</c> still has.</para>
    /// </summary>
    public static IReadOnlyList<ProfileIssue> ValidateProxy(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var issues = new List<ProfileIssue>();
        if (!configuration.GetValue<bool>($"{LlmOptions.SectionName}:Enabled"))
            return issues;

        var modeKey = $"{LlmProxyOptions.SectionName}:Mode";
        var addressKey = $"{LlmProxyOptions.SectionName}:Address";

        var rawMode = configuration[modeKey];
        if (string.IsNullOrWhiteSpace(rawMode)) return issues;

        if (!Enum.TryParse<LlmProxyMode>(rawMode.Trim(), ignoreCase: true, out var mode))
        {
            issues.Add(new ProfileIssue(
                modeKey,
                $"LLM proxy mode '{rawMode}' is not recognised. Use 'Off', 'System', or 'Custom'."));
            return issues;
        }

        if (mode != LlmProxyMode.Custom) return issues;

        if (!HasProxyAddress(configuration[addressKey], out var address))
        {
            issues.Add(new ProfileIssue(
                addressKey,
                "LLM proxy mode is 'Custom' but no proxy address is set. Enter a proxy URL "
                + "(e.g. http://proxy.corp.local:8080), or switch the mode to 'Off' or 'System'."));
            return issues;
        }

        if (!IsHttpProxyUrl(address, out _))
        {
            issues.Add(new ProfileIssue(
                addressKey,
                $"LLM proxy address '{address}' is not a valid http(s) URL."));
            return issues;
        }

        if (LlmEndpointGuard.IsCloudMetadataEndpoint(address))
        {
            issues.Add(new ProfileIssue(
                addressKey,
                $"SECURITY: the LLM proxy address ('{address}') points at a cloud-metadata endpoint. "
                + "This range (169.254.0.0/16, metadata.google.internal, metadata.azure.com) is always blocked."));
        }

        return issues;
    }

    /// <summary>
    /// First rule for a <c>Custom</c> proxy address: it has to be there. <paramref name="address"/>
    /// is the trimmed value both callers go on to use.
    /// </summary>
    public static bool HasProxyAddress(string? rawAddress, out string address)
    {
        address = rawAddress?.Trim() ?? "";
        return !string.IsNullOrWhiteSpace(address);
    }

    /// <summary>
    /// Second rule: an absolute http(s) URL. Shared with <see cref="LlmConfiguredProxy"/>, which
    /// builds the live proxy from the same value — the two must not disagree on what "valid" means,
    /// while each keeps its own wording for the rejection.
    /// </summary>
    public static bool IsHttpProxyUrl(string address, [NotNullWhen(true)] out Uri? url) =>
        Uri.TryCreate(address, UriKind.Absolute, out url)
        && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// True when <c>Llm:ActiveProfileId</c> names an existing profile. Read straight from
    /// configuration so it works on a simulated merged config (settings PUT) as well as at boot.
    /// </summary>
    public static bool HasResolvableActiveProfile(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var activeId = configuration[$"{LlmOptions.SectionName}:ActiveProfileId"];
        if (string.IsNullOrWhiteSpace(activeId)) return false;

        return configuration.GetSection(ProfilesKey).GetChildren()
            .Any(p => string.Equals(p.Key, activeId.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
