using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

public class RestApiActivity : IActivityExecutor
{
    private readonly RestApiHttpClientProvider _clientProvider;
    private readonly IConfiguration _config;

    public string ActivityType => "restApi";

    // Headers that must never cross an origin boundary via a redirect. Single source of truth in
    // Core (also drives definition redaction of a restApi headers string). Keep in sync with
    // WebhooksController.BlockedHeaderNames so the "don't forward auth on cross-origin redirect"
    // rule lives in one mental model.
    private static readonly IReadOnlySet<string> CredentialHeaders = WorkflowSecretContent.CredentialHeaderNames;

    // Bound cross-origin redirect chains. Five hops is well above any legitimate API and
    // stops redirect-ping-pong from spinning the engine.
    private const int MaxRedirects = 5;

    public RestApiActivity(RestApiHttpClientProvider clientProvider, IConfiguration config)
    {
        _clientProvider = clientProvider;
        _config = config;
    }

    private const int MaxResponseBytes = 16 * 1024 * 1024;

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
        => ActivityExecution.RunAsync(async () =>
        {
            var url = config.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                return new ActivityResult { Success = false, ErrorOutput = "REST API: 'url' is required" };

            // Initial URL validation — SSRF guard, scheme allow-list. The per-hop revalidation
            // happens below in the manual redirect loop.
            NetworkGuard.ValidateUrl(_config, url);
            var initialUrl = new Uri(url, UriKind.Absolute);
            _clientProvider.ValidateDestinationPolicy(config, initialUrl);

            var method = config.TryGetProperty("method", out var m) ? m.GetString()!.ToUpperInvariant() : "GET";
            var body = ReadBody(config);

            // Resolve the HttpClient via the proxy-aware provider. Default mode uses the
            // "NodePilot" named client (configured once from RestApi:Proxy:*). proxyMode
            // "direct"/"custom" yields a step-local client over a cached handler so
            // connection pools are still reused. AllowAutoRedirect is always off so we can
            // revalidate every hop here (closes H6 + L2 from the SSRF audit).
            using var client = _clientProvider.GetClient(config);

            var headers = ParseHeaders(config);

            var timeoutSeconds = config.GetOptionalPositiveInt("timeoutSeconds");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutSeconds is { } secs)
                cts.CancelAfter(TimeSpan.FromSeconds(secs));

            var response = await SendWithManualRedirectsAsync(
                client, config, initialUrl, new HttpMethod(method), body, headers, cts.Token);

            return await BuildResultAsync(response, cts.Token);
        });

    private static string? ReadBody(JsonElement config)
    {
        if (!config.TryGetProperty("body", out var b)) return null;
        return b.ValueKind == JsonValueKind.String ? b.GetString() : b.GetRawText();
    }

    private async Task<HttpResponseMessage> SendWithManualRedirectsAsync(
        HttpClient client,
        JsonElement stepConfig,
        Uri initialUrl,
        HttpMethod initialMethod,
        string? initialBody,
        List<(string Name, string Value)> initialHeaders,
        CancellationToken ct)
    {
        // Manual redirect loop so we can (a) re-run NetworkGuard against the new target on
        // every hop, and (b) strip credential-bearing headers when we cross an origin.
        var currentUrl = initialUrl;
        var currentMethod = initialMethod;
        var currentBody = initialBody;
        var effectiveHeaders = new List<(string Name, string Value)>(initialHeaders);
        var hops = 0;

        while (true)
        {
            using var req = new HttpRequestMessage(currentMethod, currentUrl);
            if (!string.IsNullOrEmpty(currentBody)
                && currentMethod != HttpMethod.Get && currentMethod != HttpMethod.Head)
                req.Content = new StringContent(currentBody, System.Text.Encoding.UTF8, "application/json");
            foreach (var (name, value) in effectiveHeaders)
                req.Headers.TryAddWithoutValidation(name, value);

            var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!ShouldFollowRedirect(response, hops))
                return response;

            var nextUrl = ResolveRedirectTarget(currentUrl, response.Headers.Location!);
            try
            {
                NetworkGuard.ValidateUrl(_config, nextUrl.ToString());
                _clientProvider.ValidateDestinationPolicy(stepConfig, nextUrl);
                ApplyRedirectPolicy(response.StatusCode, currentUrl, nextUrl, effectiveHeaders, ref currentMethod, ref currentBody);
            }
            catch
            {
                response.Dispose();
                throw;
            }

            response.Dispose();
            currentUrl = nextUrl;
            hops++;
        }
    }

    private static bool ShouldFollowRedirect(HttpResponseMessage response, int hops) =>
        (int)response.StatusCode is >= 300 and < 400
        && response.Headers.Location is not null
        && hops < MaxRedirects;

    private static Uri ResolveRedirectTarget(Uri current, Uri location) =>
        location.IsAbsoluteUri ? location : new Uri(current, location);

    private static void ApplyRedirectPolicy(
        System.Net.HttpStatusCode statusCode,
        Uri currentUrl,
        Uri nextUrl,
        List<(string Name, string Value)> effectiveHeaders,
        ref HttpMethod currentMethod,
        ref string? currentBody)
    {
        // Drop credential-bearing headers when the authority changes — the secret
        // was issued for the original host, not for whoever 302's us.
        if (!SameAuthority(currentUrl, nextUrl))
            effectiveHeaders.RemoveAll(h => CredentialHeaders.Contains(h.Name));

        // Per RFC 7231 §6.4.2/§6.4.3, 301/302/303 with a non-GET body "SHOULD" be
        // resubmitted as GET. We take the conservative route and drop the body on
        // redirect — fewer surprises, matches what most HTTP clients do.
        if ((int)statusCode is 301 or 302 or 303)
        {
            currentMethod = HttpMethod.Get;
            currentBody = null;
        }
        // M-11: 307/308 preserve the body and method per RFC, BUT when the redirect
        // crosses authority the body may contain credentials or secrets that were
        // intended only for the original origin (API key in JSON body, etc). Drop
        // the body defensively — mirrors the cross-authority header-stripping above.
        else if ((int)statusCode is 307 or 308 && !SameAuthority(currentUrl, nextUrl))
        {
            currentBody = null;
        }
    }

    private static async Task<ActivityResult> BuildResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var statusCode = response.StatusCode;
        var isSuccess = response.IsSuccessStatusCode;

        try
        {
            var (body, exceeded) = await ReadBoundedBodyAsync(response, ct);
            if (exceeded)
            {
                return new ActivityResult
                {
                    Success = false,
                    ErrorOutput = $"REST API: response body exceeded {MaxResponseBytes} bytes (status {(int)statusCode}).",
                    OutputParameters = StatusCodeOutput(statusCode),
                };
            }

            return new ActivityResult
            {
                Success = isSuccess,
                Output = $"HTTP {(int)statusCode} {statusCode}\n{body}",
                ErrorOutput = isSuccess ? null : body,
                // Expose statusCode as an output parameter so workflows can branch on
                // 4xx/5xx without parsing the Output string. Always emitted regardless of
                // success — error-path callers (e.g. "fail-route on 401") depend on this.
                OutputParameters = StatusCodeOutput(statusCode),
            };
        }
        finally
        {
            response.Dispose();
        }
    }

    private static Dictionary<string, string> StatusCodeOutput(System.Net.HttpStatusCode statusCode) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["statusCode"] = ((int)statusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    private static async Task<(string Body, bool Exceeded)> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // M-12: bounded response read. A malicious / misconfigured endpoint that returns
        // 50 GiB of chunked body would otherwise pin the managed heap for the lifetime of
        // the step. 16 MiB is generous for real API responses; beyond that, fail the step.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
                return ("", true);
            ms.Write(buf, 0, read);
        }
        return (System.Text.Encoding.UTF8.GetString(ms.ToArray()), false);
    }

    /// <summary>
    /// Accept either a JSON object {"Key":"Value"} or a multi-line string
    /// "Key: Value\nKey2: Value2" (the format used by the UI textarea).
    /// </summary>
    private static List<(string Name, string Value)> ParseHeaders(JsonElement config)
    {
        var list = new List<(string, string)>();
        if (!config.TryGetProperty("headers", out var headers)) return list;
        if (headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var h in headers.EnumerateObject())
                list.Add((h.Name, h.Value.GetString() ?? ""));
        }
        else if (headers.ValueKind == JsonValueKind.String)
        {
            foreach (var line in (headers.GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = line.IndexOf(':');
                if (idx > 0)
                {
                    var name = line[..idx].Trim();
                    var value = line[(idx + 1)..].Trim();
                    if (name.Length > 0) list.Add((name, value));
                }
            }
        }
        return list;
    }

    private static bool SameAuthority(Uri a, Uri b)
        => string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
           && a.Port == b.Port;
}
