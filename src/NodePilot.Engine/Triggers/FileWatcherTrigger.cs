using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Triggers;

/// <summary>
/// File watcher trigger — monitors a directory for file changes.
/// When executed as a node (manual run), it checks the directory once
/// and reports any matching files. The background listener uses FileSystemWatcher.
/// </summary>
public class FileWatcherTrigger : IActivityExecutor
{
    private static readonly IConfiguration EmptyPathGuardConfiguration =
        new ConfigurationBuilder().Build();

    private readonly IConfiguration? _config;

    public FileWatcherTrigger(IConfiguration? config = null)
    {
        _config = config;
    }

    public string ActivityType => "fileWatcherTrigger";

    public async Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        var directory = config.TryGetProperty("directory", out var d) ? d.GetString() : null;
        var filter = config.TryGetProperty("filter", out var f) ? f.GetString() : "*.*";
        var watchType = config.TryGetProperty("watchType", out var w) ? w.GetString() : "created";
        var includeSubdirs = config.TryGetProperty("includeSubdirectories", out var s) && s.GetBoolean();

        if (string.IsNullOrWhiteSpace(directory))
        {
            return new ActivityResult { Success = false, ErrorOutput = "No directory specified" };
        }

        // If the orchestrator fired this trigger, event data is in context.Variables as manual.*
        var outputParams = TriggerVariables.ExtractManualParams(context.Variables);

        if (outputParams.TryGetValue("filePath", out var triggeredFile))
        {
            var action = outputParams.GetValueOrDefault("fileAction", "changed");
            return new ActivityResult
            {
                Success = true,
                Output = $"File {action}: {triggeredFile}",
                OutputParameters = outputParams,
            };
        }

        // Manual execution: scan directory once. Apply the same allow-list +
        // hard-block check as the scheduler-side source so a workflow author can't
        // enumerate C:\Windows via a manual "Run Step" while the live trigger
        // would have refused to start there.
        try
        {
            FileWatcherPathGuard.Validate(_config ?? EmptyPathGuardConfiguration, directory);
        }
        catch (InvalidOperationException ex)
        {
            return new ActivityResult { Success = false, ErrorOutput = ex.Message };
        }

        return await Task.Run(() =>
        {
            if (!Directory.Exists(directory))
            {
                return new ActivityResult { Success = false, ErrorOutput = $"Directory not found: {directory}" };
            }

            IReadOnlyList<string> files;
            try
            {
                files = FileWatcherPathGuard.EnumerateFilesReparseFree(
                    directory,
                    filter ?? "*.*",
                    includeSubdirs);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return new ActivityResult
                {
                    Success = false,
                    ErrorOutput = $"FileWatcherTrigger: secure directory scan failed: {ex.Message}",
                };
            }

            return new ActivityResult
            {
                Success = true,
                Output = $"Directory: {directory}\nFilter: {filter}\nWatch type: {watchType}\nFiles found: {files.Count}\n" +
                         string.Join("\n", files.Take(20).Select(Path.GetFileName))
            };
        }, ct);
    }
}
