using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.System;

[SupportedOSPlatform("windows")]
public sealed class HealthCommand : BaseCommand<GlobalSettings>
{
    public HealthCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    public override async Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings)
    {
        // Health works without authentication — bypass BaseCommand's "session required" check.
        var format = OutputFormatParser.Resolve(settings.Output);
        var writer = new OutputWriter(format, settings.NoColor || Console.IsOutputRedirected);
        try
        {
            var session = Sessions.Resolve(settings);
            if (!session.HasServer)
            {
                writer.Error("Kein Server konfiguriert.");
                return ExitCodes.Error;
            }
            var api = ClientFactory.Create(session, requireAuth: false);
            var (live, ready, detail, leaderStatus) = await api.HealthAsync(CancellationToken.None);

            writer.WriteData(new { Live = live, Ready = ready, Detail = detail, Leader = leaderStatus }, (console, v) =>
            {
                console.MarkupLine($"Live  : {(v.Live ? "[green]ok[/]" : "[red]down[/]")}");
                console.MarkupLine($"Ready : {(v.Ready ? "[green]ok[/]" : "[red]down[/]")}");
                if (!v.Ready && !string.IsNullOrEmpty(v.Detail))
                    console.MarkupLine($"[grey]{Spectre.Console.Markup.Escape(v.Detail)}[/]");
                if (!string.IsNullOrEmpty(v.Leader))
                {
                    var color = v.Leader switch
                    {
                        "leader" => "green",
                        "follower" => "yellow",
                        _ => "red",
                    };
                    console.MarkupLine($"Leader: [{color}]{Spectre.Console.Markup.Escape(v.Leader)}[/]");
                }
            });
            // In a High-Availability (HA) cluster, a passive follower node reports
            // leaderStatus "follower" and returns 503 on the leader probe by design —
            // that's expected, not a failure, so the exit code only reflects live+ready.
            return live && ready ? ExitCodes.Success : ExitCodes.Error;
        }
        catch (Exception ex)
        {
            writer.Error(ex.Message);
            return ExitCodes.Error;
        }
    }

    // Unreachable: ExecuteAsync above never dispatches here (health bypasses the
    // session-required flow). Present only to satisfy the abstract base member.
    protected override Task<int> RunAsync(CommandContext context, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
        => throw new NotImplementedException();
}
