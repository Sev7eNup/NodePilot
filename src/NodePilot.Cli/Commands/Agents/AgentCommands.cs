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

namespace NodePilot.Cli.Commands.Agents;

public class AgentIdSettings : GlobalSettings
{
    [CommandArgument(0, "<ID>")] public Guid Id { get; set; }
}
public sealed class AgentEventsSettings : AgentIdSettings
{
    [CommandOption("--after <SEQUENCE>"), DefaultValue(0L)] public long After { get; set; }
    [CommandOption("--page-size <COUNT>"), DefaultValue(200)] public int PageSize { get; set; } = 200;
}
public sealed class AgentFileSettings : AgentIdSettings
{
    [CommandOption("-f|--file <PATH>")] public string File { get; set; } = "";
}
public sealed class AgentSkillImportSettings : GlobalSettings
{
    [CommandArgument(0, "<ZIP>")] public string File { get; set; } = "";
    [CommandOption("--version <VERSION>"), DefaultValue("1.0.0")] public string Version { get; set; } = "1.0.0";
}
public sealed class AgentSkillEnabledSettings : AgentIdSettings
{
    [CommandArgument(1, "<ENABLED>")] public bool Enabled { get; set; }
}

internal static class AgentCommandInput
{
    public static async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 1_000_000) throw new ArgumentException("Configuration exceeds 1 MB.");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct), NodePilotApiClient.JsonOptions)
            ?? throw new ArgumentException("Configuration is required.");
    }
    public static async Task<byte[]> ReadPackageAsync(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 10_000_000) throw new ArgumentException("Skill package exceeds 10 MB.");
        return await File.ReadAllBytesAsync(path, ct);
    }
}

[SupportedOSPlatform("windows")]
public sealed class AgentRunsCommand(SessionResolver sessions, ApiClientFactory factory) : BaseCommand<AgentIdSettings>(sessions, factory)
{
    protected override async Task<int> RunAsync(CommandContext _, AgentIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var result = await api.ListAgentRunsAsync(settings.Id, ct);
        writer.WriteData(result, (console, value) => console.Write(new Text(JsonSerializer.Serialize(value, new JsonSerializerOptions(NodePilotApiClient.JsonOptions) { WriteIndented = true }))));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class AgentEventsCommand(SessionResolver sessions, ApiClientFactory factory) : BaseCommand<AgentEventsSettings>(sessions, factory)
{
    protected override async Task<int> RunAsync(CommandContext _, AgentEventsSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var result = await api.GetAgentEventsAsync(settings.Id, settings.After, settings.PageSize, ct);
        writer.WriteData(result, (console, value) => console.Write(new Text(JsonSerializer.Serialize(value, new JsonSerializerOptions(NodePilotApiClient.JsonOptions) { WriteIndented = true }))));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class AgentMcpListCommand(SessionResolver sessions, ApiClientFactory factory) : BaseCommand<GlobalSettings>(sessions, factory)
{
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var result = await api.ListAgentMcpServersAsync(ct);
        writer.WriteData(result, (console, value) => console.Write(new Text(JsonSerializer.Serialize(value, new JsonSerializerOptions(NodePilotApiClient.JsonOptions) { WriteIndented = true }))));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class AgentMcpToolsCommand(SessionResolver sessions, ApiClientFactory factory) : BaseCommand<AgentIdSettings>(sessions, factory)
{
    protected override async Task<int> RunAsync(CommandContext _, AgentIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var result = await api.GetAgentMcpToolsAsync(settings.Id, ct);
        writer.WriteData(result, (console, value) => console.Write(new Text(JsonSerializer.Serialize(value, new JsonSerializerOptions(NodePilotApiClient.JsonOptions) { WriteIndented = true }))));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class AgentMcpSaveCommand(SessionResolver sessions, ApiClientFactory factory) : BaseCommand<AgentFileSettings>(sessions, factory)
{
    protected override async Task<int> RunAsync(CommandContext _, AgentFileSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var result = await api.SaveAgentMcpServerAsync(settings.Id, await AgentCommandInput.ReadAsync<SaveAgentMcpServerRequest>(settings.File, ct), ct);
        writer.WriteData(result, (console, value) => console.Write(new Text(JsonSerializer.Serialize(value, new JsonSerializerOptions(NodePilotApiClient.JsonOptions) { WriteIndented = true }))));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class AgentMcpDeleteCommand(SessionResolver sessions, ApiClientFactory factory) : BaseCommand<AgentIdSettings>(sessions, factory)
{
    protected override async Task<int> RunAsync(CommandContext _, AgentIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!Console.IsInputRedirected && !await AnsiConsole.ConfirmAsync($"Delete agent registration {settings.Id}?", defaultValue: false))
            return ExitCodes.Success;
        var api = ClientFactory.Create(session);
        await api.DeleteAgentMcpServerAsync(settings.Id, ct);
        writer.Success("Agent registration updated.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class AgentSkillListCommand(SessionResolver sessions, ApiClientFactory factory) : BaseCommand<GlobalSettings>(sessions, factory)
{
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var result = await api.ListAgentSkillsAsync(ct);
        writer.WriteData(result, (console, value) => console.Write(new Text(JsonSerializer.Serialize(value, new JsonSerializerOptions(NodePilotApiClient.JsonOptions) { WriteIndented = true }))));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class AgentSkillImportCommand(SessionResolver sessions, ApiClientFactory factory) : BaseCommand<AgentSkillImportSettings>(sessions, factory)
{
    protected override async Task<int> RunAsync(CommandContext _, AgentSkillImportSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var result = await api.ImportAgentSkillAsync(new ImportAgentSkillRequest { Version = settings.Version, Package = await AgentCommandInput.ReadPackageAsync(settings.File, ct) }, ct);
        writer.WriteData(result, (console, value) => console.Write(new Text(JsonSerializer.Serialize(value, new JsonSerializerOptions(NodePilotApiClient.JsonOptions) { WriteIndented = true }))));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class AgentSkillEnableCommand(SessionResolver sessions, ApiClientFactory factory) : BaseCommand<AgentSkillEnabledSettings>(sessions, factory)
{
    protected override async Task<int> RunAsync(CommandContext _, AgentSkillEnabledSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        await api.SetAgentSkillEnabledAsync(settings.Id, new SetAgentSkillEnabledRequest(settings.Enabled), ct);
        writer.Success("Agent registration updated.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class AgentSkillDeleteCommand(SessionResolver sessions, ApiClientFactory factory) : BaseCommand<AgentIdSettings>(sessions, factory)
{
    protected override async Task<int> RunAsync(CommandContext _, AgentIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!Console.IsInputRedirected && !await AnsiConsole.ConfirmAsync($"Delete agent registration {settings.Id}?", defaultValue: false))
            return ExitCodes.Success;
        var api = ClientFactory.Create(session);
        await api.DeleteAgentSkillAsync(settings.Id, ct);
        writer.Success("Agent registration updated.");
        return ExitCodes.Success;
    }
}
