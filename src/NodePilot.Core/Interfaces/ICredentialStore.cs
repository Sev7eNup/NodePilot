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
    /// Decrypts the credential's stored password and appends an audit entry pointing at the
    /// caller. <paramref name="actor"/> is free-form (user id, workflow execution id, "scheduler",
    /// …) so engine paths without an HTTP context can still provide a traceable identity.
    /// Passing null falls back to "unknown" — you almost never want that, because the audit
    /// entry then cannot be attributed during forensics (audit M11).
    /// </summary>
    string DecryptPassword(Credential credential, string? actor = null, Guid? workflowExecutionId = null);

    /// <summary>
    /// Re-encrypts every credential's password with the currently active
    /// <c>ISecretProtector</c>. Used by the post-provider-rotation admin command
    /// after switching <c>Secrets:Provider</c> so the deployment doesn't carry a
    /// long tail of old-provider ciphertexts that only get rewritten when something
    /// happens to read them. Returns rewrite + skip counts; rows that fail to
    /// decrypt are skipped (not aborted) and surfaced to the caller so an operator
    /// sees them rather than discovering the gap at next workflow run.
    /// </summary>
    Task<ReencryptionSummary> ReencryptAllCredentialsAsync(CancellationToken ct);
}

/// <summary>
/// Result of a bulk re-encrypt sweep. <see cref="Rewritten"/> is the number of rows
/// that were successfully decrypted and re-written under the active provider.
/// <see cref="Skipped"/> are rows whose ciphertext could not be decrypted under any
/// configured protector — those need manual re-entry by an admin and are listed in
/// <see cref="SkippedDetails"/> so the API response can name them. Splitting succeeded
/// from skipped here (rather than throwing on the first skip) is intentional: an admin
/// who just rotated the master key wants the sweep to make as much progress as it can,
/// then deal with the leftovers explicitly. A response with <c>Skipped&gt;0</c> is NOT
/// a clean success — the controller flags that distinctly.
/// </summary>
public sealed record ReencryptionSummary(
    int Rewritten,
    int Skipped,
    IReadOnlyList<ReencryptionSkip> SkippedDetails);

/// <summary>
/// One row that the sweep could not move to the active provider. <see cref="Reason"/>
/// is the exception type name (CryptographicException, FormatException, …) so the
/// admin can correlate to the operational cause without needing log access.
/// </summary>
public sealed record ReencryptionSkip(Guid Id, string Name, string Reason);
