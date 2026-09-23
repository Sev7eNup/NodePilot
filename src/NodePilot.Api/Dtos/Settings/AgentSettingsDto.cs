using System.ComponentModel.DataAnnotations;
using NodePilot.Core.Agents;

namespace NodePilot.Api.Dtos.Settings;

public sealed class AgentSettingsDto : IValidatableObject
{
    public bool Enabled { get; set; } = true;
    public bool AllowServiceIdentity { get; set; }
    [Range(1, 32)] public int MaxConcurrentRuns { get; set; } = 2;
    [Range(1, 200)] public int SingleModelCalls { get; set; } = 20;
    [Range(1, 500)] public int SingleToolCalls { get; set; } = 40;
    [Range(1, 7200)] public int SingleTimeoutSeconds { get; set; } = 1200;
    [Range(1, 200)] public int TeamModelCalls { get; set; } = 100;
    [Range(1, 500)] public int TeamToolCalls { get; set; } = 500;
    [Range(1, 100)] public int TeamDelegations { get; set; } = 20;
    [Range(1, 7200)] public int TeamTimeoutSeconds { get; set; } = 1800;
    [Range(1, 3600)] public int ModelCallTimeoutSeconds { get; set; } = 180;
    [Range(256, 250_000)] public int ModelMaxOutputTokens { get; set; } = 250_000;
    [Range(4096, 1_000_000)] public int MaxContextCharacters { get; set; } = 250_000;
    [Range(1024, 64_000)] public int MaxToolOutputCharacters { get; set; } = 16_000;
    [Range(1024, 64_000)] public int MaxResultCharacters { get; set; } = 64_000;
    [Required, MaxLength(200)] public AgentMcpReadGrant[] ReadOnlyMcpTools { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ReadOnlyMcpTools?.Any(g => g is null || g.ServerId == Guid.Empty || string.IsNullOrWhiteSpace(g.ToolName)
            || g.ToolName.Length > 128 || !DateTimeOffset.TryParse(g.ServerUpdatedAt, out _) || g.ContractSha256?.Length != 64
            || !g.ContractSha256.All(Uri.IsHexDigit)) == true)
            yield return new ValidationResult("MCP read grants require a server revision, tool name and SHA-256 contract fingerprint.", [nameof(ReadOnlyMcpTools)]);
    }
}
