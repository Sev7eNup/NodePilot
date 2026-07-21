using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using NodePilot.Api.Dtos;
using NodePilot.Telemetry;

namespace NodePilot.Api.Services.Observability;

/// <summary>
/// Executes the exact PromQL targets from the ten provisioned Grafana dashboards.
/// Keeping the dashboard JSON embedded makes Grafana and the native UI share one catalogue.
/// </summary>
internal static class MetricsDashboardCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["mission-control"] = "00-mission-control.json",
        ["workflows"] = "10-workflows.json",
        ["activities"] = "20-activities.json",
        ["winrm"] = "30-winrm.json",
        ["triggers"] = "40-triggers.json",
        ["api"] = "50-api.json",
        ["runtime"] = "60-runtime.json",
        ["security"] = "70-security.json",
        ["ai"] = "80-ai.json",
        ["database"] = "90-database.json",
    };

    public static bool Exists(string key) => Files.ContainsKey(key);

    internal static int PanelCount(string key)
    {
        using var document = Load(key);
        return document.RootElement.GetProperty("panels").EnumerateArray().Count(IsMetricPanel);
    }

    public static string Title(string key)
    {
        using var document = Load(key);
        return document.RootElement.TryGetProperty("title", out var title) ? title.GetString() ?? key : key;
    }

    public static async Task<MetricsDashboardResponse> ExecuteAsync(
        string key, int hours, PrometheusClient prometheus, ILogger logger, CancellationToken cancellationToken)
    {
        using var document = Load(key);
        var root = document.RootElement;
        var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? key : key;
        var panels = root.GetProperty("panels").EnumerateArray()
            .Where(IsMetricPanel)
            .OrderBy(p => IntAt(p, "gridPos", "y"))
            .ThenBy(p => IntAt(p, "gridPos", "x"))
            .Select(p => p.Clone())
            .ToArray();

        var now = DateTimeOffset.UtcNow;
        var start = now.AddHours(-hours).ToUnixTimeSeconds();
        var end = now.ToUnixTimeSeconds();
        var step = hours <= 24 ? "60" : hours <= 168 ? "300" : "1800";
        var widgets = await Task.WhenAll(panels.Select(panel => ExecutePanelAsync(
            panel, hours, start, end, step, prometheus, logger, cancellationToken)));

        return new MetricsDashboardResponse(true, key, title, [], [], [], widgets.ToList());
    }

    private static bool IsMetricPanel(JsonElement panel)
    {
        if (!panel.TryGetProperty("type", out var type)) return false;
        return type.GetString() is "stat" or "timeseries" or "bargauge" or "piechart" or "table" or "heatmap";
    }

    private static async Task<MetricsWidget> ExecutePanelAsync(
        JsonElement panel, int hours, long start, long end, string step,
        PrometheusClient prometheus, ILogger logger, CancellationToken cancellationToken)
    {
        var id = panel.TryGetProperty("id", out var idElement) ? idElement.GetInt32() : 0;
        var title = StringAt(panel, "title") ?? $"Panel {id}";
        var description = StringAt(panel, "description");
        var type = StringAt(panel, "type") ?? "timeseries";
        var unit = StringAt(panel, "fieldConfig", "defaults", "unit") ?? "short";
        var grid = new MetricsGridPosition(
            IntAt(panel, "gridPos", "x"), IntAt(panel, "gridPos", "y"),
            IntAt(panel, "gridPos", "w", 24), IntAt(panel, "gridPos", "h", 8));

        if (!panel.TryGetProperty("targets", out var targets))
            return new MetricsWidget(id, title, description, type, unit, grid, [], null);

        var queryTasks = targets.EnumerateArray()
            .Where(target => target.TryGetProperty("expr", out var expr) && !string.IsNullOrWhiteSpace(expr.GetString())
                && (!target.TryGetProperty("hide", out var hidden) || !hidden.GetBoolean()))
            .Select(target => ExecuteTargetAsync(target.Clone(), type, hours, start, end, step, prometheus, cancellationToken))
            .ToArray();

        try
        {
            var results = await Task.WhenAll(queryTasks);
            var data = results.SelectMany(result => result.Data).ToList();
            var errors = results.Where(result => result.Error is not null).Select(result => result.Error).ToArray();
            return new MetricsWidget(id, title, description, type, unit, grid, data,
                errors.Length == 0 ? null : string.Join("; ", errors));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Grafana-compatible metrics panel {PanelId} ({Title}) failed", id, title);
            return new MetricsWidget(id, title, description, type, unit, grid, [], exception.Message);
        }
    }

    private static async Task<TargetResult> ExecuteTargetAsync(
        JsonElement target, string panelType, int hours, long start, long end, string step,
        PrometheusClient prometheus, CancellationToken cancellationToken)
    {
        var query = ExpandMacros(target.GetProperty("expr").GetString()!, hours, step);
        var legend = StringAt(target, "legendFormat");
        var instant = panelType is "stat" or "bargauge" or "piechart" or "table"
            || (target.TryGetProperty("instant", out var instantElement) && instantElement.GetBoolean());
        var response = instant
            ? await prometheus.InstantAsync(query, null, cancellationToken)
            : await prometheus.RangeAsync(query, start, end, step, cancellationToken);
        return response.IsSuccess
            ? new TargetResult(ParseResponse(response.Body, legend), null)
            : new TargetResult([], $"Prometheus HTTP {response.StatusCode}");
    }

    private static string ExpandMacros(string query, int hours, string step)
    {
        var range = hours switch { 1 => "1h", 24 => "24h", 168 => "7d", 720 => "30d", _ => "24h" };
        return query
            .Replace("$__rate_interval", hours <= 24 ? "5m" : "15m", StringComparison.Ordinal)
            .Replace("$__range", range, StringComparison.Ordinal)
            .Replace("$__interval", $"{step}s", StringComparison.Ordinal)
            .Replace("$workflow_name", ".*", StringComparison.Ordinal)
            .Replace("$activity_type", ".*", StringComparison.Ordinal)
            .Replace("$http_route", ".*", StringComparison.Ordinal);
    }

    private static List<MetricsDataSeries> ParseResponse(string body, string? legend)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.GetProperty("data");
            var resultType = data.GetProperty("resultType").GetString();
            var result = data.GetProperty("result");
            if (resultType == "scalar")
                return [new MetricsDataSeries(legend ?? "Value", [], [new MetricsPoint((long)result[0].GetDouble(), Number(result[1].GetString()))])];

            return result.EnumerateArray().Select(item =>
            {
                var labels = ReadLabels(item);
                var label = FormatLegend(legend, labels);
                var points = resultType == "matrix"
                    ? item.GetProperty("values").EnumerateArray().Select(value => new MetricsPoint((long)value[0].GetDouble(), Number(value[1].GetString()))).ToList()
                    : new List<MetricsPoint> { new((long)item.GetProperty("value")[0].GetDouble(), Number(item.GetProperty("value")[1].GetString())) };
                return new MetricsDataSeries(label, labels, points);
            }).ToList();
        }
        catch { return []; }
    }

    private static Dictionary<string, string> ReadLabels(JsonElement item)
    {
        if (!item.TryGetProperty("metric", out var metric)) return [];
        return metric.EnumerateObject()
            .Where(property => property.Name is not "__name__" and not "instance" and not "job")
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "", StringComparer.Ordinal);
    }

    private static string FormatLegend(string? template, Dictionary<string, string> labels)
    {
        if (!string.IsNullOrWhiteSpace(template))
        {
            var formatted = Regex.Replace(template, @"\{\{\s*([^}\s]+)\s*\}\}", match =>
                labels.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Groups[1].Value,
                RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (!string.IsNullOrWhiteSpace(formatted)) return formatted;
        }
        return labels.Count == 0 ? "Value" : string.Join(" · ", labels.Values);
    }

    private static double? Number(string? raw) => PrometheusResponseParser.TryParseFiniteNumber(raw);

    private static int IntAt(JsonElement root, string first, string second, int fallback = 0)
        => root.TryGetProperty(first, out var child) && child.TryGetProperty(second, out var value) && value.TryGetInt32(out var number) ? number : fallback;

    private static string? StringAt(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
            if (!current.TryGetProperty(segment, out current)) return null;
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static JsonDocument Load(string key)
    {
        if (!Files.TryGetValue(key, out var file)) throw new KeyNotFoundException(key);
        var assembly = typeof(MetricsDashboardCatalog).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith($"MetricsDashboards.{file}", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Embedded dashboard '{file}' not found.");
        return JsonDocument.Parse(stream);
    }

    private sealed record TargetResult(List<MetricsDataSeries> Data, string? Error);
}
