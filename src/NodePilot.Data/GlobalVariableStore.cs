using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Data;

/// <summary>
/// Default <see cref="IGlobalVariableStore"/>. Routes all encrypt/decrypt of secret
/// global-variable values through <see cref="ISecretProtector"/>, the same pluggable
/// abstraction used by <see cref="CredentialStore"/>. So switching from DPAPI to AES-GCM
/// for cluster portability is a single DI registration change in
/// <see cref="SecretProtectorRegistry"/>; this class never sees the difference.
/// </summary>
public class GlobalVariableStore : IGlobalVariableStore
{
    private readonly NodePilotDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly ILogger<GlobalVariableStore>? _logger;

    /// <summary>
    /// Pluggable secret-protector constructor. Marked with
    /// <see cref="ActivatorUtilitiesConstructorAttribute"/> so DI picks this ctor
    /// unambiguously — the previous IConfiguration-taking overload was removed because
    /// it produced an <c>AmbiguousMatchException</c> at runtime when both ctors
    /// resolved equally.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public GlobalVariableStore(NodePilotDbContext db, ISecretProtector protector, ILogger<GlobalVariableStore>? logger = null)
    {
        _db = db;
        _protector = protector;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GlobalVariable>> GetAllAsync(CancellationToken ct)
        => await _db.GlobalVariables.OrderBy(v => v.Name).ToListAsync(ct);

    public async Task<string?> GetValueAsync(string name, CancellationToken ct)
    {
        var v = await _db.GlobalVariables.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Name == name, ct);
        return v is null ? null : Decode(v);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllResolvedAsync(CancellationToken ct)
    {
        var detailed = await GetAllResolvedDetailedAsync(ct);
        return detailed.Resolved;
    }

    public async Task<GlobalVariableResolutionResult> GetAllResolvedDetailedAsync(CancellationToken ct)
    {
        var rows = await _db.GlobalVariables.AsNoTracking().ToListAsync(ct);
        var dict = new Dictionary<string, string>(rows.Count, StringComparer.Ordinal);
        var broken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in rows)
        {
            try { dict[v.Name] = Decode(v); }
            // Per-row isolation: a single broken secret must not poison the whole map.
            // Three failure classes we tolerate by skipping just the one entry:
            //   - CryptographicException: scope mismatch, moved host, profile re-imaged,
            //     wrong AES key, missing tag.
            //   - FormatException: stored Value isn't valid Base64 (DB corruption / a
            //     manual UPDATE that stomped the column).
            //   - ArgumentException: AES-GCM ciphertext too short for header+nonce+tag.
            // Anything else (DB I/O, OOM, ThreadAborted) propagates so we don't silently
            // mask infrastructure problems as "secret unresolvable".
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException || ex is ArgumentException)
            {
                broken.Add(v.Name);
                _logger?.LogWarning(ex,
                    "Decrypt failed for global variable '{Name}' (id={Id}, error={ErrorType}); " +
                    "variable will be unresolvable until it is re-entered.",
                    v.Name, v.Id, ex.GetType().Name);
            }
        }
        return new GlobalVariableResolutionResult(dict, broken);
    }

    public async Task<GlobalVariable> CreateAsync(string name, string value, bool isSecret,
        string? description, Guid folderId, string? updatedBy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var v = new GlobalVariable
        {
            Id = Guid.NewGuid(),
            Name = name,
            Value = Encode(value, isSecret),
            IsSecret = isSecret,
            Description = description,
            FolderId = folderId,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = updatedBy,
        };
        _db.GlobalVariables.Add(v);
        await _db.SaveChangesAsync(ct);
        return v;
    }

    public async Task UpdateAsync(Guid id, string name, string? value, bool isSecret,
        string? description, Guid? folderId, string? updatedBy, CancellationToken ct)
    {
        var v = await _db.GlobalVariables.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"GlobalVariable {id} not found");

        // M-24: demoting a secret to non-secret without a new plaintext value would decrypt
        // the stored ciphertext and persist it in the clear — effectively an unintentional
        // secret leak into any future DB dump / audit export / UI read. Refuse the flip
        // unless the caller explicitly provides a new plaintext value (at which point the
        // old ciphertext is replaced rather than decrypted-into-cleartext).
        if (v.IsSecret && !isSecret && string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                "Cannot demote a secret global variable to non-secret without providing a new plaintext value. " +
                "Either provide a new Value, or keep IsSecret=true.");
        }

        v.Name = name;
        v.Description = description;
        // null folderId = "leave the existing folder untouched" (mirrors the value:null convention),
        // so a caller that doesn't echo a folderId — e.g. `np globals import --upsert` — does not
        // silently relocate the variable to Root. To move, pass an explicit folder id.
        if (folderId is not null) v.FolderId = folderId.Value;
        v.UpdatedAt = DateTime.UtcNow;
        v.UpdatedBy = updatedBy;

        // Secret toggle requires a value change: flipping IsSecret=true without providing a
        // fresh value would Base64-"encrypt" the old plaintext, which is clearly wrong.
        // Flipping IsSecret=false without a value is blocked above by the M-24 guard.
        if (value is not null)
        {
            v.Value = Encode(value, isSecret);
            v.IsSecret = isSecret;
        }
        else if (v.IsSecret != isSecret)
        {
            // Only reachable for the isSecret=false -> true promotion path with no new value.
            // Re-encode the CURRENT plaintext as ciphertext.
            var current = Decode(v);
            v.Value = Encode(current, isSecret);
            v.IsSecret = isSecret;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task MoveToFolderAsync(Guid id, Guid folderId, string? updatedBy, CancellationToken ct)
    {
        var v = await _db.GlobalVariables.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"GlobalVariable {id} not found");
        v.FolderId = folderId;
        v.UpdatedAt = DateTime.UtcNow;
        v.UpdatedBy = updatedBy;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var v = await _db.GlobalVariables.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"GlobalVariable {id} not found");
        _db.GlobalVariables.Remove(v);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ReencryptionSummary> ReencryptAllSecretsAsync(CancellationToken ct)
    {
        // Decrypt with the active protector (which falls back to legacy when wrapped
        // in MigratingSecretProtector) and re-encrypt with the same instance. Non-secret
        // rows are skipped — they're stored plaintext anyway. Decrypt failures are
        // recorded per-row and returned to the caller so the operator gets a complete
        // accounting (succeed + skipped + reasons), not just a "rewritten N" tally.
        var rows = await _db.GlobalVariables.Where(v => v.IsSecret).ToListAsync(ct);
        var rewritten = 0;
        var skipped = new List<ReencryptionSkip>();
        foreach (var v in rows)
        {
            string plaintext;
            try { plaintext = Decode(v); }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException || ex is ArgumentException)
            {
                _logger?.LogWarning(ex,
                    "Re-encrypt skipped global variable '{Name}' (id={Id}, error={ErrorType}); " +
                    "stored ciphertext could not be decrypted.",
                    v.Name, v.Id, ex.GetType().Name);
                skipped.Add(new ReencryptionSkip(v.Id, v.Name, ex.GetType().Name));
                continue;
            }
            v.Value = Encode(plaintext, isSecret: true);
            v.UpdatedAt = DateTime.UtcNow;
            rewritten++;
        }
        if (rewritten > 0) await _db.SaveChangesAsync(ct);
        return new ReencryptionSummary(rewritten, skipped.Count, skipped);
    }

    private string Encode(string plaintext, bool isSecret)
    {
        if (!isSecret) return plaintext;
        var protectedBytes = _protector.Protect(plaintext);
        return Convert.ToBase64String(protectedBytes);
    }

    private string Decode(GlobalVariable v)
    {
        if (!v.IsSecret) return v.Value;
        var protectedBytes = Convert.FromBase64String(v.Value);
        return _protector.Unprotect(protectedBytes);
    }
}
