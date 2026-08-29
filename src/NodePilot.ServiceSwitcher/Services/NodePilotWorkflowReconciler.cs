using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NodePilot.ServiceSwitcher.Configuration;
using NodePilot.ServiceSwitcher.Models;

namespace NodePilot.ServiceSwitcher.Services;

internal sealed record NodePilotWorkflow(Guid Id, string Name, bool IsEnabled);
internal sealed record NodePilotOperationsGraph(IReadOnlyList<NodePilotOperationsNode> Nodes);
internal sealed record NodePilotOperationsNode(Guid WorkflowId, int RunningCount);
internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

internal interface ICommandRunner
{
    Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Could not start '{executable}'.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Command '{Path.GetFileName(executable)}' exceeded {timeout.TotalSeconds:0} seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new CommandResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }
}

internal sealed class NodePilotWorkflowReconciler
{
    private readonly ICommandRunner _runner;
    private readonly IActivityLogger _logger;

    public NodePilotWorkflowReconciler(ICommandRunner runner, IActivityLogger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task ReconcileAsync(
        NodePilotWorkloadConfiguration configuration,
        IReadOnlyList<string> allowList,
        IProgress<SwitchProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new SwitchProgress(SwitchProgressKind.ReconcilingWorkloads, "NodePilot workflows"));
        var workflows = await ListAsync(configuration, cancellationToken).ConfigureAwait(false);
        if (workflows.Count >= 500)
            throw new InvalidOperationException("NodePilot returned 500 workflows. Strict reconciliation is unsafe because the API result may be truncated.");

        var allowed = ResolveAllowList(workflows, allowList, "NodePilot workflow");
        var allowedIds = allowed.Select(workflow => workflow.Id).ToHashSet();
        var unlisted = workflows.Where(workflow => !allowedIds.Contains(workflow.Id)).ToArray();

        foreach (var workflow in unlisted)
        {
            if (workflow.IsEnabled)
            {
                await RunWorkflowCommandAsync(configuration, "disable", workflow.Id, cancellationToken).ConfigureAwait(false);
                _logger.Info($"Unlisted workflow disabled: {workflow.Name} ({workflow.Id}).");
            }
        }

        await CancelAndVerifyActiveUnlistedAsync(configuration, workflows, unlisted, cancellationToken)
            .ConfigureAwait(false);

        foreach (var workflow in allowed.Where(workflow => !workflow.IsEnabled))
        {
            await RunWorkflowCommandAsync(configuration, "enable", workflow.Id, cancellationToken).ConfigureAwait(false);
            _logger.Info($"Allowed workflow permanently enabled: {workflow.Name} ({workflow.Id}).");
        }

        var verified = await ListAsync(configuration, cancellationToken).ConfigureAwait(false);
        var actualEnabled = verified.Where(workflow => workflow.IsEnabled).Select(workflow => workflow.Id).ToHashSet();
        if (!actualEnabled.SetEquals(allowedIds))
            throw new InvalidOperationException("NodePilot workflow verification failed: the enabled set does not exactly match the allowlist.");
        _logger.Info($"NodePilot workflow allowlist verified: {allowedIds.Count} enabled, all others disabled.");
    }

