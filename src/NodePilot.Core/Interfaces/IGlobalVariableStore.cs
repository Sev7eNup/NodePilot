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
    /// Returns every global resolved to plaintext. Called once per workflow execution so the
    /// engine can inject <c>globals.NAME</c> into every step's <c>Variables</c> dict with a
    /// single query. Secrets that fail to decrypt are skipped; callers that must distinguish
    /// "missing" from "exists but undecryptable" use <see cref="GetAllResolvedDetailedAsync"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetAllResolvedAsync(CancellationToken ct);

    /// <summary>
    /// Like <see cref="GetAllResolvedAsync"/>, but also returns the names that exist in the DB
    /// yet could not be decrypted on this host (DPAPI scope mismatch, changed AES key, corrupt
    /// ciphertext). The engine uses that set to fail a step which references such a variable,
    /// instead of substituting an empty string and letting a broken call go through.
    /// </summary>
    Task<GlobalVariableResolutionResult> GetAllResolvedDetailedAsync(CancellationToken ct);

    Task<GlobalVariable> CreateAsync(string name, string value, bool isSecret, string? description,
        Guid folderId, string? updatedBy, CancellationToken ct);

    /// <summary>
    /// Null <paramref name="value"/> means "leave the existing value untouched", so a caller can
    /// rename or retype a secret variable without knowing the old plaintext. Null
    /// <paramref name="folderId"/> means "leave the existing folder untouched", so an update that
    /// only touches name/value/isSecret/description does not relocate the variable to Root. To
    /// move a variable, pass an explicit folder id or use <see cref="MoveToFolderAsync"/>.
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
    /// Re-encrypts every secret global variable with the currently active <c>ISecretProtector</c>.
    /// Run after switching <c>Secrets:Provider</c> so rows written by the old provider are
    /// converted in one sweep instead of lazily on first read. Rows that no configured protector
    /// can decrypt are skipped and listed in the <see cref="ReencryptionSummary"/> for manual
    /// re-entry.
    /// </summary>
    Task<ReencryptionSummary> ReencryptAllSecretsAsync(CancellationToken ct);
}

/// <summary>
/// Result of bulk global-variable resolution. <see cref="Resolved"/> holds every name that
/// decoded to a plaintext value. <see cref="Unresolvable"/> holds names that exist in the DB but
/// failed to decrypt; referencing one in a workflow template fails the step. Names absent from
/// both do not exist, and the engine leaves the <c>{{globals.X}}</c> literal in place.
/// </summary>
public sealed record GlobalVariableResolutionResult(
    IReadOnlyDictionary<string, string> Resolved,
    IReadOnlySet<string> Unresolvable);
