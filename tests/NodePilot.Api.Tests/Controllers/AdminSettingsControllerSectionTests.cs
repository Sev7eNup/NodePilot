using NodePilot.Ai;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
/// PR4 surface tests — section GET/PUT with ETag, validation, audit, restart-marker.
/// The SMTP probe endpoint is covered separately (it talks to a real SmtpClient and
/// would need a full TCP mock to test).
/// </summary>
public sealed class AdminSettingsControllerSectionTests : IDisposable
{
    private readonly string _tempDir;

    public AdminSettingsControllerSectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "np-admin-section-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class PassthroughProtector : ISecretProtector
    {
        public string ProviderName => "Test";
        public byte[] Protect(string plaintext) =>
            Encoding.UTF8.GetBytes("ENC:" + plaintext);
        public string Unprotect(byte[] blob) =>
            Encoding.UTF8.GetString(blob).StartsWith("ENC:")
                ? Encoding.UTF8.GetString(blob).Substring(4)
                : throw new InvalidOperationException("Unknown blob.");
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    private (AdminSettingsController controller, RuntimeOverridesWriter writer, CapturingAuditWriter audit, IConfigurationRoot cfg) NewController(SmtpOptions? initialSmtp = null, LlmOptions? initialLlm = null, RetentionOptions? initialRetention = null, LdapOptions? initialLdap = null, WindowsAuthOptions? initialWindows = null)
    {
        var overridesPath = Path.Combine(_tempDir, "appsettings.runtime.json");
        var writer = new RuntimeOverridesWriter(overridesPath, NullLogger<RuntimeOverridesWriter>.Instance);
        var initial = initialSmtp ?? new SmtpOptions();
        var llm = initialLlm ?? new LlmOptions();
        var ret = initialRetention ?? new RetentionOptions();
        var ldap = initialLdap ?? new LdapOptions();
        var windows = initialWindows ?? new WindowsAuthOptions();

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:Host"]     = initial.Host,
                ["Smtp:Port"]     = initial.Port.ToString(),
                ["Smtp:From"]     = initial.From,
                ["Smtp:Username"] = initial.Username,
                ["Smtp:Password"] = initial.Password,
                ["Llm:Enabled"]   = llm.Enabled.ToString(),
                ["Llm:BaseUrl"]   = llm.BaseUrl,
                ["Llm:ApiKey"]    = llm.ApiKey,
                ["Llm:Model"]     = llm.Model,
                ["Retention:Executions:MaxAgeDays"] = ret.Executions.MaxAgeDays.ToString(),
                ["Retention:AuditLog:MaxAgeDays"]   = ret.AuditLog.MaxAgeDays.ToString(),
            })
            .Build();
        var audit = new CapturingAuditWriter();
        var probe = new SettingsTestProbe(NullLogger<SettingsTestProbe>.Instance, new StubHttpFactory());

        var controller = new AdminSettingsController(
            writer,
            cfg,
            new PassthroughProtector(),
            audit,
            probe,
            new StaticOptionsMonitor<SmtpOptions>(initial),
            new StaticOptionsMonitor<LlmOptions>(llm),
            new StaticOptionsMonitor<RetentionOptions>(ret),
            new StaticOptionsMonitor<LdapOptions>(ldap),
            new StaticOptionsMonitor<WindowsAuthOptions>(windows),
            new StaticOptionsMonitor<NodePilotTelemetryOptions>(new NodePilotTelemetryOptions()),
            new StaticOptionsMonitor<AiKnowledgeOptions>(new AiKnowledgeOptions()),
            new NoopClusterState());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return (controller, writer, audit, cfg);
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

    [Fact]
    public void GetSnapshot_ReturnsAllSchemaSections()
    {
        var (controller, _, _, _) = NewController();
        var result = controller.GetSnapshot();
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var list = ok!.Value as IReadOnlyList<object>;
        list.Should().NotBeNull();
        list!.Should().HaveCount(SettingsSchema.Sections.Length);
    }

    [Fact]
    public void GetSection_Llm_MasksApiKey_WhenSet()
    {
        var (controller, _, _, _) = NewController(initialLlm: new LlmOptions
        {
            Enabled = true, BaseUrl = "http://localhost:1234/v1", Model = "gpt", ApiKey = "real-api-key",
        });
        var result = controller.GetSection("Llm") as OkObjectResult;
        result.Should().NotBeNull();
        var payload = result!.Value!.GetType().GetProperty("Payload")!.GetValue(result.Value) as LlmSettingsDto;
        payload!.ApiKey.Should().Be("********");
        payload.Enabled.Should().BeTrue();
        payload.Model.Should().Be("gpt");
    }

