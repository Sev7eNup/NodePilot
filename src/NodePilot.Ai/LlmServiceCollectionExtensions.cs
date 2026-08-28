using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
// Both namespaces above define an ILogger: the connect guard uses the Microsoft abstraction, while
// the boot-time messages in this file use Serilog's static Log.
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace NodePilot.Ai;

/// <summary>
/// SSRF guard applied at TCP-connect time. The literal-host check in
/// <see cref="LlmEndpointGuard.IsCloudMetadataEndpoint"/> only catches a configured
/// <c>Llm:BaseUrl</c> that already names a metadata endpoint, not a hostname that resolves to
/// <c>169.254.169.254</c> at connect time. Loopback and private IPs stay allowed here, unlike in
/// the RestApi guard, because local LLM endpoints are the common case for this feature.
/// </summary>
internal static class LlmConnectGuard
{
    /// <summary>
    /// Deadline for name resolution and the TCP connect together, the part of the handshake this
    /// callback owns.
    ///
    /// <para>Kept separate from the per-call <c>TimeoutSeconds</c>, which is an answer budget that
    /// operators set to minutes for slow local models: failing reachability on its own deadline is
    /// what tells an unreachable endpoint apart from a slow one. A constant rather than a knob,
    /// because 15 s is far beyond a healthy DNS lookup or TCP handshake.</para>
    /// </summary>
    internal static readonly TimeSpan ConnectPhaseTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectTimeout"/> for the LLM handler. Covers everything up to
    /// a usable connection, including the TLS handshake, which this callback cannot cover: it
    /// returns the raw transport stream and the handler negotiates TLS on top of it.
    ///
    /// <para>Larger than <see cref="ConnectPhaseTimeout"/> so the two never race: DNS and TCP
    /// always fail on their own deadline. Anything that trips this one has already connected at
    /// the TCP level and is stuck in the handshake, and <see cref="LlmHttpTransport"/> relies on
    /// that ordering to name the stage.</para>
    /// </summary>
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Log category for the connect diagnostics. Named rather than tied to a type so an operator
    /// can raise just this one to Debug (<c>Serilog:MinimumLevel:Override</c>) without turning on
    /// debug logging for the whole AI stack.
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

        // Logging the resolved set separates a stale AAAA record from a name that resolves
        // elsewhere under the service account's DNS suffixes; both look identical from outside.
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
/// (<see cref="LlmHttpClient.Name"/>) with its own <see cref="SocketsHttpHandler"/> instead of the
/// shared <c>"NodePilot"</c> HTTP pipeline, whose RestApi SSRF guard would block localhost
/// endpoints (e.g. Ollama on <c>127.0.0.1:11434</c>).
/// </summary>
public static class LlmServiceCollectionExtensions
{
    public static IServiceCollection AddNodePilotAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        services.Configure<AiKnowledgeOptions>(configuration.GetSection(AiKnowledgeOptions.SectionName));

        // Fail fast when a configured profile BaseUrl points at a known cloud-metadata IP. Only
        // applies when Llm:Enabled=true, so an untouched AI config block never blocks startup.
        // Uses the same helper as the settings boot-validator, so an accepted save always boots.
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
                // Disabled so the per-request timeout comes only from the linked CTS in
                // OpenAiCompatibleLlmClient (CancelAfter Llm:TimeoutSeconds). The .NET default of
                // 100s would otherwise abort any slower request first and report it as the
                // configured timeout expiring.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                // Local endpoints (Ollama, llama.cpp) may speak plaintext HTTP on literal
                // loopback. LlmEndpointGuard rejects HTTP everywhere else; HTTPS certificate
                // validation is the unmodified SocketsHttpHandler default.
                //
                // Proxying is decided per request by LlmConfiguredProxy from Llm:Proxy:*, not
                // here: this handler is built once per handler lifetime, so reading the config at
                // this point would make the whole Llm settings section restart-required. The
                // default (Llm:Proxy:Mode=Off) bypasses every destination, so there is no proxy
                // auto-discovery unless an operator opts in.
                UseProxy = true,
                Proxy = sp.GetRequiredService<LlmConfiguredProxy>(),
                AllowAutoRedirect = false,
                // Bounds establishing a connection, TLS handshake included — the one phase the
                // ConnectCallback below cannot cover, because it hands back the raw transport
                // stream and the handler negotiates TLS on top of it. Without it a stalled
                // handshake runs against the per-call answer budget instead.
                ConnectTimeout = LlmConnectGuard.HandshakeTimeout,
                // SSRF guard at TCP-connect time. Closes the DNS-rebinding window between
                // IsCloudMetadataEndpoint (literal-host check at boot) and the outbound connect
                // of each request. With a proxy in the path this callback sees the proxy
                // endpoint, not the LLM host; see LlmConfiguredProxy.
                ConnectCallback = (ctx, ct) => LlmConnectGuard.ConnectAsync(
                    ctx, sp.GetRequiredService<ILoggerFactory>().CreateLogger(LlmConnectGuard.LoggerCategory), ct),
            });

        services.AddSingleton<PromptCatalog>();
        services.AddSingleton<IChatToolRegistry, WorkflowChatToolRegistry>(); // read-only, stateless
        // No scoped ILlmClient registration: Create() throws when no active profile is configured,
        // and a container-level registration would resolve during controller construction, before
        // the action's Enabled/active-profile gate, turning a clean 503 into a DI failure. Every
        // consumer injects the factory and calls Create() at use time.
        services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
        services.AddScoped<ScriptGenerationService>();
        services.AddScoped<WorkflowGenerationService>();
        services.AddScoped<WorkflowAssistantService>();

        // Global "AI Chat" knowledge assistant: the docs and source readers are singletons (pure
        // file IO over the live AiKnowledgeOptions roots) and the tool registry is a stateless
        // singleton; the operational reader is DB-scoped and registered in the API host.
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
