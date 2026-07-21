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

        if (AllowedHosts is null)
        {
            yield return new ValidationResult(
                "AllowedHosts is required; use an empty array when no host is allowed.",
                [nameof(AllowedHosts)]);
            yield break;
        }

        if (AllowedHosts.Count > 256)
        {
            yield return new ValidationResult(
                "At most 256 exact outbound hosts may be allow-listed.",
                [nameof(AllowedHosts)]);
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configured in AllowedHosts)
        {
            var host = configured?.Trim() ?? string.Empty;
            var unbracketed = host.Length > 2 && host[0] == '[' && host[^1] == ']'
                ? host[1..^1]
                : host;
            var valid = host.Length is > 0 and <= 253
                        && !host.Contains("://", StringComparison.Ordinal)
                        && host.IndexOfAny(['/', '?', '#', '@']) < 0
                        && (IPAddress.TryParse(unbracketed, out _)
                            || Uri.CheckHostName(host) is UriHostNameType.Dns);
            if (!valid)
            {
                yield return new ValidationResult(
                    $"'{host}' is not an exact host name or IP address. Schemes, paths, ports, wildcards, and user-info are not allowed.",
                    [nameof(AllowedHosts)]);
            }
            else if (!seen.Add(host))
            {
                yield return new ValidationResult(
                    $"Duplicate outbound allow-list host '{host}'.",
                    [nameof(AllowedHosts)]);
            }
        }
    }
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
