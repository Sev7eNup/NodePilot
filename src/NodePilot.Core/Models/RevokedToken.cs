namespace NodePilot.Core.Models;

/// <summary>
/// Tracks JWTs that have been explicitly revoked before their natural expiry (logout,
/// password change, admin-initiated kick). The <c>Jti</c> column matches the <c>jti</c>
/// claim in the token. Rows past their <c>ExpiresAt</c> can be pruned — after that time
/// the token fails normal lifetime validation anyway.
/// </summary>
public class RevokedToken
{
    public string Jti { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public DateTime RevokedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? Reason { get; set; }
}
