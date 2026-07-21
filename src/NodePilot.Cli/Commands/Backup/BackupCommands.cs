using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Backup;

/// <summary>
/// Resolves the backup passphrase without ever accepting it as a plain CLI flag (ADR 0001) —
/// a flag would leak via the process list and shell history. Order: env var, then file, then an
/// interactive secret prompt (only when stdin is a terminal). Returns null + an error message
/// when no source is available (e.g. headless run with neither option set).
/// </summary>
internal static class PassphraseResolver
{
    public static (string? Passphrase, string? Error) Resolve(string? envVar, string? file, string promptLabel)
    {
        if (!string.IsNullOrWhiteSpace(envVar))
        {
            var v = Environment.GetEnvironmentVariable(envVar);
            return string.IsNullOrEmpty(v)
                ? (null, $"Environment variable '{envVar}' is not set or empty.")
                : (v, null);
        }
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file)) return (null, $"Passphrase file not found: {file}");
            // Trim a single trailing newline so a file written with `echo` works as expected.
            var content = File.ReadAllText(file).TrimEnd('\r', '\n');
            return string.IsNullOrEmpty(content)
                ? (null, $"Passphrase file is empty: {file}")
                : (content, null);
        }
        if (!Console.IsInputRedirected)
        {
            var entered = AnsiConsole.Prompt(new TextPrompt<string>($"{promptLabel}:").Secret());
            return string.IsNullOrEmpty(entered) ? (null, "No passphrase entered.") : (entered, null);
        }
        return (null, "No passphrase source. Use --passphrase-env <VAR> or --passphrase-file <PATH> in non-interactive runs.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class BackupManifestCommand : BaseCommand<GlobalSettings>
{
    public BackupManifestCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var manifest = await api.GetBackupManifestAsync(ct);

        var table = new Table().AddColumn("Section").AddColumn(new TableColumn("Count").RightAligned());
        foreach (var s in manifest.Sections)
            table.AddRow(Markup.Escape(s.Section), s.Count.ToString());
        AnsiConsole.Write(table);
        return ExitCodes.Success;
    }
}

public sealed class BackupExportSettings : GlobalSettings
{
    [CommandOption("--out <PATH>")]
    [Description("Write the .npbackup archive to this file (required).")]
    public string? Out { get; set; }

    [CommandOption("--sections <LIST>")]
    [Description("Comma-separated section keys, or 'all' (default). E.g. workflows,credentials,machines.")]
    public string Sections { get; set; } = "all";

    [CommandOption("--passphrase-env <VAR>")]
    [Description("Read the passphrase from this environment variable (preferred for cron/headless).")]
    public string? PassphraseEnv { get; set; }

    [CommandOption("--passphrase-file <PATH>")]
    [Description("Read the passphrase from this file (trailing newline trimmed).")]
    public string? PassphraseFile { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class BackupExportCommand : BaseCommand<BackupExportSettings>
{
    public BackupExportCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, BackupExportSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Out))
        {
            writer.Error("--out <PATH> is required (the backup is a binary archive, not stdout).");
            return ExitCodes.Error;
        }

        var (passphrase, err) = PassphraseResolver.Resolve(settings.PassphraseEnv, settings.PassphraseFile, "Backup passphrase");
        if (passphrase is null) { writer.Error(err!); return ExitCodes.Error; }

        var api = ClientFactory.Create(session);

        List<string> sections;
        if (string.IsNullOrWhiteSpace(settings.Sections) || settings.Sections.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var manifest = await api.GetBackupManifestAsync(ct);
            sections = manifest.Sections.Select(s => s.Section).ToList();
        }
        else
        {
            sections = settings.Sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        var (content, warnings) = await api.ExportBackupAsync(sections, passphrase, ct);
        await File.WriteAllBytesAsync(settings.Out, content, ct);

        writer.Success($"Backup geschrieben: {settings.Out} ({content.Length:N0} bytes, sections: {string.Join(", ", sections)})");
        if (warnings > 0)
            writer.Warning($"{warnings} Warnung(en) beim Export — siehe Server-Log (z. B. nicht entschlüsselbare Secrets auf diesem Host).");
        return ExitCodes.Success;
    }
}

