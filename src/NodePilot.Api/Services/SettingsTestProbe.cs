using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.DirectoryServices.Protocols;
using System.Security.Principal;
using Microsoft.Extensions.Options;
using NodePilot.Ai;
using NodePilot.Api.Dtos.Settings;
using NodePilot.Api.Security.Ldap;

namespace NodePilot.Api.Services;

/// <summary>
/// "Test connection" probes for the Admin Settings UI. Each method accepts a fully-
/// populated section DTO (i.e. the values the operator just typed in the form, NOT
/// the persisted ones) so the operator can validate a candidate configuration before
/// committing it. Results are surfaced as a small JSON payload — no streaming, no
/// websocket — which keeps both the API and the UI modal as simple as possible.
///
/// <para>The probe deliberately does NOT touch the persistent settings file. It's a
/// read-only diagnostic; failure does not prevent a subsequent PUT, and success does
/// not imply the value will be persisted. The UI uses it strictly as "would this
/// work?" feedback alongside the form.</para>
/// </summary>
public sealed class SettingsTestProbe
{
    private readonly ILogger<SettingsTestProbe> _log;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<LdapOptions>? _ldapOptions;

    public SettingsTestProbe(
        ILogger<SettingsTestProbe> log,
        IHttpClientFactory httpFactory,
        IOptionsMonitor<LdapOptions>? ldapOptions = null)
    {
        _log = log;
        _httpFactory = httpFactory;
        _ldapOptions = ldapOptions;
    }

    public async Task<SettingsTestProbeResult> TestSmtpAsync(SmtpTestProbeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Settings is null) throw new ArgumentException("Smtp settings must be supplied.", nameof(request));

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new SmtpClient(request.Settings.Host, request.Settings.Port)
            {
                // H-2: respect the DTO's EnableSsl flag so the "Test connection" button
                // exercises the same transport behaviour the runtime EmailActivity will
                // use. Hardcoding false would lie to the operator about whether their
                // TLS configuration actually works.
                EnableSsl = request.Settings.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 10_000,
            };
            // SmtpClient credential semantics: null → anonymous bind. Empty/blank user
            // is treated as "don't authenticate" so a misconfigured form (user typed
            // a space) doesn't surface as 535-auth-failure noise.
            if (!string.IsNullOrWhiteSpace(request.Settings.Username))
            {
                client.Credentials = new NetworkCredential(
                    request.Settings.Username,
                    request.Settings.Password ?? string.Empty);
            }

            var to = string.IsNullOrWhiteSpace(request.ToAddress) ? request.Settings.From : request.ToAddress;
            using var message = new MailMessage(request.Settings.From, to)
            {
                Subject = "NodePilot SMTP connectivity test",
                Body = "This is an automated test sent from the NodePilot Admin Settings page. " +
                       "If you received it, the configured SMTP settings are working.",
            };

            await client.SendMailAsync(message, ct);

