using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Settings;

// Branch: `np settings ...` — wraps /api/admin/settings/* (Admin role).
//
// Design: every mutation is file-based + ETag-gated, mirroring the API contract one-to-one.
// We deliberately do not provide a `set key=value` shortcut: the backend works on
// whole-section payloads with DataAnnotations + boot-validator pre-flight; piecing
// together a single key from the CLI would require local schema knowledge and would
// break on any backend additions. File-roundtrip is the honest interface.

/// <summary>
/// Renders a server-shaped JSON document while honouring the caller's <c>-o</c> choice:
/// <list type="bullet">
///   <item><c>-o table</c> (default): pretty-printed JSON (the natural shape).</item>
///   <item><c>-o json</c>: compact JSON (one line, no extra whitespace).</item>
///   <item><c>-o yaml</c>: YAML — same conversion the generic <c>WriteData</c> uses.</item>
/// </list>
/// Writing directly to <see cref="Console.Out"/> rather than <see cref="OutputWriter"/>'s
/// markup-aware console because JSON/YAML payloads must never be touched by Spectre's
/// markup parser.
/// </summary>
internal static class JsonShapedPrint
{
    public static void Write(OutputWriter writer, JsonDocument doc)
    {
        switch (writer.Format)
        {
            case OutputFormat.Json:
                Console.Out.WriteLine(JsonSerializer.Serialize(doc.RootElement));
                break;
            case OutputFormat.Yaml:
                Console.Out.Write(YamlEmitter.Emit(doc.RootElement));
                break;
            default:
                Console.Out.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
                break;
        }
    }
}

// ---- np settings status -----------------------------------------------------

[SupportedOSPlatform("windows")]
public sealed class SettingsStatusCommand : BaseCommand<GlobalSettings>
{
    public SettingsStatusCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var status = await api.GetSettingsStatusAsync(ct);
        writer.WriteData(status, (console, value) =>
        {
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("Overrides Path", Markup.Escape(value.OverridesPath));
            grid.AddRow("Restart Required", value.RestartRequired ? "[yellow]yes[/]" : "[green]no[/]");
            if (value.RestartRequired)
            {
                grid.AddRow("  Since", value.RestartRequiredSince?.ToString("u") ?? "-");
                grid.AddRow("  For Sections", value.RestartRequiredFor.Count == 0 ? "-" : Markup.Escape(string.Join(", ", value.RestartRequiredFor)));
            }
            grid.AddRow("Last Saved At", value.LastSavedAt?.ToString("u") ?? "-");
            grid.AddRow("Last Saved By", value.LastSavedBy ?? "-");
            console.Write(grid);
        });
        return ExitCodes.Success;
    }
}

// ---- np settings system-info -----------------------------------------------

[SupportedOSPlatform("windows")]
public sealed class SettingsSystemInfoCommand : BaseCommand<GlobalSettings>
{
    public SettingsSystemInfoCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var info = await api.GetSystemInfoAsync(ct);
        writer.WriteData(info, (console, value) =>
        {
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("App Version", value.AppVersion);
            grid.AddRow("Overrides Path", Markup.Escape(value.OverridesPath));
            grid.AddRow("Database Provider", value.DatabaseProvider);
            grid.AddRow("Database Host", value.DatabaseHost ?? "(not configured)");
            grid.AddRow("Secrets Provider", value.SecretsProvider);
            grid.AddRow("Cluster Enabled", value.ClusterEnabled ? "[green]yes[/]" : "no");
            grid.AddRow("Cluster Node ID", value.ClusterNodeId);
            grid.AddRow("Cluster Leader", value.ClusterIsLeader ? "[green]yes[/]" : "no");
            grid.AddRow("JWT Issuer", value.JwtIssuer);
            grid.AddRow("JWT Audience", value.JwtAudience);
            console.Write(grid);
        });
        return ExitCodes.Success;
    }
}

// ---- np settings get [section] ---------------------------------------------

public sealed class SettingsGetSettings : GlobalSettings
{
    [CommandArgument(0, "[SECTION]")]
    [Description("Section name (e.g. Smtp, Llm, Authentication). Omit for the full snapshot.")]
    public string? Section { get; set; }

    [CommandOption("--etag-only")]
    [Description("Print just the ETag for the section (suitable for shell capture).")]
    public bool EtagOnly { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SettingsGetCommand : BaseCommand<SettingsGetSettings>
{
    public SettingsGetCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SettingsGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);

        if (string.IsNullOrWhiteSpace(settings.Section))
        {
            if (settings.EtagOnly)
            {
                writer.Error("--etag-only setzt eine konkrete Section voraus.");
                return ExitCodes.Error;
            }
            using var snapshot = await api.GetSettingsSnapshotAsync(ct);
            JsonShapedPrint.Write(writer, snapshot);
            return ExitCodes.Success;
        }

        var (body, headerEtag) = await api.GetSettingsSectionAsync(settings.Section, ct);
        using (body)
        {
            if (settings.EtagOnly)
            {
                // Strip optional weak-validator prefix + surrounding quotes that .NET adds
                // back into the canonical header form. We hand the user exactly what they
                // need to put into the next `--etag` flag.
                var etag = headerEtag;
                if (string.IsNullOrEmpty(etag)
                    && body.RootElement.TryGetProperty("etag", out var inline))
                {
                    etag = inline.GetString();
                }
                await Console.Out.WriteLineAsync(etag ?? "");
                return ExitCodes.Success;
            }
            JsonShapedPrint.Write(writer, body);
            if (!string.IsNullOrEmpty(headerEtag))
                writer.Info($"[grey]ETag:[/] {Markup.Escape(headerEtag)}");
        }
        return ExitCodes.Success;
    }
}

