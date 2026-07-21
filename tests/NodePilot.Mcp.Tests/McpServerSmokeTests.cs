using System.Linq;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NodePilot.Mcp.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

/// <summary>
/// End-to-end smoke test: launches the real server process and drives it over stdio.
/// Proves the MCP stack works end-to-end AND that the destructive-tool gate keeps delete_*
/// out of tools/list by default.
/// </summary>
public sealed class McpServerSmokeTests
{
    [Fact(Timeout = 60_000)]
    public async Task Server_ListsSafeToolsAndRejectsInsecureApi_ButHidesDestructiveTools()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        using var api = new TestApi();
        api.Server
            .Given(Request.Create().WithPath("/api/auth/me").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = Guid.NewGuid(),
                username = "admin",
                role = "Admin",
            }));

        await using var client = await McpServerProcess.ConnectAsync(api.Url, token: "test-token", cts.Token);

        // tools/list — safe tools present, destructive gated out (env flag unset for the child).
        var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
        var names = tools.Select(t => t.Name).ToHashSet();

        names.Should().Contain("whoami");
        names.Should().Contain("list_workflows");
        names.Should().Contain("get_workflow");
        names.Should().Contain("get_safety_status");
        names.Should().NotContain("delete_workflow");
        names.Should().NotContain("force_unlock_workflow");
        names.Should().NotContain("test_step", "step tests execute real activities and are gated as destructive");

        // The real process must refuse the HTTP test backend before sending its bearer token.
        // ApiClientFactory_RejectsInsecureServerUrl proves the HTTPS-specific cause, while
        // DiscoveryToolsTests.WhoAmI_ReturnsUsernameAndRole keeps the API round-trip covered.
        var result = await client.CallToolAsync("whoami", cancellationToken: cts.Token);
        result.IsError.Should().BeTrue();
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        text.Should().Contain("whoami");
        api.Server.LogEntries.Should().NotContain(entry => entry.RequestMessage.Path == "/api/auth/me");

        // resources/list — all three explicitly registered resources are served.
        var resources = await client.ListResourcesAsync(cancellationToken: cts.Token);
        var uris = resources.Select(r => r.Uri).ToHashSet();
        uris.Should().Contain("nodepilot://activity-catalog");
        uris.Should().Contain("nodepilot://activity-config-reference");
        uris.Should().Contain("nodepilot://styleguide");

        // resources/read — each returns non-empty text content.
        foreach (var uri in new[] { "nodepilot://activity-catalog", "nodepilot://activity-config-reference", "nodepilot://styleguide" })
        {
            var read = await client.ReadResourceAsync(uri, cancellationToken: cts.Token);
            var content = string.Concat(read.Contents.OfType<TextResourceContents>().Select(c => c.Text));
            content.Trim().Should().NotBeEmpty($"resource {uri} should return text");
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Server_WithDestructiveEnabled_RegistersGatedTools()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        using var api = new TestApi();

        await using var client = await McpServerProcess.ConnectAsync(api.Url, token: "test-token", cts.Token, allowDestructive: true);

        var names = (await client.ListToolsAsync(cancellationToken: cts.Token)).Select(t => t.Name).ToHashSet();
        names.Should().Contain("cancel_all_executions");   // gated tool now visible
        names.Should().Contain("test_step");              // real activity execution is gated too
        names.Should().Contain("whoami");                   // safe tools still present
    }
}
