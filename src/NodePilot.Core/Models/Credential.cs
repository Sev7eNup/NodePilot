namespace NodePilot.Core.Models;

public class Credential
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public byte[] EncryptedPassword { get; set; } = [];
    public string? Domain { get; set; }

    /// <summary>
    /// Optional account-expiry timestamp (UTC). Purely advisory — NodePilot cannot
    /// rotate the underlying AD/Windows account; the CredentialExpiring gauge signal
    /// warns ahead of this date so a 2 a.m. run doesn't fail auth unannounced.
    /// Null = no expiry tracking for this credential.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}
