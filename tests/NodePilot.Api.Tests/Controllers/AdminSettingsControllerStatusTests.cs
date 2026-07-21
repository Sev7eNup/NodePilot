using NodePilot.Ai;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodePilot.Api.Ai;
using NodePilot.Api.Configuration;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos.Settings;
using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Services;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Options;
using NodePilot.Scheduler.Options;
using NodePilot.Telemetry;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// PR1 surface: status endpoint. PR4 added full GET/PUT — those have a dedicated
/// test file (<c>AdminSettingsControllerSectionTests</c>); this file stays focused on
/// the status-only assertions so the smaller test surface keeps the regression cause
/// obvious when the file does fail.
/// </summary>
public sealed class AdminSettingsControllerStatusTests : IDisposable
{
    private readonly string _tempDir;

    public AdminSettingsControllerStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "np-controller-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

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

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    private sealed class NoopClusterState : IClusterStateProvider
    {
        public bool IsLeader => true;
        public string NodeId => "test-node";
        public DateTime? LeaseExpiresAt => null;
        public long LeaseEpoch => 0;
        public DateTime? LastSuccessfulRenewAt => null;
        public event Action<long>? OnLeadershipAcquired { add { } remove { } }
        public event Action? OnLeadershipLost           { add { } remove { } }
    }

    private (AdminSettingsController controller, RuntimeOverridesWriter writer) NewController()
    {
        var path = Path.Combine(_tempDir, "appsettings.runtime.json");
        var writer = new RuntimeOverridesWriter(path, NullLogger<RuntimeOverridesWriter>.Instance);
        var cfg = new ConfigurationBuilder().Build();
        var controller = new AdminSettingsController(
            writer,
            cfg,
            new NullProtector(),
            NoopAuditWriter.Instance,
            new SettingsTestProbe(NullLogger<SettingsTestProbe>.Instance, new StubHttpFactory()),
            new StaticOptionsMonitor<SmtpOptions>(new SmtpOptions()),
            new StaticOptionsMonitor<LlmOptions>(new LlmOptions()),
            new StaticOptionsMonitor<RetentionOptions>(new RetentionOptions()),
            new StaticOptionsMonitor<LdapOptions>(new LdapOptions()),
            new StaticOptionsMonitor<WindowsAuthOptions>(new WindowsAuthOptions()),
            new StaticOptionsMonitor<NodePilotTelemetryOptions>(new NodePilotTelemetryOptions()),
            new StaticOptionsMonitor<AiKnowledgeOptions>(new AiKnowledgeOptions()),
            new NoopClusterState());
        return (controller, writer);
    }

    [Fact]
    public void GetStatus_FileMissing_ReturnsCleanState()
    {
        var (controller, writer) = NewController();
        var result = controller.GetStatus();
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var body = ok!.Value as SettingsStatusResponse;
        body.Should().NotBeNull();
        body!.OverridesPath.Should().Be(writer.OverridesPath);
        body.RestartRequired.Should().BeFalse();
        body.RestartRequiredFor.Should().BeEmpty();
        body.RestartRequiredSince.Should().BeNull();
        body.LastSavedAt.Should().BeNull();
        body.LastSavedBy.Should().BeNull();
    }

    [Fact]
    public void GetStatus_AfterRestartMark_FlagsBanner()
    {
        var (controller, writer) = NewController();
        var t = DateTimeOffset.UtcNow;
        writer.MarkRestartRequired(new[] { "Smtp", "Llm" }, t);

        var result = controller.GetStatus();
        var body = ((OkObjectResult)result.Result!).Value as SettingsStatusResponse;
        body!.RestartRequired.Should().BeTrue();
        body.RestartRequiredFor.Should().BeEquivalentTo(new[] { "Llm", "Smtp" });
        body.RestartRequiredSince!.Value.Should().BeCloseTo(t, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void GetStatus_AfterClear_BannerGone()
    {
        var (controller, writer) = NewController();
        writer.MarkRestartRequired(new[] { "Smtp" }, DateTimeOffset.UtcNow);
        writer.ClearRestartMarker();

        var body = ((OkObjectResult)controller.GetStatus().Result!).Value as SettingsStatusResponse;
        body!.RestartRequired.Should().BeFalse();
        body.RestartRequiredFor.Should().BeEmpty();
    }

    [Fact]
    public void GetStatus_SurfacesLastSaveMetadata()
    {
        var (controller, writer) = NewController();
        writer.RecordLastSave("admin@example.com", DateTimeOffset.UtcNow);

        var body = ((OkObjectResult)controller.GetStatus().Result!).Value as SettingsStatusResponse;
        body!.LastSavedBy.Should().Be("admin@example.com");
        body.LastSavedAt.Should().NotBeNull();
    }
}
