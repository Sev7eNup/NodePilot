using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// BCrypt hash for <see cref="AuthProvider.Local"/> users. Null for LDAP / Windows users —
    /// they authenticate against the external directory and never carry a local secret.
    /// The local-login path rejects any user whose <see cref="Provider"/> is not
    /// <see cref="AuthProvider.Local"/> or whose hash is null, so a row that has both a hash
    /// and a non-Local provider can never log in via the password endpoint.
    /// </summary>
    public string? PasswordHash { get; set; }

    public UserRole Role { get; set; } = UserRole.Viewer;

    /// <summary>
    /// Authentication source for this user. Defaults to <see cref="AuthProvider.Local"/>
    /// so existing rows + the bootstrap-admin keep working unchanged. JIT-provisioned users
    /// from the LDAP / Windows path persist their provider here.
    /// </summary>
    public AuthProvider Provider { get; set; } = AuthProvider.Local;

    /// <summary>
    /// Transitional projection of the canonical external subject. New AD rows store the
    /// account SID; upgraded LDAP rows may temporarily contain objectGUID until their next
    /// successful directory lookup. New login code resolves <see cref="ExternalIdentities"/>
    /// by Authority + Subject and does not use this property as its primary key.
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// Canonical external identities linked to this user. New authentication code resolves
    /// by <c>(Authority, Subject)</c>; <see cref="Provider"/> and <see cref="ExternalId"/>
    /// remain as transitional compatibility fields for backups and rolling upgrades.
    /// </summary>
    public ICollection<ExternalIdentity> ExternalIdentities { get; set; } = new List<ExternalIdentity>();

    /// <summary>
    /// JSON array of AD-Group SIDs cached at last login. Used by the group-aware
    /// authorization path to evaluate folder permissions without a per-request LDAP roundtrip.
    /// Refreshed on every successful login. Null until the user first logs in via an
    /// external provider.
    /// </summary>
    public string? KnownGroupSidsJson { get; set; }

    /// <summary>
    /// Deactivated users are rejected at login and by the JWT validation middleware, so an
    /// admin can revoke access without deleting the row (which would destroy the audit trail
    /// and break FK references from WorkflowExecution.TriggeredBy-style fields).
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Marks a local account as an explicitly audited emergency-login account.</summary>
    public bool IsBreakGlass { get; set; }

    /// <summary>
    /// Retained external-user tombstone. A tombstoned identity cannot be recreated by JIT.
    /// </summary>
    public bool IsTombstoned { get; set; }

    /// <summary>UTC timestamp of the last authoritative directory snapshot.</summary>
    public DateTime? LastDirectorySyncAt { get; set; }

    /// <summary>Short machine-readable state for directory sync and admin diagnostics.</summary>
    public string? DirectorySyncStatus { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp of the most recent password change (and the initial password set). The
    /// TokenValidityMiddleware rejects any bearer token whose <c>iat</c> claim is older
    /// than this value, so an admin resetting a compromised user's password immediately
    /// invalidates every existing session for that user without manually listing each
    /// token's jti.
    /// </summary>
    public DateTime PasswordChangedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of consecutive failed login attempts since the last successful login. Reset
    /// to 0 on a successful login. Used by the login controller/rate-limit pipeline to
    /// trigger account lockout once a threshold is exceeded — cheap defense against online
    /// password-guessing attacks that bypass per-IP rate limits (e.g. botnets).
    /// </summary>
    public int FailedLoginCount { get; set; }

    /// <summary>
    /// When non-null and in the future, login attempts are rejected with
    /// "account temporarily locked" regardless of password correctness. Cleared on successful
    /// login (once past <see cref="LockedUntil"/>) or by an admin-initiated reset.
    /// </summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>
    /// H-1 (security audit 2026-05-15): monotonically increasing version that authorization
    /// claims must match. The JWT carries the value at mint time as the <c>np_secstamp</c>
    /// claim; the <c>TokenValidityMiddleware</c> compares it against the current row value
    /// and rejects any token whose stamp is stale.
    ///
    /// <para>
    /// Bumped on every change that must invalidate existing sessions for this user:
    /// role changes (a demoted Admin must lose Admin scope immediately, not at 12h-token-
    /// expiry), activation toggles, and password resets (the latter already uses
    /// <see cref="PasswordChangedAt"/> too — the stamp gives forensic granularity even when
    /// two resets land in the same millisecond).
    /// </para>
    /// </summary>
    public int SecurityStamp { get; set; } = 0;
}
