using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodePilot.Engine.Options;
using NodePilot.Engine.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Security;

public sealed class ProxyBypassOverrideTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void RemovedBypassDoesNotSurviveInHandlerOrDestinationPolicy(int path, bool empty)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(Json(new { RestApi = new { Proxy = new { Enabled = true, Address = "http://proxy.example:8080", BypassList = new[] { "keep.example", "removed.example" } } } }))
            .AddJsonStream(Json(new { RestApi = new { Proxy = new { BypassList = empty ? Array.Empty<string>() : ["keep.example"] } } }))
            .Build();
        var collection = new ServiceCollection();
        collection.AddNodePilotEngineOptions(config);
        using var services = collection.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<RestApiProxyOptions>>();
        var removed = new Uri("https://removed.example/resource");
        var keep = new Uri("https://keep.example/resource");
        if (path == 0)
        {
            using var handler = RestApiHttpClientProvider.BuildDefaultHandler(options.Value, config);
            Assert.False(handler.Proxy!.IsBypassed(removed));
            Assert.Equal(!empty, handler.Proxy.IsBypassed(keep));
        }
        else
        {
            var provider = new RestApiHttpClientProvider(new StubHttpClientFactory(), config, path == 1 ? options : null);
            using var step = JsonDocument.Parse("{}");
            Assert.True(provider.UsesProxyForDestination(step.RootElement, removed));
            Assert.Equal(empty, provider.UsesProxyForDestination(step.RootElement, keep));
        }
    }

    private static MemoryStream Json(object value) => new(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
}
