using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

public interface ICredentialStore
{
    Task<Credential> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Credential>> GetAllAsync(CancellationToken ct);
    Task<Credential> CreateAsync(string name, string username, string password, string? domain, DateTime? expiresAt, CancellationToken ct);
    Task UpdateAsync(Guid id, string name, string username, string? password, string? domain, DateTime? expiresAt, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Decrypts the credential's stored password and appends an audit entry naming the caller.
    /// <paramref name="actor"/> is free-form (user id, workflow execution id, "scheduler") so
    /// engine paths without an HTTP context can still supply a traceable identity. Passing null
    /// falls back to "unknown", which leaves the audit entry unattributable.
    /// </summary>
    string DecryptPassword(Credential credential, string? actor = null, Guid? workflowExecutionId = null);

    /// <summary>
    /// Re-encrypts every credential's password with the currently active <c>ISecretProtector</c>.
    /// Run by the admin command after switching <c>Secrets:Provider</c> so the deployment keeps no
    /// old-provider ciphertexts. Returns rewrite and skip counts; rows that fail to decrypt are
    /// skipped instead of aborting the sweep and are reported back to the caller.
    /// </summary>
    Task<ReencryptionSummary> ReencryptAllCredentialsAsync(CancellationToken ct);
}

/// <summary>
/// Result of a bulk re-encrypt sweep. <see cref="Rewritten"/> counts rows successfully decrypted
/// and re-written under the active provider. <see cref="Skipped"/> counts rows whose ciphertext no
/// configured protector could decrypt; they are listed in <see cref="SkippedDetails"/> so the API
/// response can name them, and need manual re-entry by an admin. Counting instead of throwing on
/// the first skip lets the sweep finish; a result with <c>Skipped&gt;0</c> is not a clean success
/// and the controller flags it separately.
/// </summary>
public sealed record ReencryptionSummary(
    int Rewritten,
    int Skipped,
    IReadOnlyList<ReencryptionSkip> SkippedDetails);

/// <summary>
/// One row the sweep could not move to the active provider. <see cref="Reason"/> is the exception
/// type name (CryptographicException, FormatException) so an admin can identify the cause without
/// log access.
/// </summary>
public sealed record ReencryptionSkip(Guid Id, string Name, string Reason);
