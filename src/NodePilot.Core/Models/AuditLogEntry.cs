namespace NodePilot.Core.Models;

public class AuditLogEntry
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? ResourceType { get; set; }
    public Guid? ResourceId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}
