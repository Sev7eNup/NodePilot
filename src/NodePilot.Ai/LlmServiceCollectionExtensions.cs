using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
// Both namespaces above carry an ILogger; the connect guard wants the abstraction the rest of the
// stack injects, while the boot-time messages in this file stay on Serilog's static Log.
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace NodePilot.Ai;

/// <summary>
/// L-4 (security audit 2026-05-15): the literal-host SSRF check in
/// <see cref="LlmEndpointGuard.IsCloudMetadataEndpoint"/> only fires when the
/// configured <c>Llm:BaseUrl</c> already names a metadata endpoint. It does nothing against
/// a hostname that resolves to <c>169.254.169.254</c> at TCP-connect time (DNS rebinding,
/// or simply a misconfigured DNS pointing internal-* names at metadata IPs). This callback
/// re-applies the rule at connect time. Unlike the RestApi guard, we deliberately
/// <i>allow</i> loopback/private IPs — local LLM endpoints (Ollama on 127.0.0.1:11434, LM
/// Studio on 127.0.0.1:1234) are the common production case for this feature.
/// </summary>
internal static class LlmConnectGuard
{
    /// <summary>
    /// Deadline for name resolution and the TCP connect together — the part of the handshake this
    /// callback owns.
    ///
    /// <para><b>Why a separate budget at all.</b> The per-call timeout (<c>TimeoutSeconds</c>) is
    /// an <i>answer</i> budget: a local model chewing on a long prompt legitimately needs minutes,
    /// which is why operators set it to 300+. Reaching the endpoint is not that kind of work. With
    /// one shared budget, an endpoint that never completes its handshake burned the whole thing —
    /// a profile at 360 s sat there for six minutes and then reported "did not respond", the same
    /// sentence a slow model produces. Failing the reachability phase early, and separately, is
    /// what lets the two be told apart at all.</para>
    ///
    /// <para>A constant rather than an operator knob: 15 s is far beyond any healthy DNS lookup or
    /// TCP handshake (Windows gives up on an unanswered SYN after ~21 s on its own), so there is
    /// nothing to tune here — a value that needs raising means the network is broken, and the
    /// error now says so.</para>
    /// </summary>
    internal static readonly TimeSpan ConnectPhaseTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectTimeout"/> for the LLM handler. Covers everything up to
    /// a usable connection, which — unlike this callback — <b>includes the TLS handshake</b>: the
    /// callback returns the raw transport stream and the handler negotiates TLS on top of it.
    ///
    /// <para>Larger than <see cref="ConnectPhaseTimeout"/> on purpose, so the two never race: DNS
    /// and TCP always fail on their own, named deadline. Anything that trips <i>this</i> one has
    /// therefore already connected at the TCP level and is stuck in the handshake — an endpoint
    /// demanding a client certificate, an SNI mismatch, or a middlebox that accepts the connection
    /// and never speaks TLS. <see cref="LlmHttpTransport"/> relies on that ordering to name the
    /// stage.</para>
    /// </summary>
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Log category for the connect diagnostics. Named rather than tied to a type so an operator
    /// can raise just this one to Debug (<c>Serilog:MinimumLevel:Override</c>) when an endpoint is
    /// unreachable, without turning on debug logging for the whole AI stack.
    /// </summary>
    internal const string LoggerCategory = "NodePilot.Ai.LlmConnect";

