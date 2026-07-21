using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Config;

public sealed class ConfigSetSettings : GlobalSettings
{
    [CommandArgument(0, "<KEY>")]
    [Description("Config key — currently 'server' or 'default-profile'.")]
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

    public override Task<int> ExecuteAsync(CommandContext context, ConfigSetSettings settings)
    {
        var format = OutputFormatParser.Resolve(settings.Output);
        var writer = new OutputWriter(format, settings.NoColor);
        var cfg = _config.Load();
        var profile = _config.ResolveProfileName(settings.Profile, cfg);

        switch (settings.Key.ToLowerInvariant())
        {
            case "server":
                if (!cfg.Profiles.TryGetValue(profile, out var entry)) entry = new ProfileEntry();
                entry.Server = settings.Value;
                cfg.Profiles[profile] = entry;
                break;
            case "default-profile":
                cfg.DefaultProfile = settings.Value;
                break;
            default:
                writer.Error($"Unbekannter Key '{settings.Key}'. Erlaubt: server | default-profile.");
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

    public override Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings)
    {
        var format = OutputFormatParser.Resolve(settings.Output);
        var writer = new OutputWriter(format, settings.NoColor || Console.IsOutputRedirected);
        var cfg = _config.Load();
        var view = new
        {
            cfg.DefaultProfile,
            ConfigPath = _config.ConfigPath,
            Profiles = cfg.Profiles.Select(p => new { Name = p.Key, p.Value.Server }).ToList(),
        };
        writer.WriteData(view, (console, v) =>
        {
            console.MarkupLine($"Config: [grey]{Markup.Escape(v.ConfigPath)}[/]");
            console.MarkupLine($"Default Profile: [bold]{v.DefaultProfile}[/]");
            var t = new Table().Border(TableBorder.Rounded).AddColumn("Profile").AddColumn("Server");
            foreach (var p in v.Profiles) t.AddRow(p.Name, p.Server ?? "-");
            console.Write(t);
        });
        return Task.FromResult(ExitCodes.Success);
    }
}
