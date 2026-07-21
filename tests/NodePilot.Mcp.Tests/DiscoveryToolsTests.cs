using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

[Collection("EnvMutating")]
public sealed class DiscoveryToolsTests
{
    private static string Json(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public async Task WhoAmI_ReturnsUsernameAndRole()
    {
        using var api = new TestApi();
        api.Server
            .Given(Request.Create().WithPath("/api/auth/me").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = Guid.NewGuid(),
                username = "admin",
                role = "Admin",
            }));

        var tools = new DiscoveryTools(api.Client(), TestApi.Config());
        var result = await tools.WhoAmI();

        var json = Json(result);
        json.Should().Contain("\"username\":\"admin\"");
        json.Should().Contain("\"role\":\"Admin\"");
        json.Should().Contain("\"authenticated\":true");
    }

    [Fact]
    public async Task WhoAmI_Unauthorized_ThrowsActionableError()
    {
        using var api = new TestApi();
        api.Server
            .Given(Request.Create().WithPath("/api/auth/me").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));

        var tools = new DiscoveryTools(api.Client(token: null), TestApi.Config());
        var act = async () => await tools.WhoAmI();

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("np auth login");
    }

    [Fact]
    public void GetSafetyStatus_DefaultGate_ListsDestructiveAsBlocked()
    {
        // Default (env unset) → destructive tools are blocked.
        var prev = Environment.GetEnvironmentVariable("NODEPILOT_MCP_ALLOW_DESTRUCTIVE");
        Environment.SetEnvironmentVariable("NODEPILOT_MCP_ALLOW_DESTRUCTIVE", null);
        try
        {
            using var api = new TestApi();
            var tools = new DiscoveryTools(api.Client(), TestApi.Config());

            var json = Json(tools.GetSafetyStatus());
            json.Should().Contain("\"allowDestructive\":false");
            // The destructive tools that actually exist today are reported blocked.
            json.Should().Contain("cancel_all_executions");
            json.Should().Contain("test_step");
            json.Should().Contain("delete_workflow");
            json.Should().Contain("force_unlock_workflow");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NODEPILOT_MCP_ALLOW_DESTRUCTIVE", prev);
        }
    }
}
