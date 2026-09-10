using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using NodePilot.Core.Clients;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Config;

public sealed class ConfigSetSettings : GlobalSettings
{
    [CommandArgument(0, "<KEY>")]
    [Description("Config key — 'server', 'tls-thumbprint' or 'default-profile'.")]
    public string Key { get; set; } = "";

    [CommandArgument(1, "<VALUE>")]
    [Description("Value to set.")]
    public string Value { get; set; } = "";
}

[SupportedOSPlatform("windows")]
public sealed class ConfigSetCommand : AsyncCommand<ConfigSetSettings>
{
    private readonly ConfigStore _config;
    public ConfigSetCommand(ConfigStore config) => _config = config;

    protected override Task<int> ExecuteAsync(CommandContext context, ConfigSetSettings settings, CancellationToken ct)
    {
        var format = OutputFormatParser.Resolve(settings.Output);
        var writer = new OutputWriter(format, settings.NoColor);
        var cfg = _config.Load();
        var profile = _config.ResolveProfileName(settings.Profile, cfg);

        switch (settings.Key.ToLowerInvariant())
        {
            case "server":
            {
                if (!cfg.Profiles.TryGetValue(profile, out var entry)) entry = new ProfileEntry();
                // A pin vouches for one certificate at one origin. Pointing the profile elsewhere
                // drops it instead of silently extending it to the new server.
                if (entry.TlsThumbprint is not null
                    && !ClientSessionSecurity.HasSameServerOrigin(entry.Server, settings.Value))
                {
                    entry.TlsThumbprint = null;
                    writer.Warning("Gespeicherter TLS-Pin entfernt: er galt für den bisherigen Server.");
                }

                entry.Server = settings.Value;
                cfg.Profiles[profile] = entry;
                break;
            }

            case "tls-thumbprint":
            {
                if (!cfg.Profiles.TryGetValue(profile, out var entry)) entry = new ProfileEntry();
                // "none" clears it; a bare "-" would be parsed as an option by the arg parser.
                entry.TlsThumbprint = settings.Value is "none" or "clear" or ""
                    ? null
                    : TlsPinInput.Require(settings.Value, "--tls-thumbprint");
                cfg.Profiles[profile] = entry;
                break;
            }
            case "default-profile":
                cfg.DefaultProfile = settings.Value;
                break;
            default:
                writer.Error($"Unbekannter Key '{settings.Key}'. Erlaubt: server | tls-thumbprint | default-profile.");
                return Task.FromResult(ExitCodes.Error);
        }

        _config.Save(cfg);
        writer.Success($"Gespeichert: {settings.Key} = {settings.Value} (profile '{profile}')");
        return Task.FromResult(ExitCodes.Success);
    }
}

[SupportedOSPlatform("windows")]
public sealed class ConfigGetCommand : AsyncCommand<GlobalSettings>
{
    private readonly ConfigStore _config;
    public ConfigGetCommand(ConfigStore config) => _config = config;

    protected override Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings, CancellationToken ct)
    {
        var format = OutputFormatParser.Resolve(settings.Output);
        var writer = new OutputWriter(format, settings.NoColor || Console.IsOutputRedirected);
        var cfg = _config.Load();
        var view = new
        {
            cfg.DefaultProfile,
            ConfigPath = _config.ConfigPath,
            Profiles = cfg.Profiles
                .Select(p => new { Name = p.Key, p.Value.Server, p.Value.TlsThumbprint })
                .ToList(),
        };
        writer.WriteData(view, (console, v) =>
        {
            console.MarkupLine($"Config: [grey]{Markup.Escape(v.ConfigPath)}[/]");
            console.MarkupLine($"Default Profile: [bold]{v.DefaultProfile}[/]");
            var t = new Table().Border(TableBorder.Rounded)
                .AddColumn("Profile").AddColumn("Server").AddColumn("TLS-Pin (SHA-256)");
            foreach (var p in v.Profiles) t.AddRow(p.Name, p.Server ?? "-", p.TlsThumbprint ?? "-");
            console.Write(t);
        });
        return Task.FromResult(ExitCodes.Success);
    }
}
