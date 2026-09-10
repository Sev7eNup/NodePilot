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

    [CommandOption("--tls-thumbprint <SHA256>")]
    [Description("Accept exactly this server certificate (SHA-256 fingerprint) even when the chain is untrusted; `np auth login` stores it in the profile. Ignored on an http:// URL.")]
    public string? TlsThumbprint { get; set; }

    [CommandOption("--insecure-tls")]
    [Description("Skip server certificate validation for this call only (never stored). A configured pin still wins.")]
    public bool InsecureTls { get; set; }

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
