using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;

namespace NodePilot.Data;

/// <summary>
/// Database-backed, cluster-wide replay guard for authenticated webhook deliveries.
/// Reuses the existing idempotency-key table so all API nodes contend on the same unique
/// index and the existing leader-only cleanup service removes expired claims.
/// </summary>
public sealed class WebhookReplayStore
{
    private const string KeyPrefix = "webhook-replay:v1:";
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    private readonly NodePilotDbContext _db;

    public WebhookReplayStore(NodePilotDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Atomically claims an opaque, key-derived delivery token for a workflow. Returns false
    /// when another request or cluster node has already claimed it. A successful claim is
    /// deliberately not rolled back when dispatch later fails: an authenticated delivery is
    /// single-use and the sender must create a new timestamp/delivery ID for a retry.
    /// </summary>
    public async Task<bool> TryClaimAsync(
        Guid workflowId,
        string claimToken,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var key = BuildStorageKey(claimToken);
        _db.IdempotencyKeys.Add(new IdempotencyKey
        {
            Id = Guid.NewGuid(),
            Key = key,
            WorkflowId = workflowId,
            // Replay claims do not represent an execution. Guid.Empty also prevents the
            // dispatch maintenance-window cleanup from mistaking them for trigger response
            // cache entries, which carry a real execution ID.
            ExecutionId = Guid.Empty,
            FirstSeenAt = nowUtc,
            // Five minutes of allowed clock skew means a future-dated request can remain
            // cryptographically fresh for almost ten minutes. Fifteen minutes keeps the
            // unique claim alive beyond that entire window, with cleanup margin.
            ExpiresAt = nowUtc.Add(Retention),
        });

        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // A failed SaveChanges leaves the added entity tracked. Clear before querying so
            // a subsequent dispatch cannot accidentally retry the rejected INSERT.
            _db.ChangeTracker.Clear();

            if (await _db.IdempotencyKeys.AsNoTracking()
                    .AnyAsync(x => x.WorkflowId == workflowId && x.Key == key, ct))
            {
                return false;
            }

            // Not a unique-index race (for example, database connectivity or storage error).
            // Preserve the operational failure rather than misreporting it as a replay.
            throw;
        }
    }

    /// <summary>
    /// Derives an opaque claim token from the webhook key and delivery ID. Keying this digest
    /// prevents a holder of the unrelated external-trigger API key from pre-claiming the
    /// reserved idempotency-table namespace to deny a webhook delivery.
    /// </summary>
    public static string CreateClaimToken(ReadOnlySpan<byte> webhookKey, string deliveryId)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, webhookKey);
        hmac.AppendData("NodePilot.WebhookReplay.v1\0"u8);
        hmac.AppendData(Encoding.UTF8.GetBytes(deliveryId));
        return Convert.ToHexString(hmac.GetHashAndReset());
    }

    internal static string BuildStorageKey(string claimToken) => KeyPrefix + claimToken;
}
