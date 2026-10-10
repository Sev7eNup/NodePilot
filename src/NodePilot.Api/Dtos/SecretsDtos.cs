using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Dtos;

/// <summary>
/// Body of the re-encrypt response. Exposes the full skip accounting so an operator can
/// immediately see "47 moved, 3 still need manual re-entry".
/// </summary>
public sealed record ReencryptResult(
    int CredentialsRewritten,
    int CredentialsSkipped,
    IReadOnlyList<ReencryptionSkip> CredentialSkipDetails,
    int GlobalSecretsRewritten,
    int GlobalSecretsSkipped,
    IReadOnlyList<ReencryptionSkip> GlobalSecretSkipDetails,
    int WorkflowVersionsRewritten,
    int WorkflowVersionsSkipped,
    IReadOnlyList<ReencryptionSkip> WorkflowVersionSkipDetails,
    bool PartialSuccess)
{
    public int AgentMcpSecretsRewritten { get; init; }
    public int AgentMcpSecretsSkipped { get; init; }
    public IReadOnlyList<ReencryptionSkip> AgentMcpSecretSkipDetails { get; init; } = [];
    public int NotificationRoutesRewritten { get; init; }
    public int NotificationRoutesSkipped { get; init; }
    public IReadOnlyList<ReencryptionSkip> NotificationRouteSkipDetails { get; init; } = [];
    public int DispatchParametersRewritten { get; init; }
    public int DispatchParametersSkipped { get; init; }
    public IReadOnlyList<ReencryptionSkip> DispatchParameterSkipDetails { get; init; } = [];
    public int RuntimeSettingsFilesRewritten { get; init; }
    public int RuntimeSettingsFilesSkipped { get; init; }
    public IReadOnlyList<ReencryptionSkip> RuntimeSettingsFileSkipDetails { get; init; } = [];
}
