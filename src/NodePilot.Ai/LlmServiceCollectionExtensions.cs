using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace NodePilot.Ai;

/// <summary>
/// L-4 (security audit 2026-05-15): the literal-host SSRF check in
/// <see cref="LlmServiceCollectionExtensions.IsCloudMetadataEndpoint"/> only fires when the
/// configured <c>Llm:BaseUrl</c> already names a metadata endpoint. It does nothing against
/// a hostname that resolves to <c>169.254.169.254</c> at TCP-connect time (DNS rebinding,
/// or simply a misconfigured DNS pointing internal-* names at metadata IPs). This callback
/// re-applies the rule at connect time. Unlike the RestApi guard, we deliberately
/// <i>allow</i> loopback/private IPs — local LLM endpoints (Ollama on 127.0.0.1:11434, LM
/// Studio on 127.0.0.1:1234) are the common production case for this feature.
/// </summary>
internal static class LlmConnectGuard
{
    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var endPoint = ctx.DnsEndPoint;
        var host = endPoint.Host;
        var port = endPoint.Port;

        IPAddress[] resolved;
        if (IPAddress.TryParse(host, out var direct))
        {
            resolved = new[] { direct };
        }
        else
        {
            resolved = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }

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

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed.ToArray(), port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
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
        var endpointIssues = LlmProfileValidation.ValidateProfileEndpoints(configuration);
        if (endpointIssues.Count > 0)
            throw new InvalidOperationException(string.Join(" ", endpointIssues.Select(i => i.Message)));

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
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Local endpoints (Ollama, llama.cpp) speak plaintext HTTP on 127.0.0.1.
                // Cloud endpoints speak HTTPS — the default SocketsHttpHandler validates that
                // normally. No forcing HTTPS, no proxy auto-discovery (local endpoints should
                // ignore the Windows system proxy).
                UseProxy = false,
                AllowAutoRedirect = false,
                // L-4: SSRF guard at TCP-connect time. Closes the DNS-rebinding window
                // between IsCloudMetadataEndpoint (literal-host check at boot) and the
                // actual outbound connect on every request.
                ConnectCallback = LlmConnectGuard.ConnectAsync,
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
