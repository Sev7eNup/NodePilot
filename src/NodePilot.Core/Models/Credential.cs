namespace NodePilot.Core.Models;

public class Credential
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public byte[] EncryptedPassword { get; set; } = [];
    public string? Domain { get; set; }

    /// <summary>
    /// Optional account-expiry timestamp (UTC). Advisory only: NodePilot cannot rotate the
    /// underlying AD or Windows account. The CredentialExpiring gauge signal warns ahead of
    /// this date so an unattended run does not fail authentication without notice.
    /// Null = no expiry tracking for this credential.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}
