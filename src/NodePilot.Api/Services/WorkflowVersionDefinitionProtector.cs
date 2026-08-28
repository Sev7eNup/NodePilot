using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Services;

/// <summary>
/// Protects complete historic workflow definitions before they enter <c>WorkflowVersions</c>.
/// History is not executed directly and is only materialised through authorised API paths, so
/// storing an opaque envelope is safer than trying to identify individual literals inside arbitrary
/// scripts and migration payloads. Legacy plaintext rows remain readable during rolling upgrades.
/// </summary>
public sealed class WorkflowVersionDefinitionProtector(
    ISecretProtector protector,
    ILogger<WorkflowVersionDefinitionProtector> logger)
{
    internal const string StoragePrefix = "np:wfv:v1:";
    private const int MigrationBatchSize = 100;

    public bool IsProtected(string value) => value.StartsWith(StoragePrefix, StringComparison.Ordinal);

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (IsProtected(plaintext)) return plaintext;
        return ProtectWithActiveProvider(plaintext);
    }

    /// <summary>
    /// Returns plaintext for both current envelopes and legacy rows. A malformed/current envelope
    /// is never treated as plaintext: decoding or authentication failures propagate fail-closed.
    /// </summary>
    public string Unprotect(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (!IsProtected(stored)) return stored;

        var encoded = stored[StoragePrefix.Length..];
        if (encoded.Length == 0)
            throw new InvalidOperationException("Workflow version definition has an empty encrypted envelope.");

        return protector.Unprotect(Convert.FromBase64String(encoded));
    }

    /// <summary>
    /// Re-wraps every history definition with the active provider. When the registered protector is
    /// a migrating wrapper, <see cref="Unprotect"/> can read the legacy provider while protection
    /// always writes the active one. Per-row crypto failures are reported without preventing other
    /// recoverable rows from rotating.
    /// </summary>
    public async Task<ReencryptionSummary> ReencryptAllAsync(NodePilotDbContext db, CancellationToken ct)
    {
        var rewritten = 0;
        var skipped = new List<ReencryptionSkip>();
        Guid? lastWorkflowId = null;
        var lastVersion = 0;
        var lastId = Guid.Empty;

        while (true)
        {
            var query = db.WorkflowVersions.AsQueryable();
            if (lastWorkflowId is { } workflowCursor)
            {
                query = query.Where(v =>
                    v.WorkflowId.CompareTo(workflowCursor) > 0
                    || (v.WorkflowId == workflowCursor
                        && (v.Version > lastVersion
                            || (v.Version == lastVersion && v.Id.CompareTo(lastId) > 0))));
            }

            var batch = await query
                .OrderBy(v => v.WorkflowId)
                .ThenBy(v => v.Version)
                .ThenBy(v => v.Id)
                .Take(MigrationBatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var row in batch)
            {
                try
                {
                    var plaintext = Unprotect(row.DefinitionJson);
                    row.DefinitionJson = ProtectWithActiveProvider(plaintext);
                    rewritten++;
                }
                catch (Exception ex) when (ex is CryptographicException
                                           or FormatException
                                           or ArgumentException
                                           or InvalidOperationException)
                {
                    logger.LogWarning(ex,
                        "Re-encrypt skipped workflow version '{Name}' v{Version} (id={Id}, error={ErrorType}); " +
                        "stored definition could not be decrypted.",
                        row.Name, row.Version, row.Id, ex.GetType().Name);
                    skipped.Add(new ReencryptionSkip(
                        row.Id, $"{row.Name} v{row.Version}", ex.GetType().Name));
                }
            }

            if (batch.Any(row => db.Entry(row).Property(v => v.DefinitionJson).IsModified))
                await db.SaveChangesAsync(ct);

            // Advance by the last stable key, not an offset. Retention may delete already-processed
            // rows while a long rotation runs; such deletes must not shift a live OFFSET window and
            // make an untouched legacy-provider row disappear from the sweep.
            var cursor = batch[^1];
            lastWorkflowId = cursor.WorkflowId;
            lastVersion = cursor.Version;
            lastId = cursor.Id;
            db.ChangeTracker.Clear();
            logger.LogDebug(
                "Re-encrypted workflow-version batch through workflow {WorkflowId}, version {Version}, id {Id}.",
                lastWorkflowId, lastVersion, lastId);
        }

        return new ReencryptionSummary(rewritten, skipped.Count, skipped);
    }

    /// <summary>
    /// Reports whether an upgraded database still contains legacy plaintext history. The startup
    /// path is deliberately read-only: rewriting rows before the updater's health check would make
    /// its binary rollback unsafe, and in HA a newly upgraded passive node must not mutate data
    /// that
    /// the still-active older binary cannot read. Administrators perform the cutover explicitly via
    /// <c>POST /api/secrets/reencrypt</c> after every node is on the new version.
    /// </summary>
    public async Task<bool> WarnIfExplicitMigrationRequiredAsync(NodePilotDbContext db, CancellationToken ct)
    {
        var required = await db.WorkflowVersions.AsNoTracking()
            .AnyAsync(v => !v.DefinitionJson.StartsWith(StoragePrefix), ct);
        if (required)
        {
            logger.LogWarning(
                "Legacy plaintext workflow-version definitions remain. After every NodePilot node " +
                "has been upgraded and the binary rollback window has closed, run POST " +
                "/api/secrets/reencrypt (or 'np secrets reencrypt') to protect workflow history.");
        }

        return required;
    }

    private string ProtectWithActiveProvider(string plaintext) =>
        StoragePrefix + Convert.ToBase64String(protector.Protect(plaintext));
}
