using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Dtos;
using NodePilot.Telemetry;
using NodePilot.Api.Services.Observability;
using System.Globalization;
using System.Text.Json;

namespace NodePilot.Api.Controllers;

[ApiController]
[Route("api/observability")]
[Authorize]
public class ObservabilityController : ControllerBase
{
    private readonly NodePilotTelemetryOptions _options;
    private readonly PrometheusClient _prom;
    private readonly ILogger<ObservabilityController> _logger;

    public ObservabilityController(
        NodePilotTelemetryOptions options,
        PrometheusClient prom,
        ILogger<ObservabilityController> logger)
    {
        _options = options;
        _prom = prom;
        _logger = logger;
    }

    /// <summary>
    /// Returns UI-facing observability configuration. The SPA calls this at bootstrap
    /// (pre-login) to initialize the OTel Web SDK, so it must remain reachable unauthenticated.
    /// To avoid leaking internal infra URLs to the public, backend URLs (OTLP endpoint,
    /// Tempo/Grafana link templates) and the Prometheus-availability flag are only populated
    /// for authenticated callers. Anonymous callers get a minimal shell with everything disabled.
    /// </summary>
    [HttpGet("config")]
    [AllowAnonymous]
    public ActionResult<ObservabilityConfigResponse> GetConfig()
    {
        var authenticated = User?.Identity?.IsAuthenticated == true;
        if (!authenticated)
        {
            return Ok(new ObservabilityConfigResponse(
                Enabled: false,
                TraceUiUrlTemplate: null,
                TraceBackendName: null,
                PrometheusAvailable: false,
                BrowserOtlpEndpoint: null,
                ServiceName: "nodepilot-ui",
                Environment: null));
        }

        return Ok(new ObservabilityConfigResponse(
            Enabled: _options.Enabled,
            TraceUiUrlTemplate: string.IsNullOrWhiteSpace(_options.TraceUi.UrlTemplate) ? null : _options.TraceUi.UrlTemplate,
            TraceBackendName: _options.TraceUi.BackendName,
            PrometheusAvailable: _prom.IsConfigured,
            BrowserOtlpEndpoint: string.IsNullOrWhiteSpace(_options.Otlp.BrowserEndpoint) ? null : _options.Otlp.BrowserEndpoint,
            ServiceName: "nodepilot-ui",
            Environment: _options.Environment,
            GrafanaBaseUrl: NormalizeHttpUrl(_options.GrafanaBaseUrl)));
    }

    private static string? NormalizeHttpUrl(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        return uri.ToString().TrimEnd('/');
    }

    // Hardening for the Prometheus proxy endpoints. The endpoint is authenticated, but a
    // compromised operator token must not become a blank cheque to query anything on the
    // Prometheus box. MaxQueryLength stops unbounded payloads; the metric-name allow-list
    // (configured via OpenTelemetry:AllowedMetricPrefixes) confines queries to NodePilot's
    // own metric families. When the list is empty the default prefixes are used.
    private const int MaxPromQueryLength = 8 * 1024;

    private static readonly string[] DefaultAllowedMetricPrefixes =
    {
        "nodepilot_",
        "http_server_",
        "process_",
        "dotnet_",
        "up",
    };

    private IActionResult? ValidatePromQl(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { message = "query parameter is required." });
        if (query.Length > MaxPromQueryLength)
            return BadRequest(new { message = $"query exceeds {MaxPromQueryLength} characters." });

        // H8: explicitly reject the __name__ label-selector trick. A query like
        // `{__name__=~".+"}` has zero bare-metric tokens for the identifier regex below, so
        // it trivially passes the prefix allow-list while returning every series in the
        // Prometheus TSDB (including prometheus_tsdb_* internals and any co-tenant data).
        // No legitimate dashboard needs to address metrics by the __name__ label — callers
        // spell the metric name directly. Reject early with a clear message.
        if (System.Text.RegularExpressions.Regex.IsMatch(
                query,
                @"\b__name__\b",
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromMilliseconds(250)))
        {
            return BadRequest(new
            {
                message = "PromQL: the __name__ label selector is not permitted. Address metrics by name (e.g. `nodepilot_executions_started`) instead of selecting by __name__.",
            });
        }

        var configured = _options.AllowedMetricPrefixes;
        var prefixes = (configured is { Length: > 0 } ? configured : DefaultAllowedMetricPrefixes);

