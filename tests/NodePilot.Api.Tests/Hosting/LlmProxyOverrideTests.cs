using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Ai;
using NodePilot.Api.Configuration;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class LlmProxyOverrideTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedBypassStaysRemovedAtStartupAndAfterHotReload(bool empty)
    {
        var lower = Path.GetTempFileName();
        var higher = Path.GetTempFileName();
        try
        {
            File.WriteAllText(lower, Json(new { Llm = new { Proxy = new { Mode = "Custom", Address = "http://proxy.example:8080", BypassList = new[] { "keep.example", "removed.example" } } } }));
            var replacement = Json(new { Llm = new { Proxy = new { BypassList = empty ? Array.Empty<string>() : ["keep.example"] } } });
            File.WriteAllText(higher, replacement);
            using var config = new ConfigurationManager();
            config.AddJsonFile(lower, optional: false, reloadOnChange: false);
            config.AddJsonFile(higher, optional: false, reloadOnChange: false);
            var collection = new ServiceCollection();
            collection.AddSingleton<IConfiguration>(config);
            collection.AddNodePilotAi(config);
            collection.ConfigureOptions<LlmProxyOptionsPostConfigure>();
            using var services = collection.BuildServiceProvider();
            var proxy = services.GetRequiredService<LlmConfiguredProxy>();
            var removed = new Uri("https://removed.example/v1/models");
            var keep = new Uri("https://keep.example/v1/models");
            Assert.False(proxy.IsBypassed(removed));
            Assert.Equal(!empty, proxy.IsBypassed(keep));

            File.WriteAllText(higher, Json(new { Llm = new { Proxy = new { BypassList = new[] { "keep.example", "removed.example" } } } }));
            ((IConfigurationRoot)config).Reload();
            Assert.True(proxy.IsBypassed(removed));
            File.WriteAllText(higher, replacement);
            ((IConfigurationRoot)config).Reload();
            Assert.False(proxy.IsBypassed(removed));
            Assert.Equal(!empty, proxy.IsBypassed(keep));
        }
        finally
        {
            File.Delete(lower);
            File.Delete(higher);
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