            sw.Stop();
            return SettingsTestProbeResult.Success(
                $"Test email accepted by {request.Settings.Host}:{request.Settings.Port} → {to}",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogWarning(ex, "SMTP test probe failed against {Host}:{Port}.", request.Settings.Host, request.Settings.Port);
            return SettingsTestProbeResult.Failure(
                $"SMTP probe failed: {ex.Message}",
                sw.Elapsed.TotalMilliseconds,
                ex.GetType().Name);
        }
    }

    /// <summary>
    /// LLM connectivity probe. Sends a HEAD/GET to <c>{BaseUrl}/models</c>, which every
    /// OpenAI-compatible endpoint (OpenAI Cloud, Ollama, LM Studio, vLLM, LocalAI,
    /// llama.cpp) supports as a cheap "are you there + does my key work" check. Avoids
    /// burning tokens on a chat-completion probe.
    /// </summary>
    public async Task<SettingsTestProbeResult> TestLlmAsync(LlmTestProbeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Settings is null) throw new ArgumentException("Llm settings must be supplied.", nameof(request));

        var sw = Stopwatch.StartNew();
        try
        {
            // Single guarded egress path (Finding 17): the SSRF-guarded named "Llm" client plus the
            // shared LlmEndpointGuard — same validation/guard the runtime LLM calls use. Rejects
            // cloud-metadata / non-http(s) BaseUrls before any connect.
            var client = _httpFactory.CreateClient(LlmHttpClient.Name);
            client.Timeout = TimeSpan.FromSeconds(Math.Min(request.Settings.TimeoutSeconds, 30));

            var url = LlmEndpointGuard.NormalizeAndValidateBaseUrl(request.Settings.BaseUrl) + "/models";
            using var probe = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(request.Settings.ApiKey))
                probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Settings.ApiKey);

            using var response = await client.SendAsync(probe, ct);
            sw.Stop();
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (body.Length > 200) body = body[..200] + "…";
                return SettingsTestProbeResult.Failure(
                    $"LLM endpoint returned {(int)response.StatusCode} {response.StatusCode}: {body}",
                    sw.Elapsed.TotalMilliseconds,
                    response.StatusCode.ToString());
            }
            return SettingsTestProbeResult.Success(
                $"LLM endpoint {request.Settings.BaseUrl} accepted the probe.",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogWarning(ex, "LLM test probe failed against {BaseUrl}.", request.Settings.BaseUrl);
            return SettingsTestProbeResult.Failure(
                $"LLM probe failed: {ex.Message}",
                sw.Elapsed.TotalMilliseconds,
                ex.GetType().Name);
        }
    }

    public Task<SettingsTestProbeResult> TestLdapAsync(LdapTestProbeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Settings is null) throw new ArgumentException("LDAP settings must be supplied.", nameof(request));
        return Task.Run(() => TestLdapCore(request.Settings, ct), ct);
    }

    private SettingsTestProbeResult TestLdapCore(LdapAuthenticationDto settings, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!settings.UseSsl)
            return SettingsTestProbeResult.Failure("LDAP probe refused: LDAPS is required.", 0, "TlsRequired");

        var password = settings.ServicePassword;
        if (password is "********" or "__unchanged__")
            password = _ldapOptions?.CurrentValue.ServicePassword;
        if (string.IsNullOrWhiteSpace(settings.ServiceBindDn) || string.IsNullOrWhiteSpace(password))
            return SettingsTestProbeResult.Failure(
                "LDAP probe requires service-bind credentials so sync/offboarding can be verified.",
                0,
                "ServiceBindRequired");

        try
        {
            var candidate = new LdapOptions
            {
                Server = settings.Server,
                Port = settings.Port,
                Endpoints = settings.Endpoints,
            };
            var endpoints = LdapEndpoint.Resolve(candidate);
            if (endpoints.Count == 0)
                return SettingsTestProbeResult.Failure("No LDAP endpoint configured.", 0, "Configuration");

            var successfulEndpoints = new List<string>();
            var failedEndpoints = new List<string>();
            foreach (var endpoint in endpoints)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var identifier = new LdapDirectoryIdentifier(
                        endpoint.Host, endpoint.Port, fullyQualifiedDnsHostName: false, connectionless: false);
                    using var connection = new LdapConnection(identifier)
                    {
                        AuthType = AuthType.Basic,
                        Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.BindTimeoutSeconds, 1, 60)),
                    };
                    connection.SessionOptions.ProtocolVersion = 3;
                    connection.SessionOptions.SecureSocketLayer = true;
                    connection.Bind(new NetworkCredential(settings.ServiceBindDn, password));
                    var probe = new SearchRequest(
                        settings.BaseDn,
                        "(objectClass=*)",
                        SearchScope.Base,
                        "distinguishedName");
                    _ = (SearchResponse)connection.SendRequest(probe);

                    var userProbe = (SearchResponse)connection.SendRequest(new SearchRequest(
                        settings.ServiceBindDn,
                        "(objectClass=user)",
                        SearchScope.Base,
                        "objectSid", "tokenGroups", "userAccountControl"));
                    if (userProbe.Entries.Count != 1)
                        throw new InvalidOperationException("Service-bind account could not be read as an AD user object.");
                    var entry = userProbe.Entries[0];
                    if (!entry.Attributes.Contains("objectSid"))
                        throw new InvalidOperationException("Service-bind account is missing readable objectSid.");
                    var sidValues = entry.Attributes["objectSid"].GetValues(typeof(byte[]));
                    if (sidValues.Length == 0 || sidValues[0] is not byte[] sidBytes)
                        throw new InvalidOperationException("Service-bind objectSid is empty or malformed.");
                    _ = new SecurityIdentifier(sidBytes, 0);
                    if (!entry.Attributes.Contains("tokenGroups"))
                        throw new InvalidOperationException("Service-bind account tokenGroups is not readable.");
                    successfulEndpoints.Add($"{endpoint.Host}:{endpoint.Port}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failedEndpoints.Add($"{endpoint.Host}:{endpoint.Port} ({ex.Message})");
                    _log.LogWarning(ex, "LDAP test probe failed against {Host}:{Port}.", endpoint.Host, endpoint.Port);
                }
            }

            sw.Stop();
            if (failedEndpoints.Count == 0)
            {
                return SettingsTestProbeResult.Success(
                    $"LDAPS, BaseDn, objectSid and tokenGroups checks succeeded on every endpoint: {string.Join(", ", successfulEndpoints)}.",
                    sw.Elapsed.TotalMilliseconds);
            }
            return SettingsTestProbeResult.Failure(
                $"LDAP validation failed on {failedEndpoints.Count}/{endpoints.Count} endpoint(s): {string.Join("; ", failedEndpoints)}",
                sw.Elapsed.TotalMilliseconds,
                "EndpointValidationFailed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return SettingsTestProbeResult.Failure(
                $"LDAP probe failed: {ex.Message}",
                sw.Elapsed.TotalMilliseconds,
                ex.GetType().Name);
        }
    }
}

public sealed record SmtpTestProbeRequest(SmtpSettingsDto Settings, string? ToAddress);
public sealed record LlmTestProbeRequest(LlmSettingsDto Settings);
public sealed record LdapTestProbeRequest(LdapAuthenticationDto Settings);

public sealed record SettingsTestProbeResult(bool Ok, string Message, double DurationMs, string? ErrorKind)
{
    public static SettingsTestProbeResult Success(string message, double durationMs) =>
        new(true, message, durationMs, null);

    public static SettingsTestProbeResult Failure(string message, double durationMs, string errorKind) =>
        new(false, message, durationMs, errorKind);
}
