using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NodePilot.Telemetry;

/// <summary>
/// Thin HTTP client for a Prometheus-compatible query API
/// (<c>/api/v1/query</c>, <c>/api/v1/query_range</c>). Handles basic/bearer auth and
/// extracts scalar values from instant-query responses.
/// </summary>
public sealed class PrometheusClient
{
    private readonly HttpClient _http;
    private readonly NodePilotTelemetryOptions _options;

    public PrometheusClient(HttpClient http, NodePilotTelemetryOptions options)
    {
        _http = http;
        _options = options;

        // Configure timeout + auth ONCE here, not per-request. The summary endpoint fires
        // ~8 queries concurrently over this single HttpClient; HttpClient.Timeout throws
        // InvalidOperationException once any request is in flight, so a per-call setter made
        // every concurrent query after the first fail. Headers/timeout are call-invariant.
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.Prometheus.TimeoutSeconds));

        if (!string.IsNullOrWhiteSpace(_options.Prometheus.BearerToken))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.Prometheus.BearerToken);
        }
        else if (!string.IsNullOrWhiteSpace(_options.Prometheus.Username))
        {
            var raw = $"{_options.Prometheus.Username}:{_options.Prometheus.Password}";
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", b64);
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Prometheus.QueryEndpoint);

    public async Task<PrometheusProxyResult> InstantAsync(string query, long? timeEpochSeconds, CancellationToken ct)
    {
        return await ProxyAsync("/api/v1/query", new (string, string?)[]
        {
            ("query", query),
            ("time", timeEpochSeconds?.ToString()),
        }, ct);
    }

    public async Task<PrometheusProxyResult> RangeAsync(string query, long start, long end, string step, CancellationToken ct)
    {
        return await ProxyAsync("/api/v1/query_range", new (string, string?)[]
        {
            ("query", query),
            ("start", start.ToString()),
            ("end", end.ToString()),
            ("step", step),
        }, ct);
    }

    public async Task<double?> QueryScalarAsync(string query, CancellationToken ct)
    {
        var result = await InstantAsync(query, null, ct);
        if (!result.IsSuccess) return null;
        return PrometheusResponseParser.TryExtractScalar(result.Body);
    }

    private async Task<PrometheusProxyResult> ProxyAsync(string relative, IEnumerable<(string Key, string? Value)> parameters, CancellationToken ct)
    {
        if (!IsConfigured)
            return new PrometheusProxyResult(false, 503, "application/json", "{\"error\":\"Prometheus endpoint not configured\"}");

        var url = BuildUrl(relative, parameters);
        using var resp = await _http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        return new PrometheusProxyResult(resp.IsSuccessStatusCode, (int)resp.StatusCode, contentType, body);
    }

    private string BuildUrl(string relative, IEnumerable<(string Key, string? Value)> parameters)
    {
        var baseUrl = _options.Prometheus.QueryEndpoint!.TrimEnd('/');
        var qs = string.Join("&", parameters
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));
        return $"{baseUrl}{relative}?{qs}";
    }
}

public readonly record struct PrometheusProxyResult(bool IsSuccess, int StatusCode, string ContentType, string Body);

public static class PrometheusResponseParser
{
    /// <summary>Parses a Prometheus numeric string, rejecting NaN/infinity because JSON cannot represent them.</summary>
    public static double? TryParseFiniteNumber(string? raw)
    {
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            return null;
        return double.IsFinite(value) ? value : null;
    }

    /// <summary>
    /// Extracts the numeric value from a Prometheus instant query response of type
    /// <c>vector</c> (first sample) or <c>scalar</c>. Returns null if the response
    /// is empty or malformed.
    /// </summary>
    public static double? TryExtractScalar(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty("resultType", out var rt)) return null;
            var resultType = rt.GetString();

            if (resultType == "scalar")
            {
                if (data.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array && result.GetArrayLength() >= 2)
                {
                    var valueStr = result[1].GetString();
                    var value = TryParseFiniteNumber(valueStr);
                    if (value.HasValue) return value;
                }
                return null;
            }

            if (resultType == "vector")
            {
                if (!data.TryGetProperty("result", out var vec) || vec.ValueKind != JsonValueKind.Array || vec.GetArrayLength() == 0)
                    return null;
                var first = vec[0];
                if (!first.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 2)
                    return null;
                var valueStr = v[1].GetString();
                var value = TryParseFiniteNumber(valueStr);
                if (value.HasValue) return value;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
