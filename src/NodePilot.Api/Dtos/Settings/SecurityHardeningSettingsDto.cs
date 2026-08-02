using System.ComponentModel.DataAnnotations;
using System.Net;

namespace NodePilot.Api.Dtos.Settings;

// One file holds the seven security-hardening DTOs because they're all flat,
// short, and operationally edited together. Each maps to its own top-level config
// root and gets its own Settings Schema entry — the UI groups them under a single
// "Sicherheit" tab with one card per root.

public sealed class RestApiSettingsDto : IValidatableObject
{
    public bool BlockPrivateNetworks { get; set; } = true;
    [Required] public List<string> AllowedHosts { get; set; } = new();
    [Required] public RestApiProxyDto Proxy { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Proxy is null)
        {
            yield return new ValidationResult("Proxy is required.", [nameof(Proxy)]);
        }
        else
        {
            var ctx = new ValidationContext(Proxy);
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(Proxy, ctx, results, validateAllProperties: true);
            foreach (var r in results)
                yield return new ValidationResult(r.ErrorMessage, r.MemberNames.Select(m => $"Proxy.{m}"));
        }

        foreach (var result in HostAllowList.Validate(AllowedHosts, nameof(AllowedHosts), "outbound"))
            yield return result;
    }
}

/// <summary>
/// Shared validation for the two exact-host allow-lists (<c>RestApi:AllowedHosts</c> and
/// <c>WaitForCondition:AllowedHosts</c>). They are separate settings on purpose — one governs
/// restApi's loopback/private-network exception, the other the PowerShell-backed probes — but
/// the accepted syntax is identical, so the rules live in one place.
/// </summary>
internal static class HostAllowList
{
    private const int MaxEntries = 256;

    public static IEnumerable<ValidationResult> Validate(
        List<string>? allowedHosts, string memberName, string listNoun)
    {
        if (allowedHosts is null)
        {
            yield return new ValidationResult(
                $"{memberName} is required; use an empty array when no host is allowed.",
                [memberName]);
            yield break;
        }

        if (allowedHosts.Count > MaxEntries)
        {
            yield return new ValidationResult(
                $"At most {MaxEntries} exact {listNoun} hosts may be allow-listed.",
                [memberName]);
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configured in allowedHosts)
        {
            var host = configured?.Trim() ?? string.Empty;
            var unbracketed = host.Length > 2 && host[0] == '[' && host[^1] == ']'
                ? host[1..^1]
                : host;
            // Exact host or IP only: a scheme, path, port, wildcard or user-info would make the
            // comparison in NetworkGuard.NormalizeHostForComparison silently never match.
            var valid = host.Length is > 0 and <= 253
                        && !host.Contains("://", StringComparison.Ordinal)
                        && host.IndexOfAny(['/', '?', '#', '@']) < 0
                        && (IPAddress.TryParse(unbracketed, out _)
                            || Uri.CheckHostName(host) is UriHostNameType.Dns);
            if (!valid)
            {
                yield return new ValidationResult(
                    $"'{host}' is not an exact host name or IP address. Schemes, paths, ports, wildcards, and user-info are not allowed.",
                    [memberName]);
            }
            else if (!seen.Add(host))
            {
                yield return new ValidationResult(
                    $"Duplicate {listNoun} allow-list host '{host}'.",
                    [memberName]);
            }
        }
    }
}

/// <summary>
/// Allow-list for the PowerShell-backed network probes of <c>waitForCondition</c>
/// (<c>portOpen</c> / <c>httpOk</c>). Separate from <see cref="RestApiSettingsDto.AllowedHosts"/>
/// so permitting "probe my own host" does not also open restApi's loopback exception, whose
/// URLs can be assembled from trigger payloads.
/// </summary>
public sealed class WaitForConditionSettingsDto : IValidatableObject
{
    /// <summary>Exact hosts the probes may target. Empty list rejects every probe.</summary>
    [Required] public List<string> AllowedHosts { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => HostAllowList.Validate(AllowedHosts, nameof(AllowedHosts), "probe");
}

public sealed class RestApiProxyDto
{
    public bool Enabled { get; set; }

    [StringLength(2048)]
    public string Address { get; set; } = "";

    public List<string> BypassList { get; set; } = new();

    [StringLength(255)]
    public string? Username { get; set; }

    /// <summary>SecretField semantics — <c>"__unchanged__"</c> keeps, plaintext rotates, null/empty clears.</summary>
    public string? Password { get; set; }
}

public sealed class FileSystemOperationSettingsDto
{
    public bool RejectTraversal { get; set; } = true;

    /// <summary>Allowed root directories when RejectTraversal=true. Empty = all paths allowed under the
    /// no-traversal guard. Each entry must be an absolute path.</summary>
    public List<string> AllowedRoots { get; set; } = new();
}

public sealed class SqlActivitySettingsDto
{
    public bool RequireConnectionRef { get; set; }
}

public sealed class StartProgramSettingsDto
{
    public bool DisallowShellExecute { get; set; } = true;
}

public sealed class WebhookSettingsDto
{
    public bool RequireSecret { get; set; } = true;
}

public sealed class ExternalTriggerSettingsDto
{
    /// <summary>SecretField semantics — empty/null disables the external-trigger endpoint.</summary>
    public string? ApiKey { get; set; }
}

public sealed class SecuritySettingsDto
{
    public bool StrictAllowedHosts { get; set; }

    [StringLength(2048)]
    public string AllowedHosts { get; set; } = "*";
}
