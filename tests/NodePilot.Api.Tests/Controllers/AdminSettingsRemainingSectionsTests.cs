using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Ai;
using NodePilot.Api.Configuration;
using NodePilot.Api.Controllers;
using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Services;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Options;
using NodePilot.Scheduler.Options;
using NodePilot.Telemetry;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Round-trips the five settings sections the existing section tests never touch:
/// AiKnowledge and the four hardening toggles (FileSystemOperation, SqlActivity,
/// StartProgram, Webhook). Each asserts the documented default that applies when the
/// override file has no entry — a missing key must read as hardened, not as false.
/// </summary>
public sealed class AdminSettingsRemainingSectionsTests : IDisposable
{
    private readonly string _tempDir;

    public AdminSettingsRemainingSectionsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "np-admin-rest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- AiKnowledge

    [Fact]
    public void GetSection_AiKnowledge_ReturnsTheDocumentedDefaults()
    {
        var (controller, _, _) = NewController();

        var payload = Section(controller, "AiKnowledge");

        payload.GetProperty("docsEnabled").GetBoolean().Should().BeTrue();
        payload.GetProperty("operationalEnabled").GetBoolean().Should().BeTrue();
        payload.GetProperty("sourceCodeEnabled").GetBoolean().Should().BeFalse();
        payload.GetProperty("dbEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PutSection_AiKnowledge_PersistsEverySwitchAndPath()
    {
        var (controller, writer, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("AiKnowledge");

        var result = await controller.PutSection("AiKnowledge", Body("""
            {
              "Enabled": true,
              "DocsEnabled": false,
              "OperationalEnabled": false,
              "SourceCodeEnabled": true,
              "DbEnabled": true,
              "DocsRootPath": "E:/docs",
              "SourceCodeRootPath": "E:/src",
              "DocsMaxFileBytes": 8192,
              "DocsMaxResults": 5,
              "SourceCodeMaxFileBytes": 16384,
              "SourceCodeMaxResults": 7
            }
            """), TestContext.Current.CancellationToken);

        result.Should().BeOfType<OkObjectResult>();
        var persisted = File.ReadAllText(writer.OverridesPath);
        persisted.Should().Contain("SourceCodeEnabled");
        persisted.Should().Contain("E:/docs");
        persisted.Should().Contain("E:/src");
    }

    [Fact]
    public async Task PutSection_AiKnowledge_ClearedPathsArePersistedAsExplicitNull()
    {
        // Explicit null, not "key absent": the override file replaces the section wholesale,
        // so an absent key would silently fall back to the appsettings value.
        var (controller, writer, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("AiKnowledge");

        await controller.PutSection("AiKnowledge", Body("""
            {"Enabled": true, "DocsRootPath": null, "SourceCodeRootPath": null}
            """), TestContext.Current.CancellationToken);

        var persisted = File.ReadAllText(writer.OverridesPath);
        persisted.Should().Contain("DocsRootPath");
        persisted.Should().Contain("null");
    }

    // ---------------------------------------------------------------- hardening toggles

    [Theory]
    [InlineData("FileSystemOperation", "rejectTraversal")]
    [InlineData("SqlActivity", "requireConnectionRef")]
    [InlineData("StartProgram", "disallowShellExecute")]
    [InlineData("Webhook", "requireSecret")]
    public void GetSection_HardeningToggle_DefaultsToOnWhenUnset(string section, string property)
    {
        var (controller, _, _) = NewController();

        Section(controller, section).GetProperty(property).GetBoolean()
            .Should().BeTrue($"a missing {section} override must read as hardened");
    }

    [Theory]
    [InlineData("FileSystemOperation", "RejectTraversal")]
    [InlineData("SqlActivity", "RequireConnectionRef")]
    [InlineData("StartProgram", "DisallowShellExecute")]
    [InlineData("Webhook", "RequireSecret")]
    public async Task PutSection_HardeningToggle_CanBeRelaxedExplicitly(string section, string property)
    {
        var (controller, writer, audit) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag(section);

        var result = await controller.PutSection(
            section, Body($$"""{"{{property}}": false}"""), TestContext.Current.CancellationToken);

        result.Should().BeOfType<OkObjectResult>();
        File.ReadAllText(writer.OverridesPath).Should().Contain(property);
        audit.Calls.Should().NotBeEmpty("relaxing a hardening flag must leave an audit trail");
    }

    [Fact]
    public async Task PutSection_FileSystemOperation_PersistsTheAllowedRootsList()
    {
        var (controller, writer, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("FileSystemOperation");

        var result = await controller.PutSection("FileSystemOperation", Body("""
            {"RejectTraversal": true, "AllowedRoots": ["E:/data", "E:/inbox"]}
            """), TestContext.Current.CancellationToken);

        result.Should().BeOfType<OkObjectResult>();
        var persisted = File.ReadAllText(writer.OverridesPath);
        persisted.Should().Contain("E:/data");
        persisted.Should().Contain("E:/inbox");
    }

    [Fact]
    public void GetSection_FileSystemOperation_AllowedRootsDefaultToAnEmptyList()
    {
        var (controller, _, _) = NewController();

        Section(controller, "FileSystemOperation").GetProperty("allowedRoots")
            .EnumerateArray().Should().BeEmpty();
    }

    // ---------------------------------------------------------------- helpers

    private static JsonElement Section(AdminSettingsController controller, string name)
    {
        var ok = controller.GetSection(name).Should().BeOfType<OkObjectResult>().Subject;
        // GetSection wraps the DTO in a SettingsSectionResponse (payload + etag + effective
        // source); re-serialize and drill into the payload so the assertions can read it by
        // property name without referencing every settings DTO type here.
        return JsonSerializer.SerializeToElement(ok.Value, CamelCase).GetProperty("payload");
    }

    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private (AdminSettingsController controller, RuntimeOverridesWriter writer, CapturingAuditWriter audit)
        NewController()
    {
        var writer = new RuntimeOverridesWriter(
            Path.Combine(_tempDir, "appsettings.runtime.json"),
            NullLogger<RuntimeOverridesWriter>.Instance);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var audit = new CapturingAuditWriter();

        var controller = new AdminSettingsController(
            writer,
            cfg,
            new PassthroughProtector(),
            audit,
            new SettingsTestProbe(NullLogger<SettingsTestProbe>.Instance, StubHttpFactory()),
            new StaticOptionsMonitor<SmtpOptions>(new SmtpOptions()),
            new StaticOptionsMonitor<LlmOptions>(new LlmOptions()),
            new StaticOptionsMonitor<RetentionOptions>(new RetentionOptions()),
            new StaticOptionsMonitor<LdapOptions>(new LdapOptions()),
            new StaticOptionsMonitor<WindowsAuthOptions>(new WindowsAuthOptions()),
            new StaticOptionsMonitor<NodePilotTelemetryOptions>(new NodePilotTelemetryOptions()),
            new StaticOptionsMonitor<AiKnowledgeOptions>(new AiKnowledgeOptions()),
            new NoopClusterState())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (controller, writer, audit);
    }

    private sealed class PassthroughProtector : ISecretProtector
    {
        public string ProviderName => "Test";
        public byte[] Protect(string plaintext) => Encoding.UTF8.GetBytes("ENC:" + plaintext);
        public string Unprotect(byte[] blob)
        {
            var text = Encoding.UTF8.GetString(blob);
            return text.StartsWith("ENC:", StringComparison.Ordinal) ? text[4..] : text;
        }
    }

    /// <summary>Factory whose clients answer every request with a bare 200 OK.</summary>
    private static StubHttpClientFactory StubHttpFactory() =>
        new(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
}