        // Require at least one bare metric-name token — a query consisting only of label
        // selectors (no identifier before `{`) is rejected because it would otherwise pass
        // the allow-list with zero checks performed on it.
        var metricMatches = System.Text.RegularExpressions.Regex.Matches(
            query,
            @"(?<![a-zA-Z0-9_.])([a-zA-Z_:][a-zA-Z0-9_:]*)\s*(?=[{\[(]|$|\s|[+\-*/%<>=!,\)])",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(250));

        var reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            // PromQL keywords / aggregation operators / functions — not metric names.
            "sum", "avg", "min", "max", "count", "count_values", "stddev", "stdvar",
            "topk", "bottomk", "quantile", "group",
            "rate", "irate", "increase", "delta", "idelta", "resets", "changes",
            "histogram_quantile", "clamp", "clamp_min", "clamp_max", "abs", "ceil", "floor",
            "round", "exp", "ln", "log2", "log10", "sqrt", "deriv", "predict_linear",
            "sort", "sort_desc", "label_replace", "label_join", "vector", "scalar",
            "time", "timestamp", "year", "month", "day_of_month", "day_of_week",
            "hour", "minute", "days_in_month", "absent", "absent_over_time",
            "sum_over_time", "avg_over_time", "min_over_time", "max_over_time",
            "count_over_time", "quantile_over_time", "stddev_over_time", "stdvar_over_time",
            "by", "without", "on", "ignoring", "group_left", "group_right",
            "and", "or", "unless", "if", "offset", "bool",
            "le", "NaN", "Inf",
        };

        // Track whether we actually checked any real metric name. If not, the query is either
        // a pure numeric/function expression (useless) or a label-selector-only trick that we
        // must reject rather than pass through.
        var sawMetric = false;
        foreach (System.Text.RegularExpressions.Match m in metricMatches)
        {
            var token = m.Groups[1].Value;
            if (reserved.Contains(token)) continue;
            // Skip pure numeric literals — the regex is permissive.
            if (token.All(char.IsDigit)) continue;
            sawMetric = true;
            // Anything else must match one of the allowed metric-name prefixes.
            var ok = prefixes.Any(p => token.StartsWith(p, StringComparison.Ordinal));
            if (!ok)
                return BadRequest(new { message = $"metric '{token}' is not in the allowed-prefix set ({string.Join(", ", prefixes)}). Configure OpenTelemetry:AllowedMetricPrefixes if you need more." });
        }

        if (!sawMetric)
            return BadRequest(new
            {
                message = "PromQL: the query must reference at least one allowed metric name — pure label-selector queries (`{label=...}`) are rejected because they bypass the metric allow-list.",
            });
        return null;
    }

    // M-20: raw PromQL (and the pre-composed summary) can reveal infrastructure metrics,
    // tenant activity patterns, and — via co-tenant series on a shared Prometheus — data
    // from outside NodePilot. Viewer role (read-only) gets access to the UI's tile widgets
    // only, not to the raw query surface. Admin/Operator retain full access for dashboards.
    [HttpGet("query")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Query([FromQuery] string query, [FromQuery] long? time, CancellationToken ct)
    {
        if (!_prom.IsConfigured)
            return StatusCode(503, new { message = "Prometheus query endpoint is not configured." });
        if (ValidatePromQl(query) is { } bad) return bad;

        var result = await _prom.InstantAsync(query, time, ct);
        return new ContentResult { Content = result.Body, ContentType = result.ContentType, StatusCode = result.StatusCode };
    }

    [HttpGet("query_range")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> QueryRange(
        [FromQuery] string query,
        [FromQuery] long start,
        [FromQuery] long end,
        [FromQuery] string step,
        CancellationToken ct)
    {
        if (!_prom.IsConfigured)
            return StatusCode(503, new { message = "Prometheus query endpoint is not configured." });
        if (ValidatePromQl(query) is { } bad) return bad;

        var result = await _prom.RangeAsync(query, start, end, step, ct);
        return new ContentResult { Content = result.Body, ContentType = result.ContentType, StatusCode = result.StatusCode };
    }

    /// <summary>
    /// Pre-composed telemetry-mode dashboard summary. Runs a curated set of PromQL
    /// expressions in parallel so the UI can render one combined overview panel.
    /// </summary>
    [HttpGet("summary")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<TelemetrySummaryResponse>> Summary(CancellationToken ct)
    {
        if (!_prom.IsConfigured)
            return Ok(new TelemetrySummaryResponse(Available: false, Panels: new List<TelemetryPanel>()));

        var queries = new (string Key, string Title, string Query, string Unit)[]
        {
            ("executions_per_minute", "Executions / min (5m rate)",
                "sum(rate(nodepilot_executions_started[5m])) * 60", "per_min"),
            ("success_rate_1h", "Success rate (1h)",
                "sum(rate(nodepilot_executions_completed{status=\"Succeeded\"}[1h])) / clamp_min(sum(rate(nodepilot_executions_completed[1h])), 0.00001)",
                "ratio"),
            ("failed_last_1h", "Failed executions (1h)",
                "sum(increase(nodepilot_executions_completed{status=\"Failed\"}[1h]))", "count"),
            ("active_executions", "Active executions",
                "sum(nodepilot_executions_active)", "count"),
            ("p95_duration_5m", "p95 duration (5m, ms)",
                "histogram_quantile(0.95, sum(rate(nodepilot_execution_duration_milliseconds_bucket[5m])) by (le))",
                "ms"),
            ("winrm_session_open_p95", "WinRM connect p95 (5m, ms)",
                "histogram_quantile(0.95, sum(rate(nodepilot_winrm_session_open_duration_milliseconds_bucket[5m])) by (le))",
                "ms"),
            ("http_rps", "API RPS (1m)",
                "sum(rate(http_server_request_duration_seconds_count[1m]))", "rps"),
            ("http_error_rate", "API 5xx rate (5m)",
                "sum(rate(http_server_request_duration_seconds_count{http_response_status_code=~\"5..\"}[5m]))",
                "rps"),
        };

        var tasks = queries.Select(q => RunInstantAsync(q.Key, q.Title, q.Query, q.Unit, ct)).ToArray();
        var panels = (await Task.WhenAll(tasks)).ToList();
        return Ok(new TelemetrySummaryResponse(Available: true, Panels: panels));
    }

    /// <summary>
    /// Native metrics view. Unlike the raw PromQL endpoints this route exposes only a
    /// fixed, reviewed catalogue, so every authenticated role may read it safely.
    /// </summary>
    [HttpGet("dashboards/{key}")]
    public async Task<ActionResult<MetricsDashboardResponse>> Dashboard(string key, [FromQuery] int hours = 24, CancellationToken ct = default)
    {
        if (!MetricsDashboardCatalog.Exists(key)) return NotFound(new { message = "Unknown metrics dashboard." });
        hours = hours switch { 1 or 24 or 168 or 720 => hours, _ => 24 };
        if (!_prom.IsConfigured)
            return Ok(new MetricsDashboardResponse(false, key, MetricsDashboardCatalog.Title(key), [], [], [], []));
        return Ok(await MetricsDashboardCatalog.ExecuteAsync(key, hours, _prom, _logger, ct));
    }

    private async Task<MetricsSeries> RunSeriesAsync(MetricsSeriesDefinition definition, long start, long end, int hours, CancellationToken ct)
    {
        try
        {
            var step = hours <= 24 ? "60" : hours <= 168 ? "300" : "1800";
            var response = await _prom.RangeAsync(definition.Query, start, end, step, ct);
            return new MetricsSeries(definition.Key, definition.Title, definition.Unit,
                response.IsSuccess ? ParseRange(response.Body) : []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prometheus series '{Key}' failed", definition.Key);
            return new MetricsSeries(definition.Key, definition.Title, definition.Unit, []);
        }
    }

    private async Task<MetricsTable> RunTableAsync(MetricsTableDefinition definition, CancellationToken ct)
    {
        try
        {
            var response = await _prom.InstantAsync(definition.Query, null, ct);
            return new MetricsTable(definition.Key, definition.Title, definition.Unit,
                response.IsSuccess ? ParseVector(response.Body).Take(10).ToList() : []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prometheus table '{Key}' failed", definition.Key);
            return new MetricsTable(definition.Key, definition.Title, definition.Unit, []);
        }
    }

    private static List<MetricsSeriesLine> ParseRange(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var result = doc.RootElement.GetProperty("data").GetProperty("result");
            return result.EnumerateArray().Select(item => new MetricsSeriesLine(
                SeriesLabel(item),
                item.GetProperty("values").EnumerateArray().Select(v => new MetricsPoint(
                    (long)v[0].GetDouble(), ParseNumber(v[1].GetString()))).ToList())).ToList();
        }
        catch { return []; }
    }

    private static List<MetricsTableRow> ParseVector(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("data").GetProperty("result").EnumerateArray()
                .Select(item => new MetricsTableRow(SeriesLabel(item), ParseNumber(item.GetProperty("value")[1].GetString())))
                .OrderByDescending(r => r.Value).ToList();
        }
        catch { return []; }
    }

    private static string SeriesLabel(JsonElement item)
    {
        if (!item.TryGetProperty("metric", out var metric)) return "Value";
        foreach (var key in new[] { "workflow_name", "activity_type", "trigger_type", "http_route", "operation", "result", "status", "nodepilot_llm_kind" })
            if (metric.TryGetProperty(key, out var value) && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!;
        return "Total";
    }

    private static double ParseNumber(string? raw) => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private sealed record MetricDefinition(string Key, string Title, string Query, string Unit);
    private sealed record MetricsSeriesDefinition(string Key, string Title, string Query, string Unit);
    private sealed record MetricsTableDefinition(string Key, string Title, string Query, string Unit);
    private sealed record MetricsDashboardDefinition(string Key, string Title, MetricDefinition[] Panels, MetricsSeriesDefinition[] Series, MetricsTableDefinition[] Tables);

    private static readonly IReadOnlyDictionary<string, MetricsDashboardDefinition> MetricsDashboards = new Dictionary<string, MetricsDashboardDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["mission-control"] = D("mission-control", "Mission Control", "Active executions|sum(nodepilot_executions_active) or vector(0)|count", "Executions / min|sum(rate(nodepilot_executions_started_total[5m])) * 60 or vector(0)|per_min", "Success rate|sum(rate(nodepilot_executions_completed_total{status=\"Succeeded\"}[1h])) / clamp_min(sum(rate(nodepilot_executions_completed_total[1h])), .0001)|ratio", "Execution throughput|sum(rate(nodepilot_executions_started_total[5m])) * 60|per_min", "Top failing workflows|topk(10, sum by (workflow_name) (increase(nodepilot_executions_completed_total{status=\"Failed\"}[1h])))|count"),
        ["workflows"] = D("workflows", "Workflows", "Started|sum(increase(nodepilot_executions_started_total[24h])) or vector(0)|count", "Failed|sum(increase(nodepilot_executions_completed_total{status=\"Failed\"}[24h])) or vector(0)|count", "p95 duration|histogram_quantile(.95, sum(rate(nodepilot_execution_duration_milliseconds_bucket[5m])) by (le)) or vector(0)|ms", "Workflow executions|sum by (status) (rate(nodepilot_executions_completed_total[5m]))|per_min", "Most active workflows|topk(10, sum by (workflow_name) (increase(nodepilot_executions_started_total[24h])))|count"),
        ["activities"] = D("activities", "Activities", "Steps|sum(increase(nodepilot_steps_executed_total[24h])) or vector(0)|count", "Retries|sum(increase(nodepilot_step_retry_attempts_total[24h])) or vector(0)|count", "p95 duration|histogram_quantile(.95, sum(rate(nodepilot_step_duration_milliseconds_bucket[5m])) by (le)) or vector(0)|ms", "Steps by type|sum by (activity_type) (rate(nodepilot_steps_executed_total[5m]))|per_min", "Most used activities|topk(10, sum by (activity_type) (increase(nodepilot_steps_executed_total[24h])))|count"),
        ["winrm"] = D("winrm", "WinRM", "Active sessions|sum(nodepilot_winrm_sessions_active) or vector(0)|count", "Auth failures / min|sum(rate(nodepilot_winrm_auth_failures_total[5m])) * 60 or vector(0)|per_min", "Connect p95|histogram_quantile(.95, sum(rate(nodepilot_winrm_session_open_duration_milliseconds_bucket[5m])) by (le)) or vector(0)|ms", "Sessions opened|sum(rate(nodepilot_winrm_sessions_opened_total[5m])) * 60|per_min", "Results|sum by (result) (increase(nodepilot_winrm_sessions_opened_total[24h]))|count"),
        ["triggers"] = D("triggers", "Triggers & Scheduler", "Fires / min|sum(rate(nodepilot_triggers_fired_total[5m])) * 60 or vector(0)|per_min", "Sync failures|sum(increase(nodepilot_trigger_orchestrator_sync_failures_total[1h])) or vector(0)|count", "Webhooks|sum(increase(nodepilot_webhook_requests_total[1h])) or vector(0)|count", "Trigger fires|sum by (trigger_type) (rate(nodepilot_triggers_fired_total[5m])) * 60|per_min", "Trigger types|sum by (trigger_type) (increase(nodepilot_triggers_fired_total[24h]))|count"),
        ["api"] = D("api", "API & HTTP", "Requests / s|sum(rate(http_server_request_duration_seconds_count[1m])) or vector(0)|rps", "5xx / s|sum(rate(http_server_request_duration_seconds_count{http_response_status_code=~\"5..\"}[5m])) or vector(0)|rps", "p95 latency|histogram_quantile(.95, sum(rate(http_server_request_duration_seconds_bucket[5m])) by (le)) or vector(0)|seconds", "Requests by route|sum by (http_route) (rate(http_server_request_duration_seconds_count[5m]))|rps", "Slowest routes|topk(10, sum by (http_route) (rate(http_server_request_duration_seconds_sum[5m])) / sum by (http_route) (rate(http_server_request_duration_seconds_count[5m])))|seconds"),
        ["runtime"] = D("runtime", "Runtime", "CPU|100 * (sum(rate(process_cpu_time_seconds_total[1m])) / on() group_left() avg(process_cpu_count))|percent", "Memory|process_memory_usage_bytes|bytes", "Exceptions / s|sum(rate(dotnet_exceptions_total[2m])) or vector(0)|rps", "CPU|100 * (sum(rate(process_cpu_time_seconds_total[1m])) / on() group_left() avg(process_cpu_count))|percent", "Runtime signals|sum by (reason) (increase(dotnet_exceptions_total[24h]))|count"),
        ["security"] = D("security", "Security & Audit", "Failed login rate|sum(rate(nodepilot_auth_login_attempts_total{result=\"failure\"}[5m])) / clamp_min(sum(rate(nodepilot_auth_login_attempts_total[5m])), .0001)|ratio", "Lockouts|sum(increase(nodepilot_auth_lockouts_total[1h])) or vector(0)|count", "Rate limit rejections|sum(increase(nodepilot_rate_limit_rejections_total[1h])) or vector(0)|count", "Login attempts|sum by (result) (rate(nodepilot_auth_login_attempts_total[5m]))|per_min", "Login results|sum by (result) (increase(nodepilot_auth_login_attempts_total[24h]))|count"),
        ["ai"] = D("ai", "AI / LLM", "Calls|sum(increase(nodepilot_llm_calls_total[24h])) or vector(0)|count", "Success rate|sum(increase(nodepilot_llm_calls_total{result=\"success\"}[24h])) / clamp_min(sum(increase(nodepilot_llm_calls_total[24h])), .0001)|ratio", "Tokens|sum(increase(nodepilot_llm_tokens_total[24h])) or vector(0)|count", "Calls by kind|sum by (nodepilot_llm_kind, result) (rate(nodepilot_llm_calls_total[5m]))|per_min", "Token use|topk(10, sum by (nodepilot_llm_kind) (increase(nodepilot_llm_tokens_total[24h])))|count"),
        ["database"] = D("database", "Database", "Saves / s|sum(rate(nodepilot_db_save_changes_total[1m])) or vector(0)|rps", "Save p95|histogram_quantile(.95, sum(rate(nodepilot_db_save_changes_duration_milliseconds_bucket[5m])) by (le)) or vector(0)|ms", "Failures|sum(rate(nodepilot_db_save_changes_total{status=\"failure\"}[5m])) or vector(0)|rps", "Saves by operation|sum by (operation) (rate(nodepilot_db_save_changes_total[5m]))|rps", "Rows by operation|topk(10, sum by (operation) (rate(nodepilot_db_save_changes_rows_sum[5m])))|count"),
    };

    private static MetricsDashboardDefinition D(string key, string title, string p1, string p2, string p3, string series, string table)
    {
        static MetricDefinition Panel(string raw) { var p = raw.Split('|'); return new(p[0], p[0], p[1], p[2]); }
        static MetricsSeriesDefinition Series(string raw) { var p = raw.Split('|'); return new(p[0], p[0], p[1], p[2]); }
        static MetricsTableDefinition Table(string raw) { var p = raw.Split('|'); return new(p[0], p[0], p[1], p[2]); }
        return new(key, title, [Panel(p1), Panel(p2), Panel(p3)], [Series(series)], [Table(table)]);
    }

    private async Task<TelemetryPanel> RunInstantAsync(string key, string title, string query, string unit, CancellationToken ct)
    {
        try
        {
            var value = await _prom.QueryScalarAsync(query, ct);
            return new TelemetryPanel(key, title, unit, value, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prometheus panel '{Key}' failed", key);
            return new TelemetryPanel(key, title, unit, null, ex.Message);
        }
    }
}
