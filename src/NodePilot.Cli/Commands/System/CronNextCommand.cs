using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.System;

public sealed class CronNextSettings : GlobalSettings
{
    [CommandArgument(0, "<CRON>")]
    [Description("Quartz 7-field cron expression.")]
    public string Cron { get; set; } = "";

    [CommandOption("--count <N>")]
    [Description("How many fire times to return (1-20, default 5).")]
    public int Count { get; set; } = 5;
}

[SupportedOSPlatform("windows")]
public sealed class CronNextCommand : BaseCommand<CronNextSettings>
{
    public CronNextCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, CronNextSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var result = await api.CronNextFiresAsync(settings.Cron, settings.Count, ct);
        writer.WriteData(result, (console, v) =>
        {
            console.MarkupLine($"[bold]{Markup.Escape(v.Summary)}[/]");
            foreach (var f in v.Fires)
                console.MarkupLine($"  {f.ToLocalTime():u}");
        });
        return ExitCodes.Success;
    }
}