    [Fact]
    public void GetSystemInfo_ReturnsBootstrapMetadata()
    {
        var (controller, writer, _, _) = NewController();
        var result = controller.GetSystemInfo();
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var info = ok!.Value as NodePilot.Api.Dtos.Settings.SystemInfoResponse;
        info.Should().NotBeNull();
        info!.OverridesPath.Should().Be(writer.OverridesPath);
        info.SecretsProvider.Should().Be("Test"); // PassthroughProtector reports "Test"
        info.ClusterNodeId.Should().Be("test-node");
        info.ClusterIsLeader.Should().BeTrue("single-node-mode NoopClusterState reports IsLeader=true");
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=5432;Database=np", "127.0.0.1")]
    [InlineData("Server=db.internal;Database=np;Trusted_Connection=True", "db.internal")]
    [InlineData("Data Source=:memory:", ":memory:")]
    [InlineData("Garbage;NoEquals;UrgentMaybe", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryExtractHost_FindsCommonTokens(string? cs, string? expected)
    {
        AdminSettingsController.TryExtractHost(cs).Should().Be(expected);
    }

    [Fact]
    public async Task PutSection_Retention_HappyPath_PersistsAllThreeSubsections()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Retention");

        var body = JsonDocument.Parse(
            "{\"Executions\":{\"Enabled\":true,\"MaxAgeDays\":7,\"IntervalMinutes\":30,\"BatchSize\":1000,\"ArchivePath\":\"\"},"
            + "\"AuditLog\":{\"Enabled\":true,\"MaxAgeDays\":180,\"IntervalMinutes\":720,\"BatchSize\":500,\"ArchivePath\":null},"
            + "\"WorkflowVersions\":{\"Enabled\":false,\"MaxVersionsPerWorkflow\":25,\"IntervalMinutes\":1440,\"BatchSize\":500}}"
        ).RootElement;
        var result = await controller.PutSection("Retention", body, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var fileContent = File.ReadAllText(writer.OverridesPath);
        fileContent.Should().Contain("\"MaxAgeDays\": 7");
        fileContent.Should().Contain("\"MaxVersionsPerWorkflow\": 25");
        // ArchivePath was the empty string → must be persisted as EXPLICIT JSON null
        // (Finding 7), not dropped. Without the explicit null an appsettings.json
        // ArchivePath value would silently re-activate after the next reload.
        var fileJson = JsonNode.Parse(fileContent)!.AsObject();
        var execs = fileJson["Retention"]!["Executions"]!.AsObject();
        execs.ContainsKey("ArchivePath").Should().BeTrue();
        execs["ArchivePath"].Should().BeNull();
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_RETENTION_UPDATED");
    }

    [Fact]
    public async Task PutSection_Retention_OutOfRange_Returns400()
    {
        var (controller, writer, _, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Retention");

        // MaxAgeDays=0 violates the [Range(1,3650)] guard; without it the sweeper would
        // delete every row on its next tick. The 400 must surface this BEFORE writing.
        var body = JsonDocument.Parse(
            "{\"Executions\":{\"Enabled\":true,\"MaxAgeDays\":0,\"IntervalMinutes\":60,\"BatchSize\":500},"
            + "\"AuditLog\":{\"Enabled\":true,\"MaxAgeDays\":365,\"IntervalMinutes\":720,\"BatchSize\":1000},"
            + "\"WorkflowVersions\":{\"Enabled\":true,\"MaxVersionsPerWorkflow\":50,\"IntervalMinutes\":1440,\"BatchSize\":500}}"
        ).RootElement;
        var result = await controller.PutSection("Retention", body, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        File.Exists(writer.OverridesPath).Should().BeFalse(
            "validation must fail before any file is written, otherwise a partially-saved override file could survive");
    }

    [Fact]
    public async Task PutSection_Llm_CloudMetadataBaseUrl_Returns400_NoFileWrite()
    {
        // Regression guard for Finding 2: without the LlmConfigBootValidator, this PUT
        // would persist the override, the service would write `appsettings.runtime.json`,
        // and the NEXT restart would fail with `SECURITY: Llm:BaseUrl …` — wedging the
        // process and forcing the operator to hand-edit the file.
        var (controller, writer, _, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Llm");

        var body = JsonDocument.Parse(
            "{\"Enabled\":true,\"BaseUrl\":\"http://169.254.169.254/v1\",\"Model\":\"gpt\",\"MaxTokens\":4096,\"TimeoutSeconds\":60,\"ApiKey\":null}"
        ).RootElement;
        var result = await controller.PutSection("Llm", body, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        File.Exists(writer.OverridesPath).Should().BeFalse(
            "validation must reject the save before any file is written — otherwise the next restart fails");
    }

    [Fact]
    public async Task PutSection_Llm_PersistsToolCallingFields()
    {
        // Without the wiring in SettingsSections, EnableToolCalling/ToolCallMaxDepth would be
        // lost when saving the LLM section (bug found: tool-calling wasn't reachable from Admin Settings).
        var (controller, writer, _, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Llm");

        var body = JsonDocument.Parse(
            "{\"Enabled\":true,\"BaseUrl\":\"http://localhost:1234/v1\",\"Model\":\"gpt\",\"MaxTokens\":4096,"
            + "\"TimeoutSeconds\":60,\"EnableToolCalling\":true,\"ToolCallMaxDepth\":6,\"ApiKey\":null}"
        ).RootElement;
        var result = await controller.PutSection("Llm", body, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var llm = JsonNode.Parse(File.ReadAllText(writer.OverridesPath))!["Llm"]!.AsObject();
        llm["EnableToolCalling"]!.GetValue<bool>().Should().BeTrue();
        llm["ToolCallMaxDepth"]!.GetValue<int>().Should().Be(6);
    }

    [Fact]
    public async Task PutSection_Llm_ToolCallMaxDepthOutOfRange_Returns400()
    {
        var (controller, writer, _, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Llm");

        var body = JsonDocument.Parse(
            "{\"Enabled\":true,\"BaseUrl\":\"http://localhost:1234/v1\",\"Model\":\"gpt\",\"MaxTokens\":4096,"
            + "\"TimeoutSeconds\":60,\"EnableToolCalling\":true,\"ToolCallMaxDepth\":99,\"ApiKey\":null}" // > [Range(1,10)]
        ).RootElement;
        var result = await controller.PutSection("Llm", body, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        File.Exists(writer.OverridesPath).Should().BeFalse();
    }

    [Fact]
    public async Task PutSection_Authentication_HappyPath_PersistsLdapAndWindows()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Authentication");

        var body = JsonDocument.Parse(@"{
            ""LocalLoginMode"": ""BreakGlassOnly"",
            ""SessionAbsoluteLifetimeHours"": 8,
            ""MaxAuthorizationStalenessMinutes"": 15,
            ""Ldap"": {
                ""Enabled"": true,
                ""Server"": ""dc01.firma.local"",
                ""Endpoints"": [""dc01.firma.local:636"", ""dc02.firma.local:636""],
                ""Port"": 636,
                ""UseSsl"": true,
                ""BaseDn"": ""DC=firma,DC=local"",
                ""UpnSuffix"": ""firma.local"",
                ""BindTimeoutSeconds"": 5,
                ""ServiceBindDn"": ""CN=svc-ldap,OU=Services,DC=firma,DC=local"",
                ""ServicePassword"": ""svc-secret"",
                ""DirectorySyncIntervalMinutes"": 5,
                ""AllowedGroupSids"": [""S-1-5-21-1-2-3-4567""],
                ""GlobalRoleMappings"": [
                    { ""GroupSid"": ""S-1-5-21-1-2-3-4567"", ""Role"": ""Admin"" }
                ],
                ""JitUserDefaultRootRole"": ""FolderViewer""
            },
            ""Windows"": { ""Enabled"": true, ""AllowNtlmFallback"": false, ""NtlmDisabledByPolicy"": true }
        }").RootElement;

        var result = await controller.PutSection("Authentication", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var fileContent = File.ReadAllText(writer.OverridesPath);
        fileContent.Should().Contain("dc01.firma.local");
        fileContent.Should().Contain("S-1-5-21-1-2-3-4567");
        fileContent.Should().Contain("enc:v1:");
        fileContent.Should().NotContain("svc-secret",
            "service password must never persist as plaintext — the encrypted blob is the only valid representation");
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_AUTHENTICATION_UPDATED");
    }

    [Fact]
    public async Task PutSection_Authentication_EncryptsBothScimRotationTokens()
    {
        var (controller, writer, _, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Authentication");
        var current = new string('c', 32);
        var previous = new string('p', 32);
        var body = JsonDocument.Parse($$"""
        {
          "LocalLoginMode": "BreakGlassOnly",
          "Ldap": { "Enabled": false },
          "Windows": { "Enabled": false },
          "Oidc": {
            "Enabled": true,
            "Authority": "https://idp.example.test/tenant",
            "ClientId": "nodepilot",
            "ClientSecret": "oidc-secret",
            "AllowedGroupIds": ["nodepilot-users"]
          },
          "Scim": {
            "Enabled": true,
            "Authority": "https://idp.example.test/tenant",
            "BearerToken": "{{current}}",
            "PreviousBearerToken": "{{previous}}"
          }
        }
        """).RootElement;

        var result = await controller.PutSection("Authentication", body, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var file = File.ReadAllText(writer.OverridesPath);
        file.Should().Contain("PreviousBearerToken");
        file.Should().NotContain(current);
        file.Should().NotContain(previous);
        file.Split("enc:v1:", StringSplitOptions.None).Length.Should().BeGreaterThanOrEqualTo(4,
            "OIDC plus both SCIM tokens must be encrypted");
    }

    [Fact]
    public async Task PutSection_Authentication_LdapEnabled_MissingBaseDn_Returns400()
    {
        // Cross-field IValidatableObject guard: enabling LDAP without the connection
        // essentials must reject before persisting, otherwise the first login attempt
        // would crash on a null BaseDn.
        var (controller, writer, _, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Authentication");

        var body = JsonDocument.Parse(@"{
            ""Ldap"": {
                ""Enabled"": true,
                ""Server"": ""dc01.firma.local"",
                ""Port"": 636,
                ""UseSsl"": true,
                ""BaseDn"": """",
                ""UpnSuffix"": ""firma.local"",
                ""BindTimeoutSeconds"": 5,
                ""GlobalRoleMappings"": []
            },
            ""Windows"": { ""Enabled"": false, ""AllowNtlmFallback"": false }
        }").RootElement;

        var result = await controller.PutSection("Authentication", body, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
        File.Exists(writer.OverridesPath).Should().BeFalse();
    }

    [Fact]
    public async Task PutSection_Llm_HappyPath_PersistsEncryptedApiKey()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Llm");

        var body = JsonDocument.Parse(
            "{\"Enabled\":true,\"BaseUrl\":\"http://127.0.0.1:1234/v1\",\"Model\":\"gpt-4o-mini\",\"MaxTokens\":4096,\"TimeoutSeconds\":60,\"ApiKey\":\"sk-real-secret\"}"
        ).RootElement;
        var result = await controller.PutSection("Llm", body, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var fileContent = File.ReadAllText(writer.OverridesPath);
        fileContent.Should().Contain("enc:v1:");
        fileContent.Should().NotContain("sk-real-secret",
            "API keys persisted to the override file must always go through the secret protector");
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_LLM_UPDATED");
    }

    [Fact]
    public void GetSection_Unknown_ReturnsNotFound()
    {
        var (controller, _, _, _) = NewController();
        var result = controller.GetSection("BogusSection");
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void GetSection_Smtp_MasksPassword_WhenSet()
    {
        var (controller, _, _, _) = NewController(new SmtpOptions { Host = "h", Port = 25, From = "a@b.c", Password = "secret123" });
        var result = controller.GetSection("Smtp") as OkObjectResult;
        result.Should().NotBeNull();
        var payload = result!.Value!.GetType().GetProperty("Payload")!.GetValue(result.Value) as SmtpSettingsDto;
        payload!.Password.Should().Be("********",
            "secrets must never leave the server in plaintext — even an admin shouldn't see persisted passwords in the read response");
    }

    [Fact]
    public void GetSection_Smtp_PasswordNull_WhenUnset()
    {
        var (controller, _, _, _) = NewController(new SmtpOptions { Host = "h", From = "a@b.c", Password = null });
        var result = controller.GetSection("Smtp") as OkObjectResult;
        var payload = result!.Value!.GetType().GetProperty("Payload")!.GetValue(result.Value) as SmtpSettingsDto;
        payload!.Password.Should().BeNull("no value configured → respond with null so the UI shows the 'no password' state, not 'asterisks'");
    }

    [Fact]
    public async Task PutSection_MissingIfMatch_Returns428()
    {
        var (controller, _, _, _) = NewController();
        var body = JsonDocument.Parse("{\"Host\":\"new.example.com\",\"Port\":587,\"From\":\"a@b.c\"}").RootElement;
        var result = await controller.PutSection("Smtp", body, CancellationToken.None);
        var status = result as ObjectResult;
        status!.StatusCode.Should().Be(StatusCodes.Status428PreconditionRequired);
    }

    [Fact]
    public async Task PutSection_WrongIfMatch_Returns412_WithCurrentSnapshot()
    {
        var (controller, _, _, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = "\"definitely-wrong-etag\"";
        var body = JsonDocument.Parse("{\"Host\":\"new.example.com\",\"Port\":587,\"From\":\"a@b.c\"}").RootElement;
        var result = await controller.PutSection("Smtp", body, CancellationToken.None);
        var status = result as ObjectResult;
        status!.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
        var anonymous = status.Value!.GetType().GetProperty("current")!.GetValue(status.Value);
        anonymous.Should().NotBeNull("412 must return the current snapshot so the UI can render a three-way merge");
    }

    [Fact]
    public async Task PutSection_InvalidPayload_Returns400()
    {
        var (controller, writer, _, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Smtp");
        var body = JsonDocument.Parse("{\"Host\":\"\",\"Port\":99999,\"From\":\"not-an-email\"}").RootElement;
        var result = await controller.PutSection("Smtp", body, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PutSection_HotReloadableSection_PersistsAndAudits_NoRestartMarker()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Smtp");

        var body = JsonDocument.Parse("{\"Host\":\"new.example.com\",\"Port\":587,\"From\":\"sender@example.com\",\"Username\":\"u\",\"Password\":\"plaintext-secret\"}").RootElement;
        var result = await controller.PutSection("Smtp", body, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        // File contents reflect the new values, with the password encrypted (enc:v1: prefix).
        var fileContent = File.ReadAllText(writer.OverridesPath);
        fileContent.Should().Contain("new.example.com");
        fileContent.Should().Contain("enc:v1:");
        fileContent.Should().NotContain("plaintext-secret",
            "the persisted file must never carry the plaintext secret; the audit log must not either");

        // Audit entry with the right code, redacted diff.
        audit.Calls.Should().ContainSingle()
            .Which.Action.Should().Be("SETTINGS_SMTP_UPDATED");
        audit.Calls[0].Details.Should().NotBeNullOrEmpty().And.NotContain("plaintext-secret");
        audit.Calls[0].Details.Should().Contain("\"***\"",
            "the redacted diff must use *** for secret fields, not the encrypted ciphertext (still sensitive in spirit)");

        // No restart marker — SMTP is hot-reloadable (SmtpNotificationSink + EmailActivity read
        // IOptionsMonitor<SmtpOptions>.CurrentValue per send), so the save takes effect immediately.
        var status = writer.ReadStatus();
        status.RestartRequired.Should().BeFalse();
        status.RestartRequiredFor.Should().NotContain("Smtp");
    }

    [Fact]
    public async Task PutSection_RestartRequiredSection_MarksRestart()
    {
        // Counterpart to the hot-reloadable case: a section whose consumers are boot-frozen
        // (ExecutionDispatchWorker queue/channel sizing is built once at boot) must still write
        // the restart marker so the UI surfaces the orange banner.
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("ExecutionDispatch");

        var body = JsonDocument.Parse("{\"capacity\":4096,\"workerCount\":128}").RootElement;
        var result = await controller.PutSection("ExecutionDispatch", body, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var fileContent = File.ReadAllText(writer.OverridesPath);
        fileContent.Should().Contain("4096");

        audit.Calls.Should().ContainSingle()
            .Which.Action.Should().Be("SETTINGS_EXECUTIONDISPATCH_UPDATED");

        var status = writer.ReadStatus();
        status.RestartRequired.Should().BeTrue();
        status.RestartRequiredFor.Should().Contain("ExecutionDispatch");
    }

    [Fact]
    public async Task PutSection_ClearPassword_PersistsExplicitJsonNull_ShadowsBaseProvider()
    {
        // Finding 7: a UI Clear must shadow the lower configuration layers, not just
        // remove the override row. Without this, an appsettings.json-defined password
        // would silently become effective again on the next reload.
        var (controller, writer, _, _) = NewController(
            initialSmtp: new SmtpOptions { Host = "h", Port = 25, From = "a@b.c", Password = "base-pass" });

        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Smtp");
        var body = JsonDocument.Parse(
            "{\"Host\":\"h\",\"Port\":25,\"From\":\"a@b.c\",\"Password\":null}"
        ).RootElement;
        var result = await controller.PutSection("Smtp", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        // File must contain an explicit `"Password": null` — NOT a missing key.
        var fileJson = JsonNode.Parse(File.ReadAllText(writer.OverridesPath))!.AsObject();
        var smtpSection = fileJson["Smtp"]!.AsObject();
        smtpSection.ContainsKey("Password").Should().BeTrue(
            "Clear must persist an explicit null override — without the key, appsettings.json's Password would silently reactivate");
        smtpSection["Password"].Should().BeNull("the explicit null is what shadows the lower-layer config");

        // Response payload renders the cleared field as null (no value), not "********".
        var fresh = ((OkObjectResult)result).Value!.GetType().GetProperty("Payload")!.GetValue(((OkObjectResult)result).Value) as SmtpSettingsDto;
        fresh!.Password.Should().BeNull(
            "the read-side must treat explicit-null and absent identically — both render as 'no value configured'");
    }

    [Fact]
    public async Task PutSection_EnvLockedField_StrippedFromPersistedSection()
    {
        // Finding 8: even if the UI submits a value for an env/cli-locked field
        // (e.g. because the form re-sends the whole section payload), the server must
        // drop that key before write. Otherwise the runtime file accumulates stale
        // shadow values that re-activate if the env var is later unset.
        var (controller, writer, _, cfg) = NewController();

        // Layer an env-source provider over the in-memory config so the EffectiveSource
        // detector classifies Smtp:Host as "env" — without doing this the detector
        // would label everything as "default" and the strip would be a no-op.
        const string envPrefix = "ADMIN_SETTINGS_TEST_";
        const string envKey = envPrefix + "Smtp__Host";
        Environment.SetEnvironmentVariable(envKey, "env-wins.example.com");
        try
        {
            // Rebuild the controller against a config root that observes the env-var.
            var probe = new SettingsTestProbe(NullLogger<SettingsTestProbe>.Instance, new StubHttpFactory());
            var envCfg = new ConfigurationBuilder()
                .AddInMemoryCollection(cfg.AsEnumerable())
                .AddEnvironmentVariables(envPrefix)
                .Build();
            var ctrl = new AdminSettingsController(
                writer, envCfg, new PassthroughProtector(),
                NoopAuditWriter.Instance, probe,
                new StaticOptionsMonitor<SmtpOptions>(new SmtpOptions()),
                new StaticOptionsMonitor<LlmOptions>(new LlmOptions()),
                new StaticOptionsMonitor<RetentionOptions>(new RetentionOptions()),
                new StaticOptionsMonitor<LdapOptions>(new LdapOptions()),
                new StaticOptionsMonitor<WindowsAuthOptions>(new WindowsAuthOptions()),
                new StaticOptionsMonitor<NodePilotTelemetryOptions>(new NodePilotTelemetryOptions()),
                new StaticOptionsMonitor<AiKnowledgeOptions>(new AiKnowledgeOptions()),
                new NoopClusterState());
            ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            ctrl.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Smtp");

            // Operator submits a host value — env var should win at runtime AND the
            // host field should be absent from the persisted file.
            var body = JsonDocument.Parse(
                "{\"Host\":\"ui-shadow\",\"Port\":2525,\"From\":\"a@b.c\",\"Password\":null}"
            ).RootElement;
            var result = await ctrl.PutSection("Smtp", body, CancellationToken.None);
            result.Should().BeOfType<OkObjectResult>();

            var fileJson = JsonNode.Parse(File.ReadAllText(writer.OverridesPath))!.AsObject();
            var smtpSection = fileJson["Smtp"]!.AsObject();
            smtpSection.ContainsKey("Host").Should().BeFalse(
                "env-locked Smtp:Host must NOT be persisted — otherwise a later env-var removal would silently reactivate this stale ui-shadow value");
            // Non-env keys still persist normally.
            smtpSection["Port"]!.GetValue<int>().Should().Be(2525);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // V2 sections (PR9-12): one regression test per section so a future change
    // that breaks the DTO round-trip surfaces immediately. Validation edges +
    // secret round-trips are covered by the dedicated tests above; these are the
    // "did anyone break the happy path" guards.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PutSection_Logging_HappyPath_PersistsFormatAndLevels()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Logging");

        var body = JsonDocument.Parse(@"{
            ""Format"": ""ecs-json"",
            ""LogLevel"": {
                ""Default"": ""Information"",
                ""Microsoft.AspNetCore"": ""Warning"",
                ""Microsoft.EntityFrameworkCore.Database.Command"": ""Warning"",
                ""Microsoft.EntityFrameworkCore.Database.Connection"": ""Warning"",
                ""Microsoft.EntityFrameworkCore.Infrastructure"": ""Warning""
            },
            ""StepDetail"": { ""Enabled"": true, ""MaxOutputChars"": 5000 },
            ""File"": { ""RetainedFileCountLimit"": 14, ""FileSizeLimitBytes"": 209715200, ""Async"": true },
            ""Redaction"": { ""Enabled"": true }
        }").RootElement;
        var result = await controller.PutSection("Logging", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var file = File.ReadAllText(writer.OverridesPath);
        file.Should().Contain("ecs-json");
        file.Should().Contain("Information");
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_LOGGING_UPDATED");
    }

    [Fact]
    public async Task PutSection_OpenTelemetry_HappyPath_EncryptsPrometheusSecrets()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("OpenTelemetry");

        var body = JsonDocument.Parse(@"{
            ""Enabled"": true,
            ""ServiceName"": ""np-test"",
            ""Environment"": ""prod"",
            ""RedactHostnames"": true,
            ""MetricExportIntervalSeconds"": 60,
            ""Otlp"": { ""Endpoint"": ""https://otlp.example.com:4317"", ""Protocol"": ""grpc"", ""Headers"": ""x-key=val"", ""BrowserEndpoint"": """" },
            ""Sampling"": { ""Mode"": ""ParentBasedTraceIdRatio"", ""Ratio"": 0.5 },
            ""Exporters"": { ""Traces"": true, ""Metrics"": true, ""Logs"": false, ""PrometheusScrape"": true, ""PrometheusScrapeAllowAnonymous"": false },
            ""TraceUi"": { ""UrlTemplate"": ""https://tempo/trace/{traceId}"", ""BackendName"": ""Tempo"" },
            ""Prometheus"": {
                ""QueryEndpoint"": ""https://prom.example.com"",
                ""Username"": ""scraper"",
                ""Password"": ""prom-pass-plaintext"",
                ""BearerToken"": ""bearer-plaintext"",
                ""TimeoutSeconds"": 15
            }
        }").RootElement;
        var result = await controller.PutSection("OpenTelemetry", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var file = File.ReadAllText(writer.OverridesPath);
        // Both secrets must encrypt — never persist plaintext.
        file.Should().Contain("enc:v1:");
        file.Should().NotContain("prom-pass-plaintext");
        file.Should().NotContain("bearer-plaintext");
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_OPENTELEMETRY_UPDATED");
    }

    [Fact]
    public async Task PutSection_Stats_HappyPath_PersistsBothFields()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Stats");
        var body = JsonDocument.Parse("{\"RefreshIntervalMinutes\":15,\"WindowDays\":14}").RootElement;
        var result = await controller.PutSection("Stats", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        File.ReadAllText(writer.OverridesPath).Should().Contain("\"RefreshIntervalMinutes\": 15");
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_STATS_UPDATED");
    }

    [Fact]
    public async Task PutSection_DbAdmin_HappyPath_PersistsAllThreeFields()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("DbAdmin");
        var body = JsonDocument.Parse(
            "{\"AllowWriteQueries\":true,\"QueryTimeoutSeconds\":60,\"QueryMaxRows\":5000}").RootElement;
        var result = await controller.PutSection("DbAdmin", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        var file = File.ReadAllText(writer.OverridesPath);
        file.Should().Contain("\"AllowWriteQueries\": true");
        file.Should().Contain("\"QueryTimeoutSeconds\": 60");
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_DBADMIN_UPDATED");
    }

    [Fact]
    public async Task PutSection_DbAdmin_Validation_RejectsOutOfRangeTimeout()
    {
        var (controller, writer, _, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("DbAdmin");
        // QueryTimeoutSeconds is constrained to [1, 600] via [Range(...)]; 9999 must fail validation.
        var body = JsonDocument.Parse(
            "{\"AllowWriteQueries\":false,\"QueryTimeoutSeconds\":9999,\"QueryMaxRows\":10000}").RootElement;
        var result = await controller.PutSection("DbAdmin", body, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PutSection_RestApi_HappyPath_EncryptsProxyPassword()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("RestApi");
        var body = JsonDocument.Parse(@"{
            ""BlockPrivateNetworks"": true,
            ""AllowedHosts"": [""api.internal.example"", ""10.20.30.40""],
            ""Proxy"": {
                ""Enabled"": true,
                ""Address"": ""http://proxy.firma.local:8080"",
                ""BypassList"": [""*.firma.local"", ""127.0.0.1""],
                ""Username"": ""proxyuser"",
                ""Password"": ""proxy-secret""
            }
        }").RootElement;
        var result = await controller.PutSection("RestApi", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        var file = File.ReadAllText(writer.OverridesPath);
        file.Should().Contain("enc:v1:");
        file.Should().NotContain("proxy-secret");
        file.Should().Contain("*.firma.local");
        file.Should().Contain("api.internal.example");
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_RESTAPI_UPDATED");
    }

    [Fact]
    public async Task PutSection_RestApi_RejectsNonHostAllowListEntries()
    {
        var (controller, writer, _, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("RestApi");
        var body = JsonDocument.Parse(@"{
            ""BlockPrivateNetworks"": true,
            ""AllowedHosts"": [""https://api.internal/path""],
            ""Proxy"": { ""Enabled"": false, ""Address"": """", ""BypassList"": [] }
        }").RootElement;

        var result = await controller.PutSection("RestApi", body, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PutSection_Security_HappyPath_PersistsAllowedHosts()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Security");
        var body = JsonDocument.Parse(@"{
            ""StrictAllowedHosts"": true,
            ""AllowedHosts"": ""nodepilot.firma.local;localhost""
        }").RootElement;
        var result = await controller.PutSection("Security", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        File.ReadAllText(writer.OverridesPath).Should().Contain("nodepilot.firma.local");
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_SECURITY_UPDATED");
    }

    [Fact]
    public async Task PutSection_ExternalTrigger_ClearsApiKey_WithExplicitNull()
    {
        // Regression for Finding 7 in the external-trigger path: clearing the API key must
        // disable the endpoint entirely (503), not fall back to a value from appsettings.json.
        var (controller, writer, _, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("ExternalTrigger");
        var body = JsonDocument.Parse("{\"ApiKey\":null}").RootElement;
        var result = await controller.PutSection("ExternalTrigger", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        var file = JsonNode.Parse(File.ReadAllText(writer.OverridesPath))!.AsObject();
        var section = file["ExternalTrigger"]!.AsObject();
        section.ContainsKey("ApiKey").Should().BeTrue();
        section["ApiKey"].Should().BeNull("explicit JSON null shadows any base-provider ApiKey value");
    }

    [Fact]
    public async Task PutSection_Engine_HappyPath_PersistsNestedConcurrency()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Engine");
        var body = JsonDocument.Parse(@"{
            ""Debug"": { ""MaxPauseMinutes"": 30 },
            ""MaxConcurrentExecutions"": { ""Global"": 10000, ""PerUser"": 3000 },
            ""MaxConcurrentSteps"": 1200,
            ""Runspace"": { ""MinRunspaces"": 512, ""MaxRunspaces"": 1024 }
        }").RootElement;
        var result = await controller.PutSection("Engine", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        var file = File.ReadAllText(writer.OverridesPath);
        file.Should().Contain("\"Global\": 10000");
        file.Should().Contain("\"MaxRunspaces\": 1024");
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_ENGINE_UPDATED");
    }

    [Fact]
    public async Task PutSection_Engine_MinGreaterThanMax_Returns400()
    {
        // IValidatableObject guard: Runspace.MinRunspaces > MaxRunspaces would crash the
        // PowerShell-pool builder at next boot. Catch it server-side as a 400.
        var (controller, writer, _, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Engine");
        var body = JsonDocument.Parse(@"{
            ""Debug"": { ""MaxPauseMinutes"": 10 },
            ""MaxConcurrentExecutions"": { ""Global"": 5000, ""PerUser"": 2000 },
            ""MaxConcurrentSteps"": 600,
            ""Runspace"": { ""MinRunspaces"": 1024, ""MaxRunspaces"": 256 }
        }").RootElement;
        var result = await controller.PutSection("Engine", body, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
        File.Exists(writer.OverridesPath).Should().BeFalse();
    }

    [Fact]
    public async Task PutSection_ExecutionDispatch_HappyPath()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("ExecutionDispatch");
        var body = JsonDocument.Parse("{\"Capacity\":4096,\"WorkerCount\":1200}").RootElement;
        var result = await controller.PutSection("ExecutionDispatch", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        File.ReadAllText(writer.OverridesPath).Should().Contain("\"Capacity\": 4096");
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_EXECUTIONDISPATCH_UPDATED");
    }

    [Fact]
    public async Task PutSection_Threading_HappyPath()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Threading");
        var body = JsonDocument.Parse("{\"MinWorkerThreads\":1024,\"MinIoCompletionThreads\":1024}").RootElement;
        var result = await controller.PutSection("Threading", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        File.ReadAllText(writer.OverridesPath).Should().Contain("\"MinWorkerThreads\": 1024");
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_THREADING_UPDATED");
    }

    [Fact]
    public async Task PutSection_Remote_HappyPath_PersistsPoolAndWinRm()
    {
        var (controller, writer, audit, _) = NewController();
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Remote");
        var body = JsonDocument.Parse(@"{
            ""RequireWinRmSsl"": true,
            ""WinRm"": { ""OperationTimeoutSeconds"": 600, ""OpenTimeoutSeconds"": 60 },
            ""Pool"": {
                ""Enabled"": true,
                ""MaxConcurrentPerMachine"": 10,
                ""MaxIdlePerKey"": 8,
                ""IdleTtlSeconds"": 300
            }
        }").RootElement;
        var result = await controller.PutSection("Remote", body, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        var file = File.ReadAllText(writer.OverridesPath);
        file.Should().Contain("\"OperationTimeoutSeconds\": 600");
        file.Should().Contain("\"MaxConcurrentPerMachine\": 10");
        audit.Calls.Should().ContainSingle(c => c.Action == "SETTINGS_REMOTE_UPDATED");
    }

    [Fact]
    public async Task PutSection_UnchangedSentinel_PreservesPersistedCiphertext()
    {
        var (controller, writer, _, _) = NewController();
        // First save sets a secret.
        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Smtp");
        var firstBody = JsonDocument.Parse("{\"Host\":\"h\",\"Port\":25,\"From\":\"a@b.c\",\"Password\":\"orig\"}").RootElement;
        await controller.PutSection("Smtp", firstBody, CancellationToken.None);

        // Second save uses the __unchanged__ sentinel — the file must keep the original
        // ciphertext byte-for-byte, otherwise round-trips through the UI would silently
        // reset secrets when the user only changed Host.
        var beforeContent = File.ReadAllText(writer.OverridesPath);
        var beforeCipher = ExtractFieldValue(beforeContent, "Password");

        controller.HttpContext.Request.Headers.IfMatch = writer.ComputeSectionEtag("Smtp");
        var secondBody = JsonDocument.Parse("{\"Host\":\"new-host\",\"Port\":25,\"From\":\"a@b.c\",\"Password\":\"__unchanged__\"}").RootElement;
        await controller.PutSection("Smtp", secondBody, CancellationToken.None);

        var afterContent = File.ReadAllText(writer.OverridesPath);
        var afterCipher = ExtractFieldValue(afterContent, "Password");
        afterCipher.Should().Be(beforeCipher);
        afterContent.Should().Contain("new-host");
    }

    [Fact]
    public async Task TestSmtp_UnchangedPassword_PreservesSubmittedEnableSsl()
    {
        await using var smtp = await FakeSmtpServer.StartAsync();
        var (controller, _, _, _) = NewController(new SmtpOptions
        {
            Host = IPAddress.Loopback.ToString(),
            Port = smtp.Port,
            From = "sender@example.com",
            Password = "stored-secret",
            EnableSsl = true,
        });

        var response = await controller.TestSmtp(new SmtpTestProbeRequest(
            new SmtpSettingsDto
            {
                Host = IPAddress.Loopback.ToString(),
                Port = smtp.Port,
                From = "sender@example.com",
                Password = SettingsSchema.UnchangedSecretSentinel,
                EnableSsl = false,
            },
            "recipient@example.com"), CancellationToken.None);

        var ok = response.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var result = ok!.Value as SettingsTestProbeResult;
        result.Should().NotBeNull();
        result!.Ok.Should().BeTrue(
            "the probe must use the submitted EnableSsl=false value when replacing the unchanged password sentinel");

        var session = await smtp.AwaitSessionAsync(TimeSpan.FromSeconds(5));
        session.DataReceived.Should().BeTrue();
    }

    private static string ExtractFieldValue(string json, string key)
    {
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("Smtp").GetProperty(key).GetString()!;
    }

    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TaskCompletionSource<SmtpSession> _sessionTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new();
        private Task? _acceptLoop;

        private FakeSmtpServer(TcpListener listener, int port)
        {
            _listener = listener;
            Port = port;
        }

        public int Port { get; }

        public static Task<FakeSmtpServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = new FakeSmtpServer(listener, port);
            server._acceptLoop = Task.Run(() => server.AcceptAsync(server._cts.Token));
            return Task.FromResult(server);
        }

        public async Task<SmtpSession> AwaitSessionAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_sessionTcs.Task, Task.Delay(timeout));
            if (completed != _sessionTcs.Task)
                throw new TimeoutException("Fake SMTP server did not record a session in time.");
            return await _sessionTcs.Task;
        }

        private async Task AcceptAsync(CancellationToken ct)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
                var session = new SmtpSession();

                await writer.WriteLineAsync("220 fake.smtp.test ESMTP ready");

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;

                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250-fake.smtp.test");
                        await writer.WriteLineAsync("250-SIZE 10485760");
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase) ||
                             line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("354 End data with <CRLF>.<CRLF>");
                        while (true)
                        {
                            var dataLine = await reader.ReadLineAsync(ct);
                            if (dataLine is null || dataLine == ".") break;
                        }
                        session.DataReceived = true;
                        await writer.WriteLineAsync("250 OK message accepted");
                    }
                    else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("221 Bye");
                        break;
                    }
                    else
                    {
                        await writer.WriteLineAsync("250 OK");
                    }
                }

                _sessionTcs.TrySetResult(session);
            }
            catch (OperationCanceledException)
            {
                _sessionTcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                _sessionTcs.TrySetException(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* already stopped */ }
            if (_acceptLoop is not null)
            {
                try { await _acceptLoop; } catch { /* shutdown noise */ }
            }
            _cts.Dispose();
        }
    }

    private sealed class SmtpSession
    {
        public bool DataReceived { get; set; }
    }
}