    internal static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
        => ConnectAsync(ctx, logger: null, ct);

    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext ctx,
        ILogger? logger,
        CancellationToken ct)
    {
        var endPoint = ctx.DnsEndPoint;
        var host = endPoint.Host;
        var port = endPoint.Port;

        // One deadline over both phases. Linked to the caller's token so a cancelled request still
        // aborts immediately; the two are told apart below by asking which one fired.
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(ct);
        phase.CancelAfter(ConnectPhaseTimeout);

        var stopwatch = Stopwatch.StartNew();
        IPAddress[] resolved;
        if (IPAddress.TryParse(host, out var direct))
        {
            resolved = new[] { direct };
        }
        else
        {
            try
            {
                resolved = await Dns.GetHostAddressesAsync(host, phase.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException(
                    $"LLM endpoint DNS: resolving '{host}' did not finish within {ConnectPhaseTimeout.TotalSeconds:0}s. " +
                    "The name server did not answer — this is name resolution, not the LLM.");
            }
            catch (SocketException ex)
            {
                throw new IOException(
                    $"LLM endpoint DNS: '{host}' could not be resolved ({ex.SocketErrorCode}). " +
                    "Check the name, the DNS suffix search list, and that this host uses the resolver that knows it.",
                    ex);
            }
        }
        var dnsElapsed = stopwatch.ElapsedMilliseconds;

        var allowed = new List<IPAddress>(resolved.Length);
        foreach (var ip in resolved)
        {
            if (IsLinkLocal(ip))
                continue; // 169.254/16 (cloud metadata) and IPv6 link-local — never allowed.
            allowed.Add(ip);
        }

        if (allowed.Count == 0)
            throw new IOException(
                $"LLM SSRF guard rejected every resolved address for host '{host}': link-local addresses " +
                "(169.254/16 incl. cloud-metadata, IPv6 fe80::/10) are not allowed for the LLM endpoint.");

        // The resolved set is the single most useful thing to know when an endpoint "works from my
        // machine" but not from the service: a stale AAAA record, or a name that resolves somewhere
        // else entirely under the service account's DNS suffixes, both look identical from outside.
        logger?.LogDebug(
            "LLM connect: {Host}:{Port} resolved to {Addresses} in {DnsMs} ms.",
            host, port, string.Join(", ", allowed), dnsElapsed);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed.ToArray(), port, phase.Token).ConfigureAwait(false);
            logger?.LogDebug(
                "LLM connect: TCP to {Endpoint} established in {TotalMs} ms; TLS (if any) is negotiated next.",
                socket.RemoteEndPoint, stopwatch.ElapsedMilliseconds);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new IOException(
                $"LLM endpoint TCP: no answer from {host}:{port} within {ConnectPhaseTimeout.TotalSeconds:0}s " +
                $"(tried {string.Join(", ", allowed)}). The connection attempt was dropped rather than refused, " +
                "which is what a firewall or a network segment boundary looks like from here.");
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new IOException(
                $"LLM endpoint TCP: {host}:{port} refused the connection ({ex.SocketErrorCode}, " +
                $"tried {string.Join(", ", allowed)}). Something answered — check the port and that the LLM is listening.",
                ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsLinkLocal(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] == 169 && bytes[1] == 254;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            if (ip.IsIPv4MappedToIPv6) return IsLinkLocal(ip.MapToIPv4());
        }
        return false;
    }
}

/// <summary>
/// DI wiring for the AI assistant endpoints. Binds <see cref="LlmOptions"/> from the
/// <c>Llm:*</c> configuration section and registers a dedicated named HttpClient
/// (<see cref="LlmHttpClient.Name"/>) with its own fresh <see cref="SocketsHttpHandler"/> —
/// deliberately NOT the shared <c>"NodePilot"</c> HTTP pipeline, because that one's RestApi SSRF
/// guard would block localhost endpoints (e.g. Ollama on <c>127.0.0.1:11434</c>).
/// </summary>
public static class LlmServiceCollectionExtensions
{
    public static IServiceCollection AddNodePilotAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        services.Configure<AiKnowledgeOptions>(configuration.GetSection(AiKnowledgeOptions.SectionName));

        // Fail fast when a configured profile BaseUrl points at a known cloud-metadata IP.
        // Only applies when Llm:Enabled=true — a default/unused config block must never block
        // startup, otherwise operators who never touched the AI settings couldn't boot their
        // instance at all. Same helper the settings boot-validator uses, so an accepted save can
        // never produce a config that refuses to boot.
        var enabled = configuration.GetValue<bool>($"{LlmOptions.SectionName}:Enabled");
        var endpointIssues = LlmProfileValidation.ValidateProfileEndpoints(configuration)
            .Concat(LlmProfileValidation.ValidateProxy(configuration))
            .ToList();
        if (endpointIssues.Count > 0)
            throw new InvalidOperationException(string.Join(" ", endpointIssues.Select(i => i.Message)));

        // Singleton: it is handed to the primary handler, which outlives any scope, and it holds
        // the cached custom WebProxy.
        services.AddSingleton<LlmConfiguredProxy>();

