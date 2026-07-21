namespace NodePilot.Core.Models;

/// <summary>
/// Short-lived server-side OIDC hand-off ticket. The browser receives only the random
/// <see cref="Id"/> handle, so principals with hundreds of group claims never inflate the
/// temporary callback cookie or reverse-proxy request headers.
/// </summary>
public sealed class OidcLoginTicket
{
    public string Id { get; set; } = string.Empty;
    public byte[] ProtectedPayload { get; set; } = [];
    public DateTime ExpiresAt { get; set; }
}
