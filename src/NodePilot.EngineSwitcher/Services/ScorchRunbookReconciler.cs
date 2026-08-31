using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using NodePilot.EngineSwitcher.Configuration;
using NodePilot.EngineSwitcher.Models;

namespace NodePilot.EngineSwitcher.Services;

internal sealed record ScorchRunbook(Guid Id, string Name);
internal sealed record ScorchRunbookServer(string Name);
internal sealed record ScorchJob(Guid Id, Guid? RunbookId, string Status);

internal interface IScorchApiClient : IDisposable
{
    Task<IReadOnlyList<ScorchRunbook>> ListRunbooksAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ScorchRunbookServer>> ListRunbookServersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ScorchJob>> ListJobsAsync(CancellationToken cancellationToken);
    Task StartRunbookAsync(
        Guid runbookId,
        IReadOnlyList<string> runbookServers,
        CancellationToken cancellationToken);
    Task StopJobAsync(Guid jobId, CancellationToken cancellationToken);
}

internal interface IScorchApiClientFactory
{
    IScorchApiClient Create(ScorchWorkloadConfiguration configuration);
}

internal sealed class ScorchApiClientFactory : IScorchApiClientFactory
{
    public IScorchApiClient Create(ScorchWorkloadConfiguration configuration) => new ScorchApiClient(configuration);
}

internal sealed class ScorchApiClient : IScorchApiClient, IDisposable
{
    private readonly ScorchWorkloadConfiguration _configuration;
    private readonly HttpClient _http;

    public ScorchApiClient(ScorchWorkloadConfiguration configuration)
        : this(configuration, new HttpClientHandler { UseDefaultCredentials = true, PreAuthenticate = true })
    {
    }

    internal ScorchApiClient(ScorchWorkloadConfiguration configuration, HttpMessageHandler handler)
    {
        _configuration = configuration;
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(configuration.ApiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds),
        };
    }

    public Task<IReadOnlyList<ScorchRunbook>> ListRunbooksAsync(CancellationToken cancellationToken) =>
        GetCollectionAsync<ScorchRunbook>(_configuration.RunbooksPath, cancellationToken);

    public Task<IReadOnlyList<ScorchRunbookServer>> ListRunbookServersAsync(CancellationToken cancellationToken) =>
        GetCollectionAsync<ScorchRunbookServer>(_configuration.RunbookServersPath, cancellationToken);

    public Task<IReadOnlyList<ScorchJob>> ListJobsAsync(CancellationToken cancellationToken) =>
        GetCollectionAsync<ScorchJob>(_configuration.ActiveJobsPath, cancellationToken);

    public async Task StartRunbookAsync(
        Guid runbookId,
        IReadOnlyList<string> runbookServers,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            RunbookId = runbookId,
            RunbookServers = runbookServers,
            Parameters = Array.Empty<object>(),
            CreatedBy = (string?)null,
        };
        using var response = await _http.PostAsJsonAsync(_configuration.JobsPath, payload, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "start SCOrch runbook", cancellationToken).ConfigureAwait(false);
    }

    public async Task StopJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var path = _configuration.StopJobPathTemplate.Replace("{id}", jobId.ToString(), StringComparison.Ordinal);
        using var request = new HttpRequestMessage(new HttpMethod(_configuration.StopJobMethod), path)
        {
            Content = JsonContent.Create(new { }),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "stop SCOrch job", cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();

    private async Task<IReadOnlyList<T>> GetCollectionAsync<T>(string path, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? next = path;
        while (!string.IsNullOrWhiteSpace(next))
        {
            if (!visited.Add(next) || visited.Count > 1000)
                throw new InvalidOperationException($"SCOrch returned an invalid pagination chain for {typeof(T).Name}.");
            using var response = await _http.GetAsync(next, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, $"read {typeof(T).Name} collection", cancellationToken).ConfigureAwait(false);
            var requestUri = response.RequestMessage?.RequestUri?.ToString() ?? next;
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = Parse<T>(payload, requestUri);
            var root = document.RootElement;
            next = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProperty(root, "@odata.nextLink", out var nextLink)
                    && nextLink.ValueKind == JsonValueKind.String)
                    next = nextLink.GetString();
                if (TryGetProperty(root, "value", out var value)) root = value;
            }
            if (root.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(
                    $"SCOrch returned an unexpected {typeof(T).Name} response from {requestUri}. Body: {Preview(payload)}");
            items.AddRange(Deserialize<T>(root, requestUri));
        }
        return items;
    }

    /// <summary>
    /// Parses a SCOrch response and names the request and the body when it is not JSON. A web
    /// service that faults while writing sends a truncated payload under HTTP 200, so the parser
    /// message alone does not say which call broke.
    /// </summary>
    private static JsonDocument Parse<T>(string payload, string requestUri)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"SCOrch returned a malformed {typeof(T).Name} response from {requestUri}: {exception.Message} "
                + $"Body: {Preview(payload)}",
                exception);
        }
    }

    private static List<T> Deserialize<T>(JsonElement items, string requestUri)
    {
        var raw = items.GetRawText();
        try
        {
            return JsonSerializer.Deserialize<List<T>>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"SCOrch returned an unreadable {typeof(T).Name} entry from {requestUri}: {exception.Message} "
                + $"Body: {Preview(raw)}",
                exception);
        }
    }

    private static string Preview(string payload) =>
        payload.Length > 500 ? payload[..500] : payload;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"Could not {operation}: HTTP {(int)response.StatusCode} {response.StatusCode}. {Preview(detail)}".Trim());
    }
}

