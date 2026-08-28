namespace NodePilot.Api.Configuration.Validators;

/// <summary>
/// Pre-flight checks for active/passive cluster mode. With <c>Cluster:Enabled=true</c> the JWT
/// keys (Key, Issuer, Audience) must be set explicitly so both nodes sign and validate against
/// the same values, and <c>Cluster:NodeId</c> must not look like an auto-generated container
/// hash, because such names change on every container restart and would make OwnerNodeId
/// tracking useless.
/// </summary>
public sealed class ClusterBootValidator : IBootValidator
{
    public string Name => "Cluster";

    public void Validate(IConfiguration configuration, IList<BootValidationIssue> issues)
    {
        if (!bool.TryParse(configuration["Cluster:Enabled"], out var enabled) || !enabled)
            return;

        ValidateRequired(configuration, issues, "Jwt:Key",
            "auto-generated jwt-secret.key would diverge between nodes — every cookie issued by node A would 401 on node B after failover");
        ValidateRequired(configuration, issues, "Jwt:Issuer",
            "the 'NodePilot' default fallback is implicit and invisible — must be explicit so both nodes agree on what to validate");
        ValidateRequired(configuration, issues, "Jwt:Audience",
            "same reasoning as Jwt:Issuer");

        var configuredNodeId = configuration["Cluster:NodeId"];
        if (string.IsNullOrWhiteSpace(configuredNodeId))
        {
            var machineName = Environment.MachineName ?? string.Empty;
            if (LooksLikeContainerHash(machineName))
            {
                issues.Add(new BootValidationIssue(
                    Name, BootValidationSeverity.Error, "Cluster:NodeId",
                    $"Environment.MachineName='{machineName}' looks like an auto-generated container ID — " +
                    "set this explicitly so OwnerNodeId stays stable across container restarts."));
            }
        }
    }

    private void ValidateRequired(IConfiguration config, IList<BootValidationIssue> issues, string key, string reason)
    {
        if (string.IsNullOrWhiteSpace(config[key]))
        {
            issues.Add(new BootValidationIssue(
                Name, BootValidationSeverity.Error, key,
                $"Cluster:Enabled=true requires this key to be set explicitly ({reason})."));
        }
    }

    /// <summary>
    /// Heuristic: a hostname made only of hex characters and dashes is most likely an
    /// auto-generated container ID, such as the 12 hex characters Docker uses by default.
    /// Detecting this reliably across platforms is not possible, so only the clearest case is
    /// rejected and the operator can override it with <c>Cluster:NodeId</c>.
    /// </summary>
    public static bool LooksLikeContainerHash(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Length is < 12 or > 64) return false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            var isDash = c == '-';
            if (!isHex && !isDash) return false;
        }
        return true;
    }
}