public sealed class BackupPreviewSettings : GlobalSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("Path to the .npbackup archive to inspect.")]
    public string File { get; set; } = "";

    [CommandOption("--passphrase-env <VAR>")]
    [Description("Optional: env var with the passphrase. Without it, integrity is not verified.")]
    public string? PassphraseEnv { get; set; }

    [CommandOption("--passphrase-file <PATH>")]
    [Description("Optional: file with the passphrase.")]
    public string? PassphraseFile { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class BackupPreviewCommand : BaseCommand<BackupPreviewSettings>
{
    public BackupPreviewCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, BackupPreviewSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!File.Exists(settings.File)) { writer.Error($"Datei nicht gefunden: {settings.File}"); return ExitCodes.Error; }

        // Passphrase optional for preview — only resolve if one of the options was given.
        string? passphrase = null;
        if (!string.IsNullOrWhiteSpace(settings.PassphraseEnv) || !string.IsNullOrWhiteSpace(settings.PassphraseFile))
        {
            var (p, err) = PassphraseResolver.Resolve(settings.PassphraseEnv, settings.PassphraseFile, "Backup passphrase");
            if (p is null) { writer.Error(err!); return ExitCodes.Error; }
            passphrase = p;
        }

        var api = ClientFactory.Create(session);
        var content = await File.ReadAllBytesAsync(settings.File, ct);
        var result = await api.PreviewBackupAsync(content, passphrase, ct);

        writer.Info($"Backup app version: {result.AppVersion ?? "?"} — integrity {(result.IntegrityVerified ? "[green]verified[/]" : "[yellow]unverified[/]")}");
        var table = new Table().AddColumn("Section").AddColumn("In backup").AddColumn("New").AddColumn("Conflicts");
        foreach (var s in result.Sections)
            table.AddRow(Markup.Escape(s.Section), s.InBackup.ToString(), s.New.ToString(), s.Conflicts.ToString());
        AnsiConsole.Write(table);
        foreach (var w in result.Warnings) writer.Warning($"  ! {w}");
        return ExitCodes.Success;
    }
}

public sealed class BackupRestoreSettings : GlobalSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("Path to the .npbackup archive to restore.")]
    public string File { get; set; } = "";

    [CommandOption("--passphrase-env <VAR>")]
    [Description("Env var with the passphrase (preferred for headless).")]
    public string? PassphraseEnv { get; set; }

    [CommandOption("--passphrase-file <PATH>")]
    [Description("File with the passphrase.")]
    public string? PassphraseFile { get; set; }

    [CommandOption("--policy <SPEC>")]
    [Description("Conflict policy: a bare value (skip|rename|overwrite) for all, and/or section=policy pairs, e.g. 'skip,users=overwrite'. Default: skip.")]
    public string? Policy { get; set; }

    [CommandOption("--yes")]
    [Description("Skip the confirmation prompt (required for non-interactive restore).")]
    public bool Yes { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class BackupRestoreCommand : BaseCommand<BackupRestoreSettings>
{
    public BackupRestoreCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, BackupRestoreSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!File.Exists(settings.File)) { writer.Error($"Datei nicht gefunden: {settings.File}"); return ExitCodes.Error; }

        var (passphrase, err) = PassphraseResolver.Resolve(settings.PassphraseEnv, settings.PassphraseFile, "Backup passphrase");
        if (passphrase is null) { writer.Error(err!); return ExitCodes.Error; }

        if (!settings.Yes)
        {
            if (Console.IsInputRedirected)
            {
                writer.Error("Restore ist destruktiv — in nicht-interaktiven Läufen mit --yes bestätigen.");
                return ExitCodes.Error;
            }
            if (!AnsiConsole.Confirm("Restore überschreibt/erzeugt System-Konfiguration. Fortfahren?", defaultValue: false))
                return ExitCodes.Success;
        }

        var api = ClientFactory.Create(session);
        var content = await File.ReadAllBytesAsync(settings.File, ct);
        var result = await api.RestoreBackupAsync(content, passphrase, settings.Policy, ct);

        var table = new Table().AddColumn("Section").AddColumn("Created").AddColumn("Overwritten").AddColumn("Skipped").AddColumn("Renamed");
        foreach (var s in result.Sections)
            table.AddRow(Markup.Escape(s.Section), s.Created.ToString(), s.Overwritten.ToString(), s.Skipped.ToString(), s.Renamed.ToString());
        AnsiConsole.Write(table);
        if (result.Settings is not null)
            writer.Info($"Settings: {(result.Settings.Applied ? "[green]applied[/]" : "[yellow]not applied[/]")} — {Markup.Escape(result.Settings.Message ?? "")}");
        foreach (var w in result.Warnings) writer.Warning($"  ! {w}");
        writer.Success("Restore abgeschlossen.");
        return ExitCodes.Success;
    }
}
