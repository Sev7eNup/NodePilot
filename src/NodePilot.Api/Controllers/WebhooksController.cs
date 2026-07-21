using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.ExecutionDispatch;
using NodePilot.Api.Security;
using NodePilot.Api.Telemetry;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

/// <summary>
/// HTTP endpoint that fires workflows containing a <c>webhookTrigger</c> node.
///
/// URL shape: <c>/api/webhooks/{workflowNameOrId}/{path?}</c>.
/// The trigger node's config may set:
///   - path (string) — required suffix after the workflow identifier
///   - method ("POST"|"GET"|...) — default "POST"; must match the incoming request
///   - secret (string) — optional; the shared secret. Verification mode depends on:
///   - signatureMode ("header"|"nodepilot-hmac-v2") — default "header": caller sends
///     the secret verbatim in <c>X-Webhook-Secret</c>. "nodepilot-hmac-v2" is NodePilot's
///     replay-safe protocol; it is intentionally not a provider body-only HMAC preset.
///   - signatureHeader (string) — default "X-NodePilot-Signature"; only used in v2 mode.
///   - signaturePrefix (string) — default "sha256="; only used in v2 mode.
///     V2 requests also carry <c>X-NodePilot-Timestamp</c> (UNIX seconds) and
///     <c>X-NodePilot-Delivery-Id</c>. The signed bytes are documented by
///     <see cref="WebhookHmacSecurity"/> and bind the method, path, canonical query and raw body.
///   - fieldMappings ([{name, path}]) — optional JSONPath extraction from a JSON body:
///     each entry lands as its own input parameter (same JSONPath dialect as jsonQuery),
///     so downstream nodes read <c>{{manual.ticketId}}</c> instead of re-parsing
///     <c>webhookBody</c>.
///
/// Request body + query + headers are forwarded as input parameters on the workflow
/// execution. Downstream nodes access them either via the webhookTrigger node's
/// OutputParameters (the trigger executor copies the manual-prefixed keys out without
/// the prefix) or directly via the <c>manual.</c> namespace:
///   {{webhookTrigger.param.webhookBody}}   /  {{manual.webhookBody}}    raw request body
///   {{webhookTrigger.param.webhookMethod}} /  {{manual.webhookMethod}}  HTTP verb
///   {{webhookTrigger.param.webhookPath}}   /  {{manual.webhookPath}}    suffix after the workflow id
///   {{webhookTrigger.param.webhookQuery_X}}  / {{manual.webhookQuery_X}}   per-query-string key
///   {{webhookTrigger.param.webhookHeader_X}} / {{manual.webhookHeader_X}}  per-request-header key
///   {{webhookTrigger.param.&lt;name&gt;}}          / {{manual.&lt;name&gt;}}           per fieldMappings entry
/// (Non-letter/digit chars in query/header names collapse to <c>_</c>; capped count per kind.)
/// </summary>
[ApiController]
[Route("api/webhooks")]
[AllowAnonymous]
public class WebhooksController : ControllerBase
{
    private readonly NodePilotDbContext _db;
    private readonly ExecutionDispatchService _dispatchService;
    private readonly ILogger<WebhooksController> _logger;
    private readonly IConfiguration _config;
    private readonly IAuditWriter _audit;
    private readonly IMaintenanceWindowEvaluator _maintenance;

    public WebhooksController(
        NodePilotDbContext db,
        ExecutionDispatchService dispatchService,
        ILogger<WebhooksController> logger,
        IConfiguration config,
        IAuditWriter audit,
        IMaintenanceWindowEvaluator maintenance)
    {
        _db = db;
        _dispatchService = dispatchService;
        _logger = logger;
        _config = config;
        _audit = audit;
        _maintenance = maintenance;
    }

    // 1 MiB cap. Webhook payloads larger than this are almost never legitimate (config/params,
    // not file upload) and the body is forwarded into workflow variables + logs verbatim, so
    // unbounded size is a memory- and log-volume risk.
    internal const long MaxWebhookBodyBytes = 1024 * 1024;

