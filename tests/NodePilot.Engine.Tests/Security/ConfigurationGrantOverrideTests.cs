using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodePilot.Core.Agents;
using NodePilot.Engine.Agents;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Engine.Tests.Security;

public sealed class ConfigurationGrantOverrideTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void McpReadApproval_RemovedGrantCannotReappearFromBaseConfiguration(bool empty)
    {
        var server = new AgentMcpServer { Id = Guid.NewGuid(), Enabled = true, UpdatedAt = DateTime.UtcNow };
        var first = new AgentMcpReadGrant { ServerId = server.Id, ToolName = "keep", ServerUpdatedAt = server.UpdatedAt.ToString("O"), ContractSha256 = new string('A', 64) };
        var removed = first with { ToolName = "removed" };
        var configuration = Layered(
            new { Agents = new { ReadOnlyMcpTools = new[] { first, removed } } },
            new { Agents = new { ReadOnlyMcpTools = empty ? Array.Empty<AgentMcpReadGrant>() : [first] } });
        var collection = new ServiceCollection();
        collection.AddNodePilotEngineOptions(configuration);
        using var services = collection.BuildServiceProvider();
        var policy = new AgentExternalReadPolicy(services.GetRequiredService<IOptionsMonitor<AgentOptions>>());

        Assert.Throws<UnauthorizedAccessException>(() => policy.AuthorizeMcpServer(server, "removed"));
        if (!empty) Assert.Equal(first, policy.AuthorizeMcpServer(server, "keep"));
        else Assert.Throws<UnauthorizedAccessException>(() => policy.AuthorizeMcpServer(server, "keep"));
    }

    [Theory]
    [InlineData("RestApi", false)]
    [InlineData("RestApi", true)]
    [InlineData("WaitForCondition", false)]
    [InlineData("WaitForCondition", true)]
    public void NetworkException_RemovedHostCannotReappearFromBaseConfiguration(string section, bool empty)
    {
        var configuration = Layered(
            new Dictionary<string, object> { [section] = new { AllowedHosts = new[] { "127.0.0.1", "127.0.0.2" } } },
            new Dictionary<string, object> { [section] = new { AllowedHosts = empty ? Array.Empty<string>() : ["127.0.0.1"] } });
        if (section == "RestApi")
        {
            Assert.Throws<InvalidOperationException>(() => NetworkGuard.ValidateUrl(configuration, "http://127.0.0.2/", requireAllAddresses: true));
            if (!empty) NetworkGuard.ValidateUrl(configuration, "http://127.0.0.1/", requireAllAddresses: true);
        }
        else
        {
            Assert.False(NetworkGuard.IsProbeHostAllowlisted(configuration, "127.0.0.2"));
            Assert.Equal(!empty, NetworkGuard.IsProbeHostAllowlisted(configuration, "127.0.0.1"));
        }
    }

    private static IConfigurationRoot Layered(object lower, object higher) => new ConfigurationBuilder()
        .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lower))))
        .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(higher))))
        .Build();
}
