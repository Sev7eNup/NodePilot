using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Data;

public class CredentialStore : ICredentialStore
{
    private readonly NodePilotDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly ILogger<CredentialStore>? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IAuditStager _auditStager;

    /// <summary>
    /// Pluggable secret-protector constructor. Marked with
    /// <see cref="ActivatorUtilitiesConstructorAttribute"/> so .NET's DI activator picks
    /// this one unambiguously even if a future overload is added — the previous
    /// IConfiguration-taking ctor was removed because both ctors had the same
    /// "all-resolvable" signature shape and the activator threw
    /// <c>AmbiguousMatchException</c> at runtime.
    /// <para>
    /// Tests that previously passed an <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
    /// must now construct an <see cref="ISecretProtector"/> explicitly — typically
    /// <c>new DpapiSecretProtector(DataProtectionScope.CurrentUser)</c> for the legacy
    /// behaviour.
    /// </para>
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public CredentialStore(
        NodePilotDbContext db,
        ISecretProtector protector,
        ILogger<CredentialStore>? logger = null,
        IServiceScopeFactory? scopeFactory = null,
        IAuditStager? auditStager = null)
    {
        _db = db;
        _protector = protector;
        _logger = logger;
        _scopeFactory = scopeFactory;
        // Stager-less fallback keeps unit-test ergonomics: tests constructing the store
        // directly don't have to thread DI through. Production wiring always supplies one.
        _auditStager = auditStager ?? new AuditStager();
    }

    public async Task<Credential> GetAsync(Guid id, CancellationToken ct)
    {
        return await _db.Credentials.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Credential {id} not found");
    }

    public async Task<IReadOnlyList<Credential>> GetAllAsync(CancellationToken ct)
    {
        return await _db.Credentials.OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<Credential> CreateAsync(string name, string username, string password, string? domain, DateTime? expiresAt, CancellationToken ct)
    {
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            Name = name,
            Username = username,
            EncryptedPassword = EncryptPassword(password),
            Domain = domain,
            ExpiresAt = NormalizeExpiry(expiresAt),
        };

        _db.Credentials.Add(credential);
        await _db.SaveChangesAsync(ct);
        return credential;
    }