// ---- np settings put <section> --file ... --etag ... -----------------------

public sealed class SettingsPutSettings : GlobalSettings
{
    [CommandArgument(0, "<SECTION>")]
    [Description("Section name (e.g. Smtp, Llm, Authentication).")]
    public string Section { get; set; } = "";

    [CommandOption("--file <PATH>")]
    [Description("Path to a JSON file containing the section payload. Use `-` to read stdin.")]
    public string? File { get; set; }

    [CommandOption("--etag <ETAG>")]
    [Description("Required If-Match ETag. Run `np settings get <section> --etag-only` to fetch it.")]
    public string? Etag { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SettingsPutCommand : BaseCommand<SettingsPutSettings>
{
    public SettingsPutCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SettingsPutSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.File))
        {
            writer.Error("--file <PATH> ist Pflicht (oder `--file -` für stdin).");
            return ExitCodes.Error;
        }
        if (string.IsNullOrWhiteSpace(settings.Etag))
        {
            writer.Error("--etag <ETAG> ist Pflicht. Hol ihn dir mit `np settings get <section> --etag-only`.");
            return ExitCodes.Error;
        }

        string json;
        try
        {
            json = settings.File == "-"
                ? await Console.In.ReadToEndAsync(ct)
                : await File.ReadAllTextAsync(settings.File, ct);
        }
        catch (IOException ex)
        {
            writer.Error($"Datei konnte nicht gelesen werden: {ex.Message}");
            return ExitCodes.Error;
        }

        JsonDocument payloadDoc;
        try { payloadDoc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            writer.Error($"Payload ist kein gültiges JSON: {ex.Message}");
            return ExitCodes.Error;
        }

        using (payloadDoc)
        {
            var api = ClientFactory.Create(session);
            using var saved = await api.PutSettingsSectionAsync(settings.Section, settings.Etag, payloadDoc.RootElement, ct);
            JsonShapedPrint.Write(writer, saved);

            // Surface the new ETag on stderr so chained automation can capture it without
            // re-parsing the JSON body.
            if (saved.RootElement.TryGetProperty("etag", out var etag))
                writer.Info($"[grey]New ETag:[/] {Markup.Escape(etag.GetString() ?? "")}");
            return ExitCodes.Success;
        }
    }
}

// ---- np settings test smtp|llm ---------------------------------------------

public sealed class SettingsTestSmtpSettings : GlobalSettings
{
    [CommandOption("--file <PATH>")]
    [Description("Path to a JSON file holding the test payload `{ \"settings\": <SmtpSettingsDto>, \"toAddress\": \"<email>\" }`. Use `-` for stdin.")]
    public string? File { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SettingsTestSmtpCommand : BaseCommand<SettingsTestSmtpSettings>
{
    public SettingsTestSmtpCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SettingsTestSmtpSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.File))
        {
            writer.Error("--file <PATH> ist Pflicht (Body-Shape: { settings: SmtpSettingsDto, toAddress?: string }).");
            return ExitCodes.Error;
        }

        var (req, error) = await ReadTestProbeBodyAsync<SmtpTestProbeRequest>(settings.File, ct);
        if (req is null) { writer.Error(error!); return ExitCodes.Error; }

        var api = ClientFactory.Create(session);
        var result = await api.TestSmtpAsync(req, ct);
        return WriteResult(writer, result);
    }

    internal static async Task<(T? Req, string? Error)> ReadTestProbeBodyAsync<T>(string file, CancellationToken ct) where T : class
    {
        string json;
        try
        {
            json = file == "-"
                ? await Console.In.ReadToEndAsync(ct)
                : await File.ReadAllTextAsync(file, ct);
        }
        catch (IOException ex) { return (null, $"Datei konnte nicht gelesen werden: {ex.Message}"); }

        try
        {
            var parsed = JsonSerializer.Deserialize<T>(json, NodePilotApiClient.JsonOptions);
            return parsed is null ? (null, "Payload ist leer.") : (parsed, null);
        }
        catch (JsonException ex) { return (null, $"Payload ist kein gültiges JSON: {ex.Message}"); }
    }

    internal static int WriteResult(OutputWriter writer, SettingsTestProbeResult result)
    {
        writer.WriteData(result, (console, value) =>
        {
            var ok = value.Ok ? "[green]ok[/]" : "[red]failed[/]";
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("Result", ok);
            grid.AddRow("Message", Markup.Escape(value.Message));
            grid.AddRow("Duration", $"{value.DurationMs:F0} ms");
            if (!string.IsNullOrEmpty(value.ErrorKind)) grid.AddRow("ErrorKind", Markup.Escape(value.ErrorKind));
            console.Write(grid);
        });
        return result.Ok ? ExitCodes.Success : ExitCodes.Error;
    }
}

public sealed class SettingsTestLlmSettings : GlobalSettings
{
    [CommandOption("--file <PATH>")]
    [Description("Path to a JSON file holding the test payload `{ \"settings\": <LlmSettingsDto> }`. Use `-` for stdin.")]
    public string? File { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SettingsTestLlmCommand : BaseCommand<SettingsTestLlmSettings>
{
    public SettingsTestLlmCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SettingsTestLlmSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.File))
        {
            writer.Error("--file <PATH> ist Pflicht (Body-Shape: { settings: LlmSettingsDto }).");
            return ExitCodes.Error;
        }

        var (req, error) = await SettingsTestSmtpCommand.ReadTestProbeBodyAsync<LlmTestProbeRequest>(settings.File, ct);
        if (req is null) { writer.Error(error!); return ExitCodes.Error; }

        var api = ClientFactory.Create(session);
        var result = await api.TestLlmAsync(req, ct);
        return SettingsTestSmtpCommand.WriteResult(writer, result);
    }
}