    private async Task CancelAndVerifyActiveUnlistedAsync(
        NodePilotWorkloadConfiguration configuration,
        IReadOnlyList<NodePilotWorkflow> workflows,
        IReadOnlyList<NodePilotWorkflow> unlisted,
        CancellationToken cancellationToken)
    {
        var expectedIds = workflows.Select(workflow => workflow.Id).ToHashSet();
        var unlistedById = unlisted.ToDictionary(workflow => workflow.Id);
        var cancellationRequested = new HashSet<Guid>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(configuration.CommandTimeoutSeconds);

        while (true)
        {
            var runningCounts = await ListRunningCountsAsync(configuration, expectedIds, cancellationToken)
                .ConfigureAwait(false);
            var activeUnlisted = runningCounts
                .Where(item => item.Value > 0 && unlistedById.ContainsKey(item.Key))
                .Select(item => item.Key)
                .ToArray();
            if (activeUnlisted.Length == 0)
            {
                _logger.Info(cancellationRequested.Count == 0
                    ? "No running executions found for unlisted workflows."
                    : $"Running executions verified stopped for {cancellationRequested.Count} unlisted workflows.");
                return;
            }

            var newlyActive = activeUnlisted.Where(cancellationRequested.Add).ToArray();
            foreach (var workflowId in newlyActive)
            {
                await RunWorkflowCommandAsync(configuration, "cancel-all", workflowId, cancellationToken)
                    .ConfigureAwait(false);
                var workflow = unlistedById[workflowId];
                _logger.Info($"Running executions cancelled for unlisted workflow: {workflow.Name} ({workflow.Id}).");
            }

            if (DateTime.UtcNow >= deadline)
            {
                var names = activeUnlisted.Select(id => unlistedById[id].Name);
                throw new TimeoutException(
                    $"Running executions for unlisted workflows did not stop within {configuration.CommandTimeoutSeconds} seconds: {string.Join(", ", names)}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyDictionary<Guid, int>> ListRunningCountsAsync(
        NodePilotWorkloadConfiguration configuration,
        IReadOnlySet<Guid> expectedWorkflowIds,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
                configuration,
                ["operations", "graph", "-o", "json", "--no-color"],
                cancellationToken)
            .ConfigureAwait(false);
        NodePilotOperationsGraph graph;
        try
        {
            graph = JsonSerializer.Deserialize<NodePilotOperationsGraph>(result.StandardOutput,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("NodePilot CLI returned an empty operations graph.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"NodePilot CLI returned invalid operations JSON: {exception.Message}", exception);
        }

        var counts = graph.Nodes.ToDictionary(node => node.WorkflowId, node => node.RunningCount);
        if (!expectedWorkflowIds.SetEquals(counts.Keys))
            throw new InvalidOperationException("NodePilot operations graph is incomplete; running executions cannot be reconciled safely.");
        return counts;
    }

    internal static IReadOnlyList<NodePilotWorkflow> ResolveAllowList(
        IReadOnlyList<NodePilotWorkflow> workflows,
        IReadOnlyList<string> allowList,
        string label)
    {
        var resolved = new List<NodePilotWorkflow>();
        foreach (var entry in allowList)
        {
            NodePilotWorkflow[] matches;
            if (Guid.TryParse(entry, out var id))
                matches = workflows.Where(workflow => workflow.Id == id).ToArray();
            else
                matches = workflows.Where(workflow => workflow.Name.Equals(entry, StringComparison.OrdinalIgnoreCase)).ToArray();

            if (matches.Length == 0)
                throw new InvalidOperationException($"Allowlisted {label} was not found: '{entry}'.");
            if (matches.Length > 1)
                throw new InvalidOperationException($"Allowlisted {label} name is ambiguous; use its GUID: '{entry}'.");
            if (resolved.All(item => item.Id != matches[0].Id)) resolved.Add(matches[0]);
        }
        return resolved;
    }

    private async Task<IReadOnlyList<NodePilotWorkflow>> ListAsync(
        NodePilotWorkloadConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(configuration, ["workflow", "list", "-o", "json", "--no-color"], cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<List<NodePilotWorkflow>>(result.StandardOutput,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException("NodePilot CLI returned an empty JSON document.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"NodePilot CLI returned invalid workflow JSON: {exception.Message}", exception);
        }
    }

    private Task<CommandResult> RunWorkflowCommandAsync(
        NodePilotWorkloadConfiguration configuration,
        string operation,
        Guid workflowId,
        CancellationToken cancellationToken) =>
        RunAsync(configuration, ["workflow", operation, workflowId.ToString()], cancellationToken);

    private async Task<CommandResult> RunAsync(
        NodePilotWorkloadConfiguration configuration,
        IReadOnlyList<string> commandArguments,
        CancellationToken cancellationToken)
    {
        var arguments = commandArguments.ToList();
        arguments.Add("--profile");
        arguments.Add(configuration.Profile);
        if (!string.IsNullOrWhiteSpace(configuration.ServerUrl))
        {
            arguments.Add("--server");
            arguments.Add(configuration.ServerUrl);
            if (Uri.TryCreate(configuration.ServerUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
                arguments.Add("--allow-insecure");
        }

        var result = await _runner.RunAsync(
            configuration.CliPath,
            arguments,
            TimeSpan.FromSeconds(configuration.CommandTimeoutSeconds),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            throw new InvalidOperationException($"NodePilot CLI failed with exit code {result.ExitCode}: {detail}");
        }
        return result;
    }
}