    public async Task UpdateAsync(Guid id, string name, string username, string? password, string? domain, DateTime? expiresAt, CancellationToken ct)
    {
        var credential = await GetAsync(id, ct);
        credential.Name = name;
        credential.Username = username;
        credential.Domain = domain;
        credential.ExpiresAt = NormalizeExpiry(expiresAt);

        if (password is not null)
            credential.EncryptedPassword = EncryptPassword(password);

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Npgsql maps <c>DateTime?</c> to timestamptz and THROWS for Kind=Unspecified values —
    /// which is exactly what a date-only ISO string ("2026-12-31") deserializes to at the
    /// API/MCP boundary. Expiry dates are calendar dates: pin Unspecified to UTC, convert
    /// genuinely offset-carrying (Local) values.
    /// </summary>
    private static DateTime? NormalizeExpiry(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } v => v,
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        { } v => v.ToUniversalTime(),
    };

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var credential = await GetAsync(id, ct);
        _db.Credentials.Remove(credential);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ReencryptionSummary> ReencryptAllCredentialsAsync(CancellationToken ct)
    {
        // Decrypt with the (potentially MigratingSecretProtector-wrapped) protector, then
        // re-encrypt with the same instance — Protect always writes the active format, so
        // every successfully decrypted row ends up converted regardless of what it was
        // stored as. Rows that can't be decrypted are skipped (not thrown) so an admin
        // who half-rotated a key still gets every recoverable row migrated; the leftovers
        // come back in the response so the next admin step is concretely actionable.
        var rows = await _db.Credentials.ToListAsync(ct);
        var rewritten = 0;
        var skipped = new List<ReencryptionSkip>();
        foreach (var c in rows)
        {
            string plaintext;
            try { plaintext = _protector.Unprotect(c.EncryptedPassword); }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException || ex is ArgumentException)
            {
                _logger?.LogWarning(ex,
                    "Re-encrypt skipped credential '{Name}' (id={Id}, error={ErrorType}); " +
                    "stored ciphertext could not be decrypted.",
                    c.Name, c.Id, ex.GetType().Name);
                skipped.Add(new ReencryptionSkip(c.Id, c.Name, ex.GetType().Name));
                continue;
            }
            c.EncryptedPassword = _protector.Protect(plaintext);
            rewritten++;
        }
        if (rewritten > 0) await _db.SaveChangesAsync(ct);
        return new ReencryptionSummary(rewritten, skipped.Count, skipped);
    }

    public string DecryptPassword(Credential credential, string? actor = null, Guid? workflowExecutionId = null)
    {
        string plaintext;
        try
        {
            plaintext = _protector.Unprotect(credential.EncryptedPassword);
        }
        catch (CryptographicException ex)
        {
            // Crypto failure is almost always an operational misconfiguration:
            // - DPAPI: service-account change, DB restored on a different host, or
            //   Credentials:DpapiScope flipped after initial encryption.
            // - AES-GCM: Secrets:MasterKey changed, or the row was written under a
            //   different protector and the operator forgot to run the migration.
            // Log loudly and rethrow so the failure surfaces at the step/audit layer.
            _logger?.LogError(ex,
                "Secret decrypt failed for credential {CredentialId} ({CredentialName}) using provider '{Provider}'. " +
                "Likely causes: provider switched without re-encryption, key rotation without re-encryption, " +
                "or DB restored from a host with a different DPAPI binding.",
                credential.Id, credential.Name, _protector.ProviderName);
            throw;
        }

        // Audit every decryption. Failure to append the audit row must not break the caller
        // (a credential that was legitimately needed for a workflow step should still run)
        // but should be loud so operators notice the misconfiguration.
        //
        // Actor attribution (M11): the audit row used to just say "credential.decrypt at T"
        // with no way to tell which user/workflow triggered it. Callers now provide an actor
        // string (user id, "workflowExecution:{guid}", "scheduler", …) and optionally the
        // execution id, both embedded in Details as structured JSON for later SIEM queries.
        //
        // Scope isolation (M-6): CredentialStore is scoped (same DI-scope as WorkflowEngine's
        // per-step scope). Calling _db.SaveChanges() here would flush every other tracked
        // entity the engine has in flight (StepExecution rows, WorkflowExecution status
        // transitions, etc.), which can introduce subtle ordering bugs and lost updates.
        // Instead, persist the audit entry via an independent scope on a background task —
        // best-effort, swallowed on failure, never interferes with the decrypt path.
        //
        // Audit-stager flow (Phase 3): entry construction goes through IAuditStager so the
        // 4 KiB cap + secret redaction apply uniformly — the previous direct AuditLog.Add
        // bypassed both, meaning a careless protector that surfaced ciphertext in its
        // ProviderName would have leaked unredacted.
        var auditEntry = _auditStager.Build(
            // Naming alignment: every other audit code follows UPPER_SNAKE_CASE verb-noun;
            // this used to be "credential.decrypt" (dotted lowercase) which forced the UI
            // and SIEM rules to special-case it. Renamed to CREDENTIAL_DECRYPTED so it
            // groups naturally with CREDENTIAL_CREATED/UPDATED/DELETED.
            action: AuditActions.CredentialDecrypted,
            actor: AuditActor.System,
            resourceType: "Credential",
            resourceId: credential.Id,
            details: AuditDetails.Json(
                ("name", credential.Name),
                ("username", credential.Username),
                ("provider", _protector.ProviderName),
                ("actor", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor),
                ("workflowExecutionId", workflowExecutionId?.ToString())));

        if (_scopeFactory is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
                    db.AuditLog.Add(auditEntry);
                    await db.SaveChangesAsync();
                    AuditEventForwarder.ForwardCommitted(_logger, auditEntry);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Failed to append decrypt-audit entry for credential {CredentialId}",
                        credential.Id);
                }
            });
        }
        else
        {
            // Legacy path: no scope factory was injected (primarily unit tests that construct
            // CredentialStore directly). Fall back to the shared DbContext with the original
            // try/catch guard so tests don't need to wire a full DI graph.
            try
            {
                _db.AuditLog.Add(auditEntry);
                _db.SaveChanges();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Failed to append decrypt-audit entry for credential {CredentialId}",
                    credential.Id);
            }
        }

        return plaintext;
    }

    private byte[] EncryptPassword(string password) => _protector.Protect(password);
}
