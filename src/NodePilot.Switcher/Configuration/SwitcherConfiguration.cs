using System.Text;
using System.Text.Json;
using System.IO;

namespace NodePilot.Switcher.Configuration;

internal sealed record SwitcherConfiguration(
    NodePilotWorkloadConfiguration NodePilot,
    ScorchWorkloadConfiguration SystemCenterOrchestrator);

/// <param name="CliPath">
/// Path to np.exe, resolved relative to the configuration file. Optional: left empty, the loader
/// discovers it from the machine's NodePilot installation. A relative path only points at the
/// install directory when the configuration sits in it, so the shipped template cannot carry one
/// that survives being copied next to the executable or under %ProgramData%.
/// </param>
internal sealed record NodePilotWorkloadConfiguration(
    string WorkflowAllowListPath,
    string CliPath = "",
    string Profile = "default",
    string? ServerUrl = null,
    int CommandTimeoutSeconds = 30);

internal sealed record ScorchWorkloadConfiguration(
    string RunbookAllowListPath,
    string ApiBaseUrl,
    string RunbooksPath = "api/runbooks",
    string RunbookServersPath = "api/runbookServers",
    string JobsPath = "api/jobs",
    string ActiveJobsPath = "api/jobs?$select=Id,RunbookId,Status&$filter=Status eq 'Pending' or Status eq 'Running'",
    string StopJobPathTemplate = "api/jobs/{id}",
    string StopJobMethod = "PATCH",
    int RequestTimeoutSeconds = 30,
    int ReconciliationTimeoutSeconds = 60);

internal sealed class SwitcherConfigurationLoader
{
    private const string FileName = "switcher.json";
    private static readonly HashSet<string> PathProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "workflowAllowListPath",
        "runbookAllowListPath",
        "cliPath",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string[] _arguments;
    private readonly string _programData;
    private readonly string _applicationDirectory;
    private readonly Func<IEnumerable<string>> _cliCandidates;

    public SwitcherConfigurationLoader()
        : this(Environment.GetCommandLineArgs(),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppContext.BaseDirectory)
    {
    }

    internal SwitcherConfigurationLoader(
        string[] arguments,
        string programData,
        string applicationDirectory,
        Func<IEnumerable<string>>? cliCandidates = null)
    {
        _arguments = arguments;
        _programData = programData;
        _applicationDirectory = applicationDirectory;
        _cliCandidates = cliCandidates ?? NodePilotCliLocator.Candidates;
    }

    public SwitcherConfiguration Load()
    {
        var candidates = ConfigurationCandidates();
        var path = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        if (!File.Exists(path))
            throw new InvalidOperationException(
                "Switcher configuration not found. Checked: " + string.Join(", ", candidates));

        SwitcherConfiguration? value;
        try
        {
            value = JsonSerializer.Deserialize<SwitcherConfiguration>(
                RepairPathValues(File.ReadAllText(path)),
                JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException(
                $"Switcher configuration could not be read: {path}: {exception.Message}",
                exception);
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
                CliPath = ResolveCliPath(value.NodePilot.CliPath, directory),
            },
            SystemCenterOrchestrator = value.SystemCenterOrchestrator with
            {
                RunbookAllowListPath = ResolveAllowListPath(value.SystemCenterOrchestrator.RunbookAllowListPath, "SCOrch runbook allowlist"),
            },
        };

        SwitcherConfigurationValidator.ValidateStructure(value);
        return value;
    }

    /// <summary>
    /// The configuration locations in search order. All of them are reported when none exists —
    /// naming only the machine-wide path sends the operator to the one location nothing ever
    /// creates, while the file usually ships next to the executable.
    /// </summary>
    private string[] ConfigurationCandidates()
    {
        for (var i = 0; i < _arguments.Length; i++)
        {
            if (!_arguments[i].Equals("--config", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= _arguments.Length || string.IsNullOrWhiteSpace(_arguments[i + 1]))
                throw new InvalidOperationException("--config requires a file path.");
            return [Path.GetFullPath(_arguments[i + 1])];
        }

        return
        [
            Path.GetFullPath(Path.Combine(_programData, "NodePilot", "Switcher", FileName)),
            Path.GetFullPath(Path.Combine(_applicationDirectory, FileName)),
        ];
    }

    /// <summary>
    /// Configured path wins and stays relative to the configuration file; an empty value falls
    /// back to the machine's NodePilot installation.
    /// </summary>
    private string ResolveCliPath(string configured, string configurationDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured, configurationDirectory);

        var candidates = _cliCandidates().ToArray();
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                "NodePilot CLI not found and no cliPath configured. Checked: "
                + (candidates.Length == 0 ? "(no installation found)" : string.Join(", ", candidates)));
    }

    private static string ResolveAllowListPath(string path, string label)
    {
        path = PromoteUncPrefix(path);
        SwitcherConfigurationValidator.RequireAbsolutePath(path, label);
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Restores the UNC prefix of an allowlist path written with one backslash per separator. Both
    /// allowlist paths must be fully qualified, so a single leading backslash can only mean UNC.
    /// The CLI path is excluded: it may be relative, and "\tools\np\np.exe" is a valid
    /// drive-root-relative path.
    /// </summary>
    private static string PromoteUncPrefix(string path) =>
        path.Length > 1 && path[0] == '\\' && path[1] != '\\' ? '\\' + path : path;

    /// <summary>
    /// Doubles unpaired backslashes inside the three path properties so a hand-edited Windows or UNC
    /// path loads as written. Every other value is copied unchanged, so a stray backslash there stays
    /// a load error instead of being accepted silently.
    /// </summary>
    internal static string RepairPathValues(string json)
    {
        var builder = new StringBuilder(json.Length);
        string? propertyName = null;
        var inValue = false;
        var index = 0;

        while (index < json.Length)
        {
            var character = json[index];
            if (character == '"')
            {
                var repair = inValue && propertyName is not null && PathProperties.Contains(propertyName);
                index = CopyString(json, index, builder, repair, out var content);
                if (!inValue) propertyName = content;
                inValue = false;
                continue;
            }

            if (character == ':')
            {
                inValue = true;
            }
            else if (!char.IsWhiteSpace(character))
            {
                inValue = false;
                propertyName = null;
            }

            builder.Append(character);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Copies one JSON string starting at the opening quote and returns the index after the closing
    /// one. With <paramref name="repairBackslashes"/> every backslash is emitted as a literal one;
    /// a backslash before the closing quote therefore ends the string, because a Windows path cannot
    /// contain a quote.
    /// </summary>
    private static int CopyString(
        string json,
        int start,
        StringBuilder builder,
        bool repairBackslashes,
        out string content)
    {
        var value = new StringBuilder();
        builder.Append('"');
        var index = start + 1;

        while (index < json.Length)
        {
            var character = json[index];
            if (character == '"')
            {
                builder.Append('"');
                content = value.ToString();
                return index + 1;
            }

            if (character == '\\')
            {
                var isPair = index + 1 < json.Length && json[index + 1] == '\\';
                if (repairBackslashes)
                {
                    builder.Append(@"\\");
                    value.Append('\\');
                    index += isPair ? 2 : 1;
                    continue;
                }

                builder.Append(character);
                if (index + 1 < json.Length)
                {
                    builder.Append(json[index + 1]);
                    value.Append(json[index + 1]);
                    index += 2;
                    continue;
                }

                index++;
                continue;
            }

            builder.Append(character);
            value.Append(character);
            index++;
        }

        // Unterminated string: hand the rest to the JSON reader, which reports the position.
        content = value.ToString();
        return index;
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
