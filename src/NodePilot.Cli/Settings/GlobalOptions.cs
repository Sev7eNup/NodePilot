using System.ComponentModel;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Settings;

/// <summary>
/// Settings shared across every command. Subclass this and the flags appear on every
/// subcommand without duplication.
/// </summary>
public class GlobalSettings : CommandSettings
{
    [CommandOption("--server <URL>")]
    [Description("Override the configured NodePilot server URL for this call.")]
    public string? Server { get; set; }

    [CommandOption("--allow-insecure")]
    [Description("Allow HTTP only for an explicit loopback server (development only).")]
    public bool AllowInsecureLoopback { get; set; }

    [CommandOption("--profile <NAME>")]
    [Description("Use a named connection profile (default: 'default').")]
    public string? Profile { get; set; }

    [CommandOption("-o|--output <FORMAT>")]
    [Description("Output format: table (default for TTY) | json | yaml.")]
    public string? Output { get; set; }

    [CommandOption("--no-color")]
    [Description("Disable colored output (also auto-disabled when stdout is redirected).")]
    public bool NoColor { get; set; }

    [CommandOption("-v|--verbose")]
    [Description("Print HTTP request/response trace lines on stderr.")]
    public bool Verbose { get; set; }
}
