namespace NodePilot.Api.Configuration.Validators;

/// <summary>
/// Pre-flight checks for active/passive cluster mode. Replaces the standalone
/// <c>ClusterConfigValidator</c>; the rule set is unchanged: when
/// <c>Cluster:Enabled=true</c>, the JWT trio (Key / Issuer / Audience) must be set
/// explicitly so both nodes sign + validate against the same values, and
/// <c>Cluster:NodeId</c> must not look like an auto-generated container hash because
/// those rebind on every container restart and would make OwnerNodeId tracking
/// useless.
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
    /// Heuristic: a hostname that's exactly 12 hex chars (common Docker default) or that
    /// matches a UUID-ish pattern is almost certainly an auto-generated container ID.
    /// Tested explicitly against actual hostnames is impossible cross-platform, so we
    /// just refuse the most likely-broken case and let the operator override with
    /// <c>Cluster:NodeId</c>.
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
