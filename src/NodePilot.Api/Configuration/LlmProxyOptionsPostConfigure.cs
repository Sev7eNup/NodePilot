using Microsoft.Extensions.Options;
using NodePilot.Ai;
using NodePilot.Engine.Security;

namespace NodePilot.Api.Configuration;

/// <summary>Apply the host's atomic exception-list policy without making AI depend on Engine.</summary>
public sealed class LlmProxyOptionsPostConfigure(IConfiguration configuration) : IPostConfigureOptions<LlmOptions>
{
    public void PostConfigure(string? name, LlmOptions options)
        => options.Proxy.BypassList = ProviderAtomicList.Read<string>(configuration, "Llm:Proxy:BypassList") ?? [];
}
