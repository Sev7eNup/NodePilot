using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Ai;
using NodePilot.Api.Configuration;
using NodePilot.Api.Controllers;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;
using System.Security.Claims;
using Xunit;

namespace NodePilot.Api.Tests.Ai;

/// <summary>
/// Regression guard for the DI shape behind <c>LLM_NO_ACTIVE_PROFILE</c>.
///
/// <para>Resolving the LLM connection can fail (no profile configured, or the active id names
/// none). If that resolution happened during <b>construction</b> — as it would with a
/// container-registered <see cref="ILlmClient"/> built from <c>factory.Create(null)</c> — the
/// failure would land while ASP.NET was building the controller, i.e. <i>before</i> the action's
/// gate ever ran. The operator would get an opaque 500 instead of the 503 that tells them what to
/// fix. These tests build a <b>real</b> service provider and drive the real controller.</para>
/// </summary>
public sealed class LlmDependencyResolutionTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNodePilotAi(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        return services.BuildServiceProvider();
    }

    private static readonly Dictionary<string, string?> EnabledWithoutProfile = new()
    {
        ["Llm:Enabled"] = "true",
    };

    private static T WithOperator<T>(T controller) where T : ControllerBase
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Operator"), new Claim(ClaimTypes.Name, "tester")], "TestAuth"));
        var ctx = new DefaultHttpContext { User = principal };
        ctx.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return controller;
    }

    [Fact]
    public void AiServices_ResolveEvenWithoutAnActiveProfile()
    {
        // The services must be constructible; only the CALL may fail.
        using var sp = BuildProvider(EnabledWithoutProfile);
        using var scope = sp.CreateScope();

        scope.ServiceProvider.Invoking(p => p.GetRequiredService<ScriptGenerationService>()).Should().NotThrow();
        scope.ServiceProvider.Invoking(p => p.GetRequiredService<WorkflowGenerationService>()).Should().NotThrow();
        scope.ServiceProvider.Invoking(p => p.GetRequiredService<WorkflowAssistantService>()).Should().NotThrow();
    }

    [Fact]
    public async Task GenerateScript_WithoutActiveProfile_Returns503NotAnUnhandledFailure()
    {
        using var sp = BuildProvider(EnabledWithoutProfile);
        using var scope = sp.CreateScope();
        var controller = WithOperator(new AiController(
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LlmOptions>>(),
            scope.ServiceProvider.GetRequiredService<ScriptGenerationService>(),
            scope.ServiceProvider.GetRequiredService<WorkflowGenerationService>(),
            new CapturingAuditWriter(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AiController>.Instance));

        var result = await controller.GenerateScript(
            new GenerateScriptRequest("hi", Guid.NewGuid(), "step-1", [], null), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        System.Text.Json.JsonSerializer.Serialize(obj.Value).Should().Contain(LlmAvailability.NoActiveProfileCode);
    }

    [Fact]
    public async Task GenerateWorkflow_WithoutActiveProfile_Returns503()
    {
        using var sp = BuildProvider(EnabledWithoutProfile);
        using var scope = sp.CreateScope();
        var controller = WithOperator(new AiController(
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LlmOptions>>(),
            scope.ServiceProvider.GetRequiredService<ScriptGenerationService>(),
            scope.ServiceProvider.GetRequiredService<WorkflowGenerationService>(),
            new CapturingAuditWriter(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AiController>.Instance));

        var result = await controller.GenerateWorkflow(
            new GenerateWorkflowRequest("build me a workflow"), CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        System.Text.Json.JsonSerializer.Serialize(obj.Value).Should().Contain(LlmAvailability.NoActiveProfileCode);
    }

    [Fact]
    public async Task ScriptGeneration_CalledWithoutAGate_FailsAsLlmExceptionNotNullReference()
    {
        // The service layer's own contract: if a caller skips the gate, the failure is still the
        // classified LlmException the controllers know how to map — never an NRE from a half-built
        // client.
        using var sp = BuildProvider(EnabledWithoutProfile);
        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ScriptGenerationService>();

        var act = async () =>
        {
            await foreach (var _ in svc.StreamAsync(
                new GenerateScriptRequest("hi", Guid.NewGuid(), "step-1", [], null), CancellationToken.None)) { }
        };

        (await act.Should().ThrowAsync<LlmException>()).Which.Message.Should().Contain("No active LLM profile");
    }
}
