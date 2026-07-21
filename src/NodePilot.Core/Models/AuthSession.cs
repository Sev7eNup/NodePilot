namespace NodePilot.Core.Models;

/// <summary>
/// Server-side handle for an authenticated browser or API session. JWTs only carry the
/// opaque <see cref="Id"/> and the user's current authorization version; revocation and
/// expiry are enforced from this row on every authenticated request.
/// </summary>
public sealed class AuthSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string AuthenticationMethod { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public int AuthorizationVersion { get; set; }
    /// <summary>The only JWT id currently valid for this session family.</summary>
    public string CurrentJti { get; set; } = string.Empty;
    /// <summary>Optimistic-concurrency generation incremented on every refresh.</summary>
    public int RefreshGeneration { get; set; }

    public User? User { get; set; }
}