    // Forwarding caps (audit M6). Without these every header and query-string value from the
    // incoming request was copied verbatim into the workflow's variable bag AND emitted into
    // every structured log line — a caller could push ~40 KB of attacker-controlled data per
    // request into the logs at the full webhook rate limit. Caps chosen to cover all real
    // webhook integrations (GitHub sends ~12 headers, most under 100 chars) with margin.
    private const int MaxForwardedHeaders = 32;
    private const int MaxForwardedQueryParams = 32;
    private const int MaxForwardedHeaderKeyLen = 64;
    private const int MaxForwardedHeaderValueLen = 2048;

    // Never forward credential-bearing or hop-by-hop headers into workflow parameters.
    // Match case-insensitively because HTTP header names are case-insensitive per RFC 9110.
    private static readonly HashSet<string> BlockedHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "X-Webhook-Secret",
    };

    [HttpPost("{workflowNameOrId}/{**path}")]
    [HttpGet("{workflowNameOrId}/{**path}")]
    [HttpPut("{workflowNameOrId}/{**path}")]
    [HttpDelete("{workflowNameOrId}/{**path}")]
    [RequestSizeLimit(MaxWebhookBodyBytes)]
    [EnableRateLimiting("webhook")]
    public async Task<IActionResult> Hit(string workflowNameOrId, string? path, CancellationToken ct)
    {
        Workflow? workflow;
        if (Guid.TryParse(workflowNameOrId, out var id))
        {
            workflow = await _db.Workflows.FindAsync([id], ct);
        }
        else
        {
            var resolved = await WorkflowNameResolver.ResolveByNameAsync(_db.Workflows, workflowNameOrId, ct);
            if (resolved.Outcome == WorkflowNameResolver.Outcome.Ambiguous) return HiddenReject("ambiguous_name");
            workflow = resolved.Workflow;
        }

        if (workflow is null) return HiddenReject("not_found");

        if (!WorkflowDefinitionDocument.TryParse(workflow.DefinitionJson, out var definition) || definition is null)
            return HiddenReject("no_webhook_node");

        var webhookTrigger = definition.FindFirstTrigger("webhookTrigger");
        if (webhookTrigger is null || webhookTrigger.Config.ValueKind != JsonValueKind.Object)
            return HiddenReject("no_webhook_node");

        var cfg = webhookTrigger.Config;

        // Optional shared secret
        var secret = cfg.TryGetProperty("secret", out var s) ? s.GetString() : null;
        var hasSecret = !string.IsNullOrWhiteSpace(secret);
        var hasSignatureMode = cfg.TryGetProperty("signatureMode", out var sigMode);
        if (hasSignatureMode && sigMode.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            return HiddenReject("invalid_signature_mode");
        var signatureMode = sigMode.ValueKind == JsonValueKind.String ? sigMode.GetString() : null;
        var useHmac = WebhookHmacSecurity.IsV2Mode(signatureMode);
        var useHeaderSecret = string.IsNullOrWhiteSpace(signatureMode)
                              || string.Equals(signatureMode, "header", StringComparison.OrdinalIgnoreCase);
        if (!useHmac && !useHeaderSecret)
            return HiddenReject("unsupported_signature_mode");

        var effectiveSignatureHeader = WebhookHmacSecurity.DefaultSignatureHeader;
        if (useHmac && cfg.TryGetProperty("signatureHeader", out var configuredSignatureHeader))
        {
            if (configuredSignatureHeader.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return HiddenReject("invalid_signature_header");
            effectiveSignatureHeader = configuredSignatureHeader.GetString();
            if (string.IsNullOrWhiteSpace(effectiveSignatureHeader))
                effectiveSignatureHeader = WebhookHmacSecurity.DefaultSignatureHeader;
        }

        // M-13 invariant: no body bytes are buffered for a request that fails auth.
        // The ONE exception is HMAC mode, whose signature is computed over the exact raw
        // request bytes (Request.Body is forward-only), so the buffer must exist before
        // verification. Bounded by RequestSizeLimit (1 MiB) either way; header mode
        // buffers only after every auth/path/method gate passed (below).
        async Task<byte[]> BufferBodyAsync()
        {
            if (!useHmac
                && !HttpMethods.IsPost(Request.Method)
                && !HttpMethods.IsPut(Request.Method)
                && !HttpMethods.IsPatch(Request.Method))
            {
                return [];
            }
            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        var rawBody = useHmac ? await BufferBodyAsync() : [];

        // H-14: reject webhookTrigger nodes that were saved without a configured secret.
        // Default-on since Phase 3: a missing Webhook:RequireSecret key behaves like "true"
        // so a stripped-down deployment falls on the safe side. Dev environments that test
        // secret-less webhooks set Webhook:RequireSecret=false in appsettings.Development.json.
        // Secret-less webhooks always emit a warning log so the admin sees the exposure on
        // every fire, regardless of whether the request is rejected.
        string? deliveryId = null;
        string? replayClaimToken = null;
        if (useHmac)
        {
            // HMAC mode is always fail-closed, including development deployments that allow
            // secret-less legacy header webhooks. Legacy database rows may predate the
            // publish/import validators, so enforce the key floor again at request time.
            if (!WebhookHmacSecurity.TryGetKeyBytes(secret, out var hmacKey))
            {
                _logger.LogWarning(
                    "Webhook HMAC rejected for workflow {WorkflowId}: configured secret has fewer than {MinBytes} UTF-8 bytes.",
                    workflow.Id, WebhookHmacSecurity.MinSecretBytes);
                return HiddenReject("weak_hmac_secret");
            }

            try
            {
                if (!WebhookHmacSecurity.TryParseFreshnessHeaders(
                        Request.Headers[WebhookHmacSecurity.TimestampHeader].ToString(),
                        Request.Headers[WebhookHmacSecurity.DeliveryIdHeader].ToString(),
                        DateTime.UtcNow,
                        out var timestamp,
                        out deliveryId))
                {
                    return HiddenReject("invalid_hmac_freshness");
                }

                if (!Request.Headers.TryGetValue(effectiveSignatureHeader, out var presentedSig))
                    return HiddenReject("missing_signature");

                replayClaimToken = WebhookReplayStore.CreateClaimToken(hmacKey, deliveryId);

                var sigPrefix = "sha256=";
                if (cfg.TryGetProperty("signaturePrefix", out var configuredPrefix))
                {
                    if (configuredPrefix.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        return HiddenReject("invalid_signature_prefix");
                    sigPrefix = configuredPrefix.GetString() ?? "";
                }

                var canonicalPath = WebhookHmacSecurity.CanonicalizePath(Request.PathBase, Request.Path);
                var canonicalQuery = WebhookHmacSecurity.CanonicalizeQuery(Request.Query);
                var mac = WebhookHmacSecurity.ComputeMac(
                    hmacKey,
                    timestamp,
                    deliveryId,
                    Request.Method,
                    canonicalPath,
                    canonicalQuery,
                    rawBody);
                try
                {
                    var expectedSig = sigPrefix.ToLowerInvariant()
                                      + Convert.ToHexString(mac).ToLowerInvariant();
                    if (!SecretComparer.FixedTimeEquals(
                            presentedSig.ToString().Trim().ToLowerInvariant(), expectedSig))
                    {
                        return HiddenReject("bad_signature");
                    }
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(mac);
                }
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(hmacKey);
            }
        }
        else if (!hasSecret)
        {
            _logger.LogWarning(
                "Webhook fired on workflow {WorkflowId} ({Name}) with NO secret configured — anyone with the URL can trigger this workflow. Add a 'secret' to the webhookTrigger node.",
                workflow.Id, workflow.Name);

            if (RequireSecretEnabled())
            {
                return HiddenReject("missing_secret");
                // Uniform 403 — do not distinguish "no secret configured" from
                // "wrong secret" in the response body.
            }
        }
        else
        {
            if (!Request.Headers.TryGetValue("X-Webhook-Secret", out var presented)
                || !SecretComparer.FixedTimeEquals(presented.ToString(), secret!))
            { return HiddenReject("bad_secret"); }
        }

        if (!workflow.IsEnabled) return HiddenReject("workflow_disabled");

        // Maintenance-window gate. HiddenReject (uniform with disabled/secret rejects) so an
        // anonymous caller can't tell a paused-by-window workflow from a missing one. Metric only
        // — webhook reject paths are intentionally not audited to avoid drowning the log in probe
        // noise; the authoritative dispatch gate still backstops the TOCTOU.
        if (_maintenance.Evaluate(workflow.Id, workflow.FolderId, DateTime.UtcNow).Blocked)
        {
            ApiMetrics.MaintenanceWindowBlocks.Add(1, new("source", "webhook"), new("scope", "webhook"));
            return HiddenReject("maintenance_window");
        }

        // Match path + method only after shared-secret validation so anonymous probes do not
        // learn which workflow exists or which route shape it expects.
        var expectedPath = cfg.TryGetProperty("path", out var p) ? p.GetString() : null;
        if (!string.IsNullOrEmpty(expectedPath))
        {
            var actualPath = path ?? "";
            if (!string.Equals(actualPath.TrimStart('/'), expectedPath.TrimStart('/'), StringComparison.Ordinal))
                return HiddenReject("path_mismatch");
        }

        var expectedMethod = (cfg.TryGetProperty("method", out var m) ? m.GetString() : "POST")?.ToUpperInvariant() ?? "POST";
        if (!string.Equals(Request.Method, expectedMethod, StringComparison.OrdinalIgnoreCase))
            return HiddenReject("method_mismatch");

        // Header mode buffers here — after every auth/path/method gate (M-13). HMAC mode
        // already buffered before verification.
        if (!useHmac) rawBody = await BufferBodyAsync();

        // M-13: strict UTF-8 so an attacker cannot smuggle invalid bytes into workflow
        // variables (e.g. script-interpreted locations) via a malformed multi-byte
        // sequence. throwOnInvalidBytes=true surfaces a DecoderFallbackException on bad
        // input; converted to a clean 400.
        string body = "";
        if (rawBody.Length > 0)
        {
            try
            {
                body = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(rawBody);
            }
            catch (DecoderFallbackException)
            {
                return BadRequest(new { message = "Webhook body is not valid UTF-8." });
            }
        }

        // Claim only after every authentication, route, maintenance and body-validation gate.
        // The database unique index makes this atomic across concurrent requests and HA nodes.
        // A valid delivery remains single-use even if dispatch later fails.
        if (useHmac
            && !await new WebhookReplayStore(_db)
                .TryClaimAsync(workflow.Id, replayClaimToken!, DateTime.UtcNow, ct))
        {
            return HiddenReject("replayed_delivery");
        }

        static string Truncate(string v)
            => v.Length <= MaxForwardedHeaderValueLen ? v : v[..MaxForwardedHeaderValueLen];

        var parameters = new Dictionary<string, string>
        {
            ["webhookBody"] = body,
            ["webhookMethod"] = Request.Method,
            ["webhookPath"] = path ?? "",
        };

        // JSONPath field mapping: promote body fields to first-class parameters so
        // downstream nodes read {{manual.ticketId}} instead of re-parsing webhookBody.
        // Runs BEFORE query/header forwarding — reserved/system keys win over mappings,
        // mappings win over query/header keys of the same name.
        if (cfg.TryGetProperty("fieldMappings", out var fieldMappings)
            && fieldMappings.ValueKind == JsonValueKind.Array
            && body.Length > 0)
        {
            foreach (var (key, value) in ExtractFieldMappings(fieldMappings, body, _logger, workflow.Id))
            {
                if (!parameters.ContainsKey(key)) parameters[key] = value;
            }
        }

        // Caps (M6): bound count + per-value length so a burst of oversized headers cannot
        // bloat logs or saturate the step's variable dictionary. Silent truncation is the
        // right semantic — legitimate integrations never hit these limits.
        IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> forwardedQuery = Request.Query;
        if (useHmac)
        {
            // V2 canonicalization makes raw query-key order insignificant. Forward in a stable
            // order too, otherwise sanitized-key collisions or the 32-key cap could produce
            // different workflow inputs for two requests covered by the same MAC.
            forwardedQuery = forwardedQuery.OrderBy(static q => q.Key, StringComparer.Ordinal);
        }
        var qCount = 0;
        foreach (var q in forwardedQuery)
        {
            if (qCount++ >= MaxForwardedQueryParams) break;
            parameters[$"webhookQuery_{SanitizeKey(q.Key)}"] = Truncate(q.Value.ToString());
        }
        // V2 deliberately forwards no HTTP headers: method/path/query/body are authenticated,
        // but arbitrary headers are not part of the wire contract and therefore must not alter
        // workflow behaviour. Header-secret mode keeps the legacy forwarding contract after
        // removing credential-bearing headers.
        var hCount = 0;
        if (!useHmac)
        {
            foreach (var h in Request.Headers)
            {
                if (h.Key.StartsWith(':')) continue;          // HTTP/2 pseudo-headers
                if (BlockedHeaderNames.Contains(h.Key)) continue; // credential-bearing headers
                if (hCount++ >= MaxForwardedHeaders) break;
                parameters[$"webhookHeader_{SanitizeKey(h.Key)}"] = Truncate(h.Value.ToString());
            }
        }

        // Interactive priority: webhooks are latency-sensitive, so they use the queue's
        // priority lane while still consuming the bounded dispatch worker pool.
        var dispatchIntent = new WorkflowDispatchIntent(
            WorkflowId: workflow.Id,
            TriggeredBy: "webhook",
            Parameters: parameters,
            StartedByUserId: workflow.PublishedByUserId,
            Priority: ExecutionDispatchPriority.Interactive,
            RequireWorkflowEnabled: true,
            MissingWorkflowMessage: "Webhook-triggered workflow no longer exists or was disabled before dispatch.",
            EnqueueFailureMessage: "Webhook dispatch was cancelled before enqueue completed.");

        WorkflowExecution pending;
        try
        {
            pending = await _dispatchService.DispatchAsync(dispatchIntent, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook-triggered workflow {Wf} failed to enqueue", workflow.Id);
            // Pending row, if created, is terminally Cancelled by ExecutionDispatch. Surface 503.
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Failed to enqueue webhook dispatch" });
        }

        ApiMetrics.WebhookRequests.Add(1,
            new KeyValuePair<string, object?>("result", "accepted"),
            new KeyValuePair<string, object?>("reason", "ok"));

        // Audit-trail for anonymous external invocations. Rejected paths (404/401/403/405/400)
        // are intentionally NOT audited — path/method probing would otherwise drown legitimate
        // events in noise. The metric on those branches still surfaces them for monitoring.
        await _audit.LogAsync(AuditActions.WebhookTriggered, "Workflow", workflow.Id,
            AuditDetails.Json(
                ("workflowName", workflow.Name),
                ("path", path ?? ""),
                ("method", Request.Method),
                ("hasSecret", hasSecret),
                ("bodyChars", body.Length),
                ("executionId", pending.Id)),
            ct);

        return Accepted(new { workflowId = workflow.Id, executionId = pending.Id, message = "Triggered" });
    }

    private bool RequireSecretEnabled()
    {
        // Default-on semantic: missing key → true. Matches the Phase-3 hardening contract
        // shared by RestApi:BlockPrivateNetworks, FileSystemOperation:RejectTraversal,
        // StartProgram:DisallowShellExecute, and Remote:RequireWinRmSsl.
        var raw = _config["Webhook:RequireSecret"];
        if (string.IsNullOrWhiteSpace(raw)) return true;
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void RecordReject(string reason)
    {
        ApiMetrics.WebhookRequests.Add(1,
            new KeyValuePair<string, object?>("result", "rejected"),
            new KeyValuePair<string, object?>("reason", reason));
    }

    private static NotFoundObjectResult HiddenReject(string reason)
    {
        RecordReject(reason);
        return new NotFoundObjectResult(new { message = "Webhook endpoint not found" });
    }

    // Param names must be dot-free for the {{step.param.NAME}} template regex.
    // Query/header sub-keys and mapped field names flatten to [A-Za-z0-9_-].
    private static string SanitizeKey(string k)
    {
        if (k.Length > MaxForwardedHeaderKeyLen) k = k[..MaxForwardedHeaderKeyLen];
        return new(k.Select(c => (char.IsLetterOrDigit(c) || c == '_' || c == '-') ? c : '_').ToArray());
    }

    // fieldMappings caps: same spirit as the header/query forwarding caps — bounded
    // count + bounded per-value length so a hostile payload can't bloat the variable bag.
    private const int MaxFieldMappings = 32;
    private const int MaxMappedValueLen = 4096;

    private static readonly HashSet<string> ReservedParamNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "webhookBody", "webhookMethod", "webhookPath",
    };

    /// <summary>
    /// Evaluates the trigger's <c>fieldMappings</c> ([{name, path}]) against the decoded
    /// JSON body. Same Newtonsoft JSONPath dialect and depth cap as <c>jsonQuery</c>;
    /// a non-JSON body or a non-matching path degrades to an empty value (never a reject —
    /// the webhook fired legitimately, mapping is a convenience layer).
    /// </summary>
    private static Dictionary<string, string> ExtractFieldMappings(
        JsonElement mappings, string body, ILogger logger, Guid workflowId)
    {
        var result = new Dictionary<string, string>();

        Newtonsoft.Json.Linq.JToken root;
        try
        {
            using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(body)) { MaxDepth = 64 };
            root = Newtonsoft.Json.Linq.JToken.ReadFrom(reader);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            logger.LogWarning(
                "Webhook fieldMappings skipped for workflow {WorkflowId}: body is not valid JSON.", workflowId);
            return result;
        }

        var count = 0;
        foreach (var entry in mappings.EnumerateArray())
        {
            if (count >= MaxFieldMappings) break;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
            var jsonPath = entry.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(jsonPath)) continue;

            var key = SanitizeKey(name.Trim());
            // __-prefixed names are engine-reserved (mirror startWorkflow's guard); the
            // webhook* system keys and already-mapped names must not be shadowed.
            if (key.Length == 0 || key.StartsWith("__", StringComparison.Ordinal)
                || ReservedParamNames.Contains(key) || result.ContainsKey(key))
            {
                continue;
            }
            count++;

            var value = "";
            try
            {
                var token = root.SelectToken(jsonPath);
                value = token switch
                {
                    null => "",
                    Newtonsoft.Json.Linq.JValue { Type: Newtonsoft.Json.Linq.JTokenType.Null } => "",
                    Newtonsoft.Json.Linq.JValue { Type: Newtonsoft.Json.Linq.JTokenType.String } v =>
                        v.Value as string ?? "",
                    Newtonsoft.Json.Linq.JContainer c => c.ToString(Newtonsoft.Json.Formatting.None),
                    _ => token.ToString(),
                };
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // e.g. "path returned multiple tokens" for a single-token SelectToken —
                // degrade to empty, mirroring jsonQuery's defensive posture.
                value = "";
            }

            result[key] = value.Length <= MaxMappedValueLen ? value : value[..MaxMappedValueLen];
        }

        return result;
    }
}
