using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// Admin-managed key/value pairs accessible across all workflows via the
/// <c>{{globals.NAME}}</c> template. Secrets are stored DPAPI-encrypted (Base64 in
/// <see cref="GlobalVariable.Value"/>); non-secrets are stored plaintext.
/// </summary>
public interface IGlobalVariableStore
{
    Task<IReadOnlyList<GlobalVariable>> GetAllAsync(CancellationToken ct);

    /// <summary>Resolves a single variable to its plaintext value (decrypts secrets).</summary>
    Task<string?> GetValueAsync(string name, CancellationToken ct);

    /// <summary>
    /// Returns every global resolved to plaintext — called once per workflow execution so
    /// the engine can inject <c>globals.NAME</c> into every step's <c>Variables</c> dict
    /// without N+1 DB lookups. Broken secrets are silently skipped — callers that need to
    /// distinguish "missing" from "exists but undecryptable" use
    /// <see cref="GetAllResolvedDetailedAsync"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetAllResolvedAsync(CancellationToken ct);

    /// <summary>
    /// Like <see cref="GetAllResolvedAsync"/> but returns *both* the resolved values and
    /// the set of variable names that exist in the DB but could not be decrypted on this
    /// host (DPAPI scope mismatch, AES key changed, ciphertext corruption). The engine
    /// uses the unresolvable set to fail-loudly when a workflow step *references* such a
    /// variable, rather than silently substituting an empty string and letting the step
    /// run with a broken downstream call.
    /// </summary>
    Task<GlobalVariableResolutionResult> GetAllResolvedDetailedAsync(CancellationToken ct);

    Task<GlobalVariable> CreateAsync(string name, string value, bool isSecret, string? description,
        Guid folderId, string? updatedBy, CancellationToken ct);

    /// <summary>
    /// Null <paramref name="value"/> means "leave the existing value untouched" — so a
    /// caller can rename / retype a secret variable without having to know the old plaintext.
    /// Null <paramref name="folderId"/> means "leave the existing folder untouched" — so an
    /// update that only touches name/value/isSecret/description does not silently relocate the
    /// variable to Root. To move a variable, pass an explicit folder id (or use
    /// <see cref="MoveToFolderAsync"/>).
    /// </summary>
    Task UpdateAsync(Guid id, string name, string? value, bool isSecret, string? description,
        Guid? folderId, string? updatedBy, CancellationToken ct);

    /// <summary>
    /// Reassigns a variable to a different organizational folder. Purely cosmetic — does not
    /// change how <c>{{globals.NAME}}</c> resolves. Throws <see cref="KeyNotFoundException"/>
    /// if the variable does not exist.
    /// </summary>
    Task MoveToFolderAsync(Guid id, Guid folderId, string? updatedBy, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Re-encrypts every secret global variable with the currently active
    /// <c>ISecretProtector</c>. Used by the post-provider-rotation admin command:
    /// after switching <c>Secrets:Provider</c>, ciphertexts written by the old
    /// provider would only be re-encrypted lazily on first read. This command does
    /// the sweep proactively so a deployment with many rarely-read secrets doesn't carry
    /// a long tail of old-provider rows. Rows whose ciphertext can't be decrypted
    /// under any configured protector are skipped and listed in the result so the
    /// operator can re-enter them by hand — see <see cref="ReencryptionSummary"/>.
    /// </summary>
    Task<ReencryptionSummary> ReencryptAllSecretsAsync(CancellationToken ct);
}

/// <summary>
/// Tri-state result of bulk global-variable resolution. <see cref="Resolved"/> contains
/// every name that decoded cleanly to a plaintext value. <see cref="Unresolvable"/>
/// contains names that exist in the DB but failed to decrypt — referencing one in a
/// workflow template should fail the step loudly rather than substituting nothing.
/// Names that don't exist at all are absent from both — the engine treats those as
/// user typos and leaves the <c>{{globals.X}}</c> literal in place (existing behavior).
/// </summary>
public sealed record GlobalVariableResolutionResult(
    IReadOnlyDictionary<string, string> Resolved,
    IReadOnlySet<string> Unresolvable);