        services.AddHttpClient(LlmHttpClient.Name, client =>
            {
                // The per-request timeout is enforced in OpenAiCompatibleLlmClient via a linked
                // CTS (CancelAfter Llm:TimeoutSeconds). Without this line, HttpClient.Timeout
                // would stay at the .NET default of 100s and would abort EVERY slower request
                // first — surfacing as a misleading "LLM endpoint didn't respond within
                // {TimeoutSeconds}s" even though the configured timeout (e.g. 3600s for slow
                // local models) was never actually reached. Disabled here so only the CTS
                // controls the timeout.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                // Local endpoints (Ollama, llama.cpp) speak plaintext HTTP on 127.0.0.1.
                // Cloud endpoints speak HTTPS — the default SocketsHttpHandler validates that
                // normally. No forcing HTTPS.
                //
                // Proxying is decided per request by LlmConfiguredProxy from Llm:Proxy:*, NOT
                // here: this handler is built once per handler lifetime, so reading the config at
                // this point would make the whole Llm settings section restart-required. The
                // default (Llm:Proxy:Mode=Off) bypasses every destination, which is exactly the
                // direct connection this client made before proxy support existed — no proxy
                // auto-discovery unless an operator opts in.
                UseProxy = true,
                Proxy = sp.GetRequiredService<LlmConfiguredProxy>(),
                AllowAutoRedirect = false,
                // Bounds establishing a connection, TLS handshake included — the one phase the
                // ConnectCallback below cannot cover, because it hands back the raw transport
                // stream and the handler negotiates TLS on top of it. Without it a handshake that
                // stalls (client-certificate demand, SNI mismatch, a middlebox that accepts the
                // socket and never speaks TLS) runs against the per-call answer budget instead,
                // which operators legitimately set to minutes for slow local models.
                ConnectTimeout = LlmConnectGuard.HandshakeTimeout,
                // L-4: SSRF guard at TCP-connect time. Closes the DNS-rebinding window
                // between IsCloudMetadataEndpoint (literal-host check at boot) and the
                // actual outbound connect on every request. NB: with a proxy in the path this
                // callback sees the proxy endpoint, not the LLM host — see LlmConfiguredProxy
                // for why that trade-off is accepted here.
                ConnectCallback = (ctx, ct) => LlmConnectGuard.ConnectAsync(
                    ctx, sp.GetRequiredService<ILoggerFactory>().CreateLogger(LlmConnectGuard.LoggerCategory), ct),
            });

        services.AddSingleton<PromptCatalog>();
        services.AddSingleton<IChatToolRegistry, WorkflowChatToolRegistry>(); // read-only, stateless
        // Deliberately no scoped ILlmClient registration: Create() throws when no active profile
        // is configured, and a container-level registration would resolve during controller
        // construction — i.e. BEFORE the action's Enabled/active-profile gate, turning a clean 503
        // into a DI failure. Every consumer injects the factory and calls Create() at use time.
        services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
        services.AddScoped<ScriptGenerationService>();
        services.AddScoped<WorkflowGenerationService>();
        services.AddScoped<WorkflowAssistantService>();

        // Global "AI Chat" knowledge assistant: docs/source readers are singletons (pure file IO over
        // the live AiKnowledgeOptions roots); the operational reader is DB-scoped and registered in the
        // API host. The tool registry is a stateless singleton.
        services.AddSingleton<Knowledge.IDocsKnowledgeReader, Knowledge.DocsKnowledgeReader>();
        services.AddSingleton<Knowledge.ISourceCodeKnowledgeReader, Knowledge.SourceCodeKnowledgeReader>();
        services.AddSingleton<Knowledge.IKnowledgeToolRegistry, Knowledge.KnowledgeChatToolRegistry>();
        services.AddScoped<Knowledge.KnowledgeAssistantService>();

        if (enabled)
        {
            var profileCount = configuration.GetSection(LlmProfileValidation.ProfilesKey).GetChildren().Count();
            var activeId = configuration[$"{LlmOptions.SectionName}:ActiveProfileId"];
            if (LlmProfileValidation.HasResolvableActiveProfile(configuration))
            {
                Log.Information(
                    "AI assistant: Llm:Enabled=true, {ProfileCount} profile(s), active={ActiveProfileId}, BaseUrl={BaseUrl}, Model={Model}.",
                    profileCount, activeId,
                    configuration[$"{LlmProfileValidation.ProfilesKey}:{activeId}:BaseUrl"],
                    configuration[$"{LlmProfileValidation.ProfilesKey}:{activeId}:Model"]);
            }
            else
            {
                Log.Warning(
                    "AI assistant: Llm:Enabled=true but no active profile resolves ({ProfileCount} profile(s) configured, Llm:ActiveProfileId='{ActiveProfileId}'). "
                    + "AI endpoints will answer 503 LLM_NO_ACTIVE_PROFILE until a profile is selected under Settings.",
                    profileCount, activeId);
            }
        }

        return services;
    }
}
