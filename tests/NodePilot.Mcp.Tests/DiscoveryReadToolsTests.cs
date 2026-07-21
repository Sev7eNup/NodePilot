using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

public sealed class DiscoveryReadToolsTests
{
    private static DiscoveryTools Tools(TestApi api) => new(api.Client(), TestApi.Config());

    [Fact]
    public void ListActivityTypes_InProc_IncludesRunScriptAndTriggers()
    {
        using var api = new TestApi();
        var json = JsonSerializer.Serialize(Tools(api).ListActivityTypes());
        json.Should().Contain("runScript");
        json.Should().Contain("manualTrigger");
        json.Should().Contain("\"isTrigger\":true");
    }

    [Fact]
    public void ListActivityTypes_FilterByCategory_OnlyTriggers()
    {
        using var api = new TestApi();
        var json = JsonSerializer.Serialize(Tools(api).ListActivityTypes(category: "Trigger"));
        json.Should().Contain("manualTrigger");
        json.Should().NotContain("\"type\":\"runScript\"");
    }

    [Fact]
    public void GetActivityConfigReference_KnownType_ReturnsConfigKeys()
    {
        using var api = new TestApi();
        var json = JsonSerializer.Serialize(Tools(api).GetActivityConfigReference("runScript"));
        json.Should().Contain("script");
        json.Should().Contain("successExitCodes");
    }

    [Fact]
    public void GetActivityConfigReference_UnknownType_ReturnsDocumentedTypes()
    {
        using var api = new TestApi();
        var json = JsonSerializer.Serialize(Tools(api).GetActivityConfigReference("doesNotExist"));
        json.Should().Contain("\"found\":false");
        json.Should().Contain("documentedTypes");
    }

    [Fact]
    public async Task ValidateCron_ReturnsNextFires()
    {
        using var api = new TestApi();
        api.Server
            .Given(Request.Create().WithPath("/api/triggers/schedule/next-fires").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                fires = new[] { DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(2) },
                summary = "Every hour",
            }));

        var json = JsonSerializer.Serialize(await Tools(api).ValidateCron("0 0 * * * ?"));
        json.Should().Contain("\"valid\":true");
        json.Should().Contain("Every hour");
    }
}