internal sealed class ScorchRunbookReconciler
{
    private static readonly HashSet<string> ManagedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pending", "Queued", "Running", "InProgress",
    };

    private static readonly HashSet<string> RunningStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Running", "InProgress",
    };

    private readonly IScorchApiClientFactory _clientFactory;
    private readonly IActivityLogger _logger;

    public ScorchRunbookReconciler(IScorchApiClientFactory clientFactory, IActivityLogger logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public async Task ReconcileAsync(
        ScorchWorkloadConfiguration configuration,
        IReadOnlyList<string> allowList,
        IProgress<SwitchProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new SwitchProgress(SwitchProgressKind.ReconcilingWorkloads, "SCOrch runbooks"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(configuration.ReconciliationTimeoutSeconds));
        using var client = _clientFactory.Create(configuration);
        var runbooks = await client.ListRunbooksAsync(deadline.Token).ConfigureAwait(false);
        var allowed = NodePilotWorkflowReconciler.ResolveAllowList(
            runbooks.Select(runbook => new NodePilotWorkflow(runbook.Id, runbook.Name, false)).ToArray(),
            allowList,
            "SCOrch runbook");
        var allowedIds = allowed.Select(runbook => runbook.Id).ToHashSet();
        var runbookServers = (await client.ListRunbookServersAsync(deadline.Token).ConfigureAwait(false))
            .Select(server => server.Name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (runbookServers.Length == 0)
            throw new InvalidOperationException("SCOrch has no available runbook server; runbooks cannot be started.");

        var jobs = (await client.ListJobsAsync(deadline.Token).ConfigureAwait(false))
            .Where(job => ManagedStates.Contains(job.Status))
            .ToArray();
        if (jobs.Any(job => job.RunbookId is null))
            throw new InvalidOperationException("SCOrch returned an active job without a runbook id; strict reconciliation was aborted.");
        foreach (var job in jobs.Where(job => !allowedIds.Contains(job.RunbookId!.Value)))
        {
            await client.StopJobAsync(job.Id, deadline.Token).ConfigureAwait(false);
            _logger.Info($"Unlisted SCOrch job stopped: {job.Id} (runbook {job.RunbookId}).");
        }

        var runningAllowedIds = jobs
            .Where(job => allowedIds.Contains(job.RunbookId!.Value) && RunningStates.Contains(job.Status))
            .Select(job => job.RunbookId!.Value)
            .ToHashSet();
        foreach (var runbook in allowed.Where(runbook => !runningAllowedIds.Contains(runbook.Id)))
        {
            foreach (var pendingJob in jobs.Where(job =>
                         job.RunbookId == runbook.Id && !RunningStates.Contains(job.Status)))
            {
                await client.StopJobAsync(pendingJob.Id, deadline.Token).ConfigureAwait(false);
                _logger.Info($"Stale SCOrch job stopped before restart: {pendingJob.Id} (runbook {runbook.Id}).");
            }
            await client.StartRunbookAsync(runbook.Id, runbookServers, deadline.Token).ConfigureAwait(false);
            _logger.Info(
                $"Allowed SCOrch runbook started: {runbook.Name} ({runbook.Id}) on {string.Join(", ", runbookServers)}.");
        }

        ScorchJob[] unexpected;
        Guid[] missing;
        do
        {
            var verifiedJobs = (await client.ListJobsAsync(deadline.Token).ConfigureAwait(false))
                .Where(job => ManagedStates.Contains(job.Status))
                .ToArray();
            if (verifiedJobs.Any(job => job.RunbookId is null))
                throw new InvalidOperationException("SCOrch verification returned an active job without a runbook id.");
            unexpected = verifiedJobs.Where(job => !allowedIds.Contains(job.RunbookId!.Value)).ToArray();
            missing = allowedIds.Except(verifiedJobs
                    .Where(job => RunningStates.Contains(job.Status))
                    .Select(job => job.RunbookId!.Value))
                .ToArray();
            if (unexpected.Length == 0 && missing.Length == 0) break;
            await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token).ConfigureAwait(false);
        } while (true);

        _logger.Info($"SCOrch runbook allowlist verified: {allowedIds.Count} runbooks active, no unlisted jobs running.");
    }

    /// <param name="onMutationStarted">
    /// Called before the first job is stopped. Reading the job list changes nothing, so a failure
    /// up to that point must not trigger the caller's fail-closed cleanup.
    /// </param>
    public async Task StopAllManagedJobsAsync(
        ScorchWorkloadConfiguration configuration,
        IProgress<SwitchProgress>? progress,
        Action onMutationStarted,
        CancellationToken cancellationToken)
    {
        progress?.Report(new SwitchProgress(SwitchProgressKind.ReconcilingWorkloads, "SCOrch source jobs"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(configuration.ReconciliationTimeoutSeconds));
        using var client = _clientFactory.Create(configuration);
        var jobs = (await client.ListJobsAsync(deadline.Token).ConfigureAwait(false))
            .Where(job => ManagedStates.Contains(job.Status))
            .ToArray();
        if (jobs.Length > 0) onMutationStarted();
        foreach (var job in jobs)
        {
            await client.StopJobAsync(job.Id, deadline.Token).ConfigureAwait(false);
            _logger.Info($"SCOrch source job stopped: {job.Id} (runbook {job.RunbookId}).");
        }

        while (true)
        {
            var remaining = (await client.ListJobsAsync(deadline.Token).ConfigureAwait(false))
                .Where(job => ManagedStates.Contains(job.Status))
                .ToArray();
            if (remaining.Length == 0) break;
            await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token).ConfigureAwait(false);
        }

        _logger.Info($"SCOrch source jobs verified stopped: {jobs.Length} jobs deactivated.");
    }
}
