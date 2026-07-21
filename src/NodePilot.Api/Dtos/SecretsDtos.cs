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
    bool PartialSuccess);
