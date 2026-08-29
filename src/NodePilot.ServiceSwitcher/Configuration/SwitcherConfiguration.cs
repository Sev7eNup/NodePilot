using System.Text.Json;
using System.IO;

namespace NodePilot.ServiceSwitcher.Configuration;

internal sealed record SwitcherConfiguration(
    NodePilotWorkloadConfiguration NodePilot,
    ScorchWorkloadConfiguration SystemCenterOrchestrator);

internal sealed record NodePilotWorkloadConfiguration(
    string WorkflowAllowListPath,
    string CliPath,
    string Profile = "default",
    string? ServerUrl = null,
    int CommandTimeoutSeconds = 30);

internal sealed record ScorchWorkloadConfiguration(
    string RunbookAllowListPath,
    string ApiBaseUrl,
    string RunbooksPath = "api/runbooks",
    string RunbookServersPath = "api/runbookServers",
    string JobsPath = "api/jobs",
    string ActiveJobsPath = "api/jobs?$filter=Status in ('Pending','Running')",
    string StopJobPathTemplate = "api/jobs/{id}",
    string StopJobMethod = "PATCH",
    int RequestTimeoutSeconds = 30,
    int ReconciliationTimeoutSeconds = 60);

internal sealed class SwitcherConfigurationLoader
{
    private const string FileName = "service-switcher.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string[] _arguments;
    private readonly string _programData;
    private readonly string _applicationDirectory;

    public SwitcherConfigurationLoader()
        : this(Environment.GetCommandLineArgs(),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppContext.BaseDirectory)
    {
    }

    internal SwitcherConfigurationLoader(
        string[] arguments,
        string programData,
        string applicationDirectory)
    {
        _arguments = arguments;
        _programData = programData;
        _applicationDirectory = applicationDirectory;
    }

    public SwitcherConfiguration Load()
    {
        var path = ResolveConfigurationPath();
        if (!File.Exists(path))
            throw new InvalidOperationException($"Switcher configuration not found: {path}");

        SwitcherConfiguration? value;
        try
        {
            value = JsonSerializer.Deserialize<SwitcherConfiguration>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException($"Switcher configuration could not be read: {exception.Message}", exception);
        }

        if (value is null)
            throw new InvalidOperationException("Switcher configuration is empty.");
        if (value.NodePilot is null || value.SystemCenterOrchestrator is null)
            throw new InvalidOperationException("Switcher configuration must contain nodePilot and systemCenterOrchestrator sections.");

        var directory = Path.GetDirectoryName(path)!;
        value = value with
        {
            NodePilot = value.NodePilot with
            {
                WorkflowAllowListPath = ResolveAllowListPath(value.NodePilot.WorkflowAllowListPath, "NodePilot workflow allowlist"),
                CliPath = ResolvePath(value.NodePilot.CliPath, directory, "NodePilot CLI"),
            },
            SystemCenterOrchestrator = value.SystemCenterOrchestrator with
            {
                RunbookAllowListPath = ResolveAllowListPath(value.SystemCenterOrchestrator.RunbookAllowListPath, "SCOrch runbook allowlist"),
            },
        };

        SwitcherConfigurationValidator.ValidateStructure(value);
        return value;
    }

    private string ResolveConfigurationPath()
    {
        for (var i = 0; i < _arguments.Length; i++)
        {
            if (!_arguments[i].Equals("--config", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= _arguments.Length || string.IsNullOrWhiteSpace(_arguments[i + 1]))
                throw new InvalidOperationException("--config requires a file path.");
            return Path.GetFullPath(_arguments[i + 1]);
        }

        var machinePath = Path.Combine(_programData, "NodePilot", "ServiceSwitcher", FileName);
        if (File.Exists(machinePath)) return Path.GetFullPath(machinePath);

        var applicationPath = Path.Combine(_applicationDirectory, FileName);
        return File.Exists(applicationPath) ? Path.GetFullPath(applicationPath) : Path.GetFullPath(machinePath);
    }

    private static string ResolvePath(string path, string baseDirectory, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"{label} path is missing.");
        return Path.GetFullPath(path, baseDirectory);
    }

    private static string ResolveAllowListPath(string path, string label)
    {
        SwitcherConfigurationValidator.RequireAbsolutePath(path, label);
        return Path.GetFullPath(path);
    }

}

internal static class SwitcherConfigurationValidator
{
    public static void ValidateStructure(SwitcherConfiguration value)
    {
        RequireAbsolutePath(value.NodePilot.WorkflowAllowListPath, "NodePilot workflow allowlist");
        RequireAbsolutePath(value.SystemCenterOrchestrator.RunbookAllowListPath, "SCOrch runbook allowlist");
    }

    public static void ValidateNodePilot(NodePilotWorkloadConfiguration value)
    {
        if (!File.Exists(value.CliPath))
            throw new InvalidOperationException($"NodePilot CLI not found: {value.CliPath}");
        if (string.IsNullOrWhiteSpace(value.Profile))
            throw new InvalidOperationException("NodePilot CLI profile is missing.");
        if (value.CommandTimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("NodePilot command timeout must be between 1 and 600 seconds.");
    }

    public static void ValidateScorch(ScorchWorkloadConfiguration value)
    {
        if (!Uri.TryCreate(value.ApiBaseUrl, UriKind.Absolute, out var apiUri)
            || (apiUri.Scheme != Uri.UriSchemeHttps && apiUri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("SCOrch apiBaseUrl must be an absolute HTTP(S) URL.");
        if (apiUri.Scheme == Uri.UriSchemeHttp && !apiUri.IsLoopback)
            throw new InvalidOperationException("SCOrch uses Windows credentials; a remote apiBaseUrl must use HTTPS.");
        if (!value.StopJobPathTemplate.Contains("{id}", StringComparison.Ordinal))
            throw new InvalidOperationException("SCOrch stopJobPathTemplate must contain {id}.");
        if (!new[] { "PATCH", "POST", "DELETE" }.Contains(
                value.StopJobMethod,
                StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("SCOrch stopJobMethod must be PATCH, POST, or DELETE.");
        if (value.RequestTimeoutSeconds is < 1 or > 600
            || value.ReconciliationTimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("SCOrch timeouts must be between 1 and 600 seconds.");
    }

    internal static void RequireAbsolutePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"{label} must be an absolute local or UNC path: {path}");
    }
}
