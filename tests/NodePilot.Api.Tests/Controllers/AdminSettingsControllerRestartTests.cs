using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Ai;
using NodePilot.Api.Configuration;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos.Settings;
using NodePilot.Api.Hosting;
using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Services;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Options;
using NodePilot.Scheduler.Options;
using NodePilot.Telemetry;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class AdminSettingsControllerRestartTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "np-restart-tests-" + Guid.NewGuid().ToString("N"));

    public AdminSettingsControllerRestartTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class NullProtector : ISecretProtector
    {
        public string ProviderName => "Null";
        public byte[] Protect(string plaintext) => Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] blob) => Encoding.UTF8.GetString(blob);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public int StopCalls { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopCalls++;
    }

    private AdminSettingsController NewController(CapturingAuditWriter audit)
    {
        var writer = new RuntimeOverridesWriter(
            Path.Combine(_tempDir, "appsettings.runtime.json"), NullLogger<RuntimeOverridesWriter>.Instance);
        var controller = new AdminSettingsController(
            writer,
            new ConfigurationBuilder().Build(),
            new NullProtector(),
            audit,
            new SettingsTestProbe(NullLogger<SettingsTestProbe>.Instance, new StubHttpClientFactory()),
            new StaticOptionsMonitor<SmtpOptions>(new SmtpOptions()),
            new StaticOptionsMonitor<LlmOptions>(new LlmOptions()),
            new StaticOptionsMonitor<RetentionOptions>(new RetentionOptions()),
            new StaticOptionsMonitor<LdapOptions>(new LdapOptions()),
            new StaticOptionsMonitor<WindowsAuthOptions>(new WindowsAuthOptions()),
            new StaticOptionsMonitor<NodePilotTelemetryOptions>(new NodePilotTelemetryOptions()),
            new StaticOptionsMonitor<AiKnowledgeOptions>(new AiKnowledgeOptions()),
            new NoopClusterState());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test")),
            },
        };
        return controller;
    }

    private static ServiceRestartCoordinator NewCoordinator(FakeLifetime lifetime, ServiceRestartCapability capability)
        => new(lifetime, NullLogger<ServiceRestartCoordinator>.Instance, () => capability, TimeSpan.Zero, _ => { });

    [Fact]
    public async Task RestartService_NotAService_Returns409WithReasonAndNoAudit()
    {
        var audit = new CapturingAuditWriter();
        var lifetime = new FakeLifetime();
        var controller = NewController(audit);

        var result = await controller.RestartService(
            NewCoordinator(lifetime, ServiceRestartCapability.No("NOT_A_WINDOWS_SERVICE")));

        result.Should().BeOfType<ConflictObjectResult>();
        audit.Calls.Should().BeEmpty();
        lifetime.StopCalls.Should().Be(0);
    }

    [Fact]
    public async Task RestartService_Available_Returns202AndWritesAudit()
    {
        var audit = new CapturingAuditWriter();
        var coordinator = NewCoordinator(new FakeLifetime(), ServiceRestartCapability.Yes);
        var controller = NewController(audit);

        var result = await controller.RestartService(coordinator);

        result.Should().BeOfType<AcceptedResult>();
        coordinator.IsRestartScheduled.Should().BeTrue();
        audit.Calls.Should().ContainSingle(c => c.Action == AuditActions.SettingsServiceRestartRequested);
        audit.Calls[0].Details.Should().Contain("admin");
    }

    [Fact]
    public void GetStatus_WithoutCoordinator_ReportsRestartUnavailable()
    {
        var controller = NewController(new CapturingAuditWriter());

        var body = ((OkObjectResult)controller.GetStatus().Result!).Value as SettingsStatusResponse;

        body!.CanRestartService.Should().BeFalse();
        body.RestartUnavailableReason.Should().Be("NOT_A_WINDOWS_SERVICE");
    }

    [Fact]
    public void GetStatus_ServiceWithRecovery_ReportsRestartAvailable()
    {
        var controller = NewController(new CapturingAuditWriter());
        var coordinator = NewCoordinator(new FakeLifetime(), ServiceRestartCapability.Yes);

        var body = ((OkObjectResult)controller.GetStatus(coordinator).Result!).Value as SettingsStatusResponse;

        body!.CanRestartService.Should().BeTrue();
        body.RestartUnavailableReason.Should().BeNull();
    }

    [Fact]
    public void RestartService_IsAdminOnly()
    {
        // Operator and Viewer must never restart the service: the gate is the controller's
        // Admin-only role, and the action must not loosen it.
        var controllerAuth = typeof(AdminSettingsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        controllerAuth.Roles.Should().Be("Admin");

        var action = typeof(AdminSettingsController).GetMethod(nameof(AdminSettingsController.RestartService))!;
        action.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Should().BeEmpty();
        action.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Should().BeEmpty(
            "an action-level Authorize would be combined with, but could mask, the controller's Admin gate");
    }
}
