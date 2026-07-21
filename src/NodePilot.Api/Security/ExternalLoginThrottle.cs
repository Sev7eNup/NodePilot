using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Database-backed per-identity attempt reservation for directory and unknown users.
/// Every admitted authentication attempt atomically occupies one of five slots before an
/// LDAP bind is made. Because the unique slot index lives in the shared database, a burst
/// cannot bypass the limit by racing requests or spreading them across HA nodes.
/// </summary>
public sealed class ExternalLoginThrottle
{
    private const int AttemptLimit = 5;
    internal const int MaximumUsernameLength = 200;
    private const string KeyPrefix = "login-throttle:v1:";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly Guid ScopeId = Guid.Empty;

    private readonly NodePilotDbContext _db;

    public ExternalLoginThrottle(NodePilotDbContext db)
    {
        _db = db;
    }

    public async Task<ExternalLoginReservation> TryReserveAsync(
        string username,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        var identityPrefix = BuildIdentityPrefix(username);
        await _db.IdempotencyKeys
            .Where(x => x.WorkflowId == ScopeId
                        && x.Key.StartsWith(identityPrefix)
                        && x.ExpiresAt <= utcNow)
            .ExecuteDeleteAsync(ct);

        for (var slot = 0; slot < AttemptLimit; slot++)
        {
            var key = identityPrefix + slot;
            var claim = new IdempotencyKey
            {
                Id = Guid.NewGuid(),
                Key = key,
                WorkflowId = ScopeId,
                ExecutionId = Guid.Empty,
                FirstSeenAt = utcNow,
                ExpiresAt = utcNow.Add(Window),
            };
            _db.IdempotencyKeys.Add(claim);

            try
            {
                await _db.SaveChangesAsync(ct);
                return ExternalLoginReservation.Allowed(
                    claim.Id,
                    key,
                    triggeredBlock: slot == AttemptLimit - 1);
            }
            catch (DbUpdateException)
            {
                // Do not clear the request DbContext: it may already track the User that the
                // controller will map after a successful bind. Detach only our rejected claim.
                _db.Entry(claim).State = EntityState.Detached;
                if (await _db.IdempotencyKeys.AsNoTracking()
                        .AnyAsync(x => x.WorkflowId == ScopeId && x.Key == key && x.ExpiresAt > utcNow, ct))
                {
                    continue;
                }

                // The write failed for a reason other than the expected unique-slot race.
                throw;
            }
        }

        return ExternalLoginReservation.Blocked();
    }

    public Task RecordSuccessAsync(string username, CancellationToken ct = default)
    {
        var identityPrefix = BuildIdentityPrefix(username);
        return _db.IdempotencyKeys
            .Where(x => x.WorkflowId == ScopeId && x.Key.StartsWith(identityPrefix))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Releases a provisional reservation when no credential verdict was obtained, for
    /// example when every domain controller is unavailable. Infrastructure outages must not
    /// consume a user's password-failure budget.
    /// </summary>
    public Task ReleaseAsync(ExternalLoginReservation reservation, CancellationToken ct = default)
    {
        if (!reservation.IsAllowed || reservation.ClaimId is null)
            return Task.CompletedTask;

        var claimId = reservation.ClaimId.Value;
        return _db.IdempotencyKeys
            .Where(x => x.Id == claimId && x.WorkflowId == ScopeId)
            .ExecuteDeleteAsync(ct);
    }

    internal Task<int> TrackedAttemptCountAsync(CancellationToken ct = default)
        => _db.IdempotencyKeys.AsNoTracking()
            .CountAsync(x => x.WorkflowId == ScopeId && x.Key.StartsWith(KeyPrefix), ct);

    internal static string BuildIdentityPrefix(string username)
    {
        // Bound attacker-controlled work before Trim/Unicode case conversion allocate a second
        // string. AuthController rejects overlong usernames; this defensive cap protects direct
        // callers and future authentication entry points as well.
        var bounded = username ?? string.Empty;
        if (bounded.Length > MaximumUsernameLength)
            bounded = bounded[..MaximumUsernameLength];
        var normalized = bounded.Trim().ToUpperInvariant();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        try
        {
            return KeyPrefix + Convert.ToHexString(SHA256.HashData(bytes)) + ":";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

public sealed record ExternalLoginReservation(
    bool IsAllowed,
    bool TriggeredBlock,
    Guid? ClaimId,
    string? StorageKey)
{
    internal static ExternalLoginReservation Allowed(Guid claimId, string key, bool triggeredBlock)
        => new(true, triggeredBlock, claimId, key);

    internal static ExternalLoginReservation Blocked()
        => new(false, false, null, null);
}
