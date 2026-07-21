using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Audit;

public sealed class AuditListSettings : GlobalSettings
{
    [CommandOption("--action <CODE>")]
    [Description("Filter by exact action code (e.g. WORKFLOW_PUBLISHED).")]
    public string? Action { get; set; }

    [CommandOption("--user <GUID>")]
    [Description("Filter by actor user id.")]
    public Guid? UserId { get; set; }

    [CommandOption("--resource-type <TYPE>")]
    [Description("Filter by resource type (Workflow, Machine, ...).")]
    public string? ResourceType { get; set; }

    [CommandOption("--resource-id <GUID>")]
    [Description("Filter by exact resource id.")]
    public Guid? ResourceId { get; set; }

    [CommandOption("--since <ISO>")]
    [Description("Only entries with timestamp >= since (ISO-8601 UTC).")]
    public DateTime? Since { get; set; }

    [CommandOption("--until <ISO>")]
    [Description("Only entries with timestamp < until (ISO-8601 UTC).")]
    public DateTime? Until { get; set; }

    [CommandOption("--ip <ADDRESS>")]
    [Description("Filter by source IP address (exact match).")]
    public string? IpAddress { get; set; }

    [CommandOption("--after-ts <ISO>")]
    [Description("Cursor: Timestamp from a previous response's nextCursor.")]
    public DateTime? AfterTs { get; set; }

    [CommandOption("--after-id <GUID>")]
    [Description("Cursor: Id from a previous response's nextCursor.")]
    public Guid? AfterId { get; set; }

    [CommandOption("--limit <N>")]
    [Description("Page size (default 100, server max 500).")]
    public int? Limit { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class AuditListCommand : BaseCommand<AuditListSettings>
{
    public AuditListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, AuditListSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var page = await api.AuditAsync(
            settings.Action, settings.ResourceType, settings.ResourceId, settings.UserId,
            settings.IpAddress, settings.Since, settings.Until,
            settings.AfterTs, settings.AfterId, settings.Limit, ct);
        writer.WriteData(page, (console, p) => Renderers.Audit(console, p));
        return ExitCodes.Success;
    }
}
