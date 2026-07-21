namespace NodePilot.Core.Models;

public class ManagedMachine
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public int WinRmPort { get; set; } = 5985;
    // Default HTTP (5985) for compatibility with LAN deployments that haven't enabled SSL on
    // WinRM. Operators can opt in to HTTPS per-machine or enforce SSL globally with
    // Remote:RequireWinRmSsl=true (see WinRmSessionFactory).
    public bool UseSsl { get; set; }
    public Guid? DefaultCredentialId { get; set; }
    public string? Tags { get; set; }
    public DateTime? LastConnectivityCheck { get; set; }
    public bool IsReachable { get; set; }

    public Credential? DefaultCredential { get; set; }
}
