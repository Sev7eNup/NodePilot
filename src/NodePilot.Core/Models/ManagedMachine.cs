namespace NodePilot.Core.Models;

public class ManagedMachine
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public int WinRmPort { get; set; } = 5985;
    // Defaults to plain HTTP (port 5985) for LAN deployments without SSL on WinRM. HTTPS is
    // opt-in per machine, or enforced globally with Remote:RequireWinRmSsl=true.
    public bool UseSsl { get; set; }
    public Guid? DefaultCredentialId { get; set; }
    public string? Tags { get; set; }
    public DateTime? LastConnectivityCheck { get; set; }
    public bool IsReachable { get; set; }

    public Credential? DefaultCredential { get; set; }
}
