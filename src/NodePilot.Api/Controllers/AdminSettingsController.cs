using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NodePilot.Ai;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Configuration;
using NodePilot.Api.Dtos.Settings;
using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Services;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Options;
using NodePilot.Scheduler.Options;
using NodePilot.Telemetry;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Admin-only API for runtime settings overrides. The controller owns cross-section
/// orchestration: ETag checks, env-lock stripping, boot validation, audit, and atomic
/// writes. Each editable Settings Section is handled by an explicit Settings Section
/// Adapter.
/// </summary>
[ApiController]
[Route("api/admin/settings")]
[Authorize(Roles = "Admin")]
public sealed class AdminSettingsController : ControllerBase
{
    private readonly RuntimeOverridesWriter _writer;
    private readonly IConfigurationRoot _configRoot;
    private readonly ISecretProtector _protector;
    private readonly IAuditWriter _audit;
    private readonly SettingsTestProbe _testProbe;
    private readonly IOptionsMonitor<SmtpOptions> _smtpOptions;
    private readonly IOptionsMonitor<LlmOptions> _llmOptions;
    private readonly IClusterStateProvider _clusterState;
    private readonly ISettingsSectionAdapterRegistry _sectionAdapters;
    private readonly bool _clusterEnabled;

    public AdminSettingsController(
        RuntimeOverridesWriter writer,
        IConfiguration configuration,
        ISecretProtector protector,
        IAuditWriter audit,
        SettingsTestProbe testProbe,
        IOptionsMonitor<SmtpOptions> smtpOptions,
        IOptionsMonitor<LlmOptions> llmOptions,
        IOptionsMonitor<RetentionOptions> retentionOptions,
        IOptionsMonitor<LdapOptions> ldapOptions,
        IOptionsMonitor<WindowsAuthOptions> windowsAuthOptions,
        IOptionsMonitor<NodePilotTelemetryOptions> telemetryOptions,
        IOptionsMonitor<AiKnowledgeOptions> aiKnowledgeOptions,
        IClusterStateProvider clusterState,
        ISettingsSectionAdapterRegistry? sectionAdapters = null)
    {
        _writer = writer;
        _configRoot = configuration as IConfigurationRoot
            ?? throw new InvalidOperationException("Expected IConfigurationRoot for source detection.");
        _protector = protector;
        _audit = audit;
        _testProbe = testProbe;
        _smtpOptions = smtpOptions;
        _llmOptions = llmOptions;
        _clusterState = clusterState;
        _clusterEnabled = configuration.GetValue<bool>("Cluster:Enabled");
        _sectionAdapters = sectionAdapters ?? SettingsSectionAdapters.CreateDefault(
            _configRoot,
            protector,
            smtpOptions,
            llmOptions,
            retentionOptions,
            ldapOptions,
            windowsAuthOptions,
            telemetryOptions,
            aiKnowledgeOptions);
    }

    [HttpGet("status")]
    public ActionResult<SettingsStatusResponse> GetStatus()
    {
        var status = _writer.ReadStatus();
        return Ok(new SettingsStatusResponse(
            OverridesPath: status.OverridesPath,
            RestartRequired: status.RestartRequired,
            RestartRequiredSince: status.RestartRequiredSince,
            RestartRequiredFor: status.RestartRequiredFor,
            LastSavedAt: status.LastSavedAt,
            LastSavedBy: status.LastSavedBy));
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<object>> GetSnapshot()
    {
        var responses = _sectionAdapters.All.Select(a => (object)BuildSectionResponse(a)).ToList();
        return Ok(responses);
    }

    [HttpGet("{section}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSection(string section)
    {
        var adapter = _sectionAdapters.Find(section);
        if (adapter is null) return NotFound(new { code = "SETTINGS_SECTION_UNKNOWN", section });

        var response = BuildSectionResponse(adapter);
        Response.Headers.ETag = response.Etag;
        return Ok(response);
    }

    [HttpPut("{section}")]
    public async Task<IActionResult> PutSection(string section, [FromBody] JsonElement payload, CancellationToken ct)
    {
        var adapter = _sectionAdapters.Find(section);
        if (adapter is null) return NotFound(new { code = "SETTINGS_SECTION_UNKNOWN", section });
        var descriptor = adapter.Descriptor;
        if (_clusterEnabled
            && string.Equals(descriptor.SectionPath, "Authentication", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new
            {
                code = "CLUSTER_CONFIG_AS_CODE_REQUIRED",
                message = "Authentication settings are read-only in HA. Deploy identical bootstrap configuration and secrets to every node, then restart the cluster.",
            });
        }

        var ifMatch = Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return StatusCode(StatusCodes.Status428PreconditionRequired,
                new { code = "ETAG_REQUIRED", message = "Provide the section's current ETag in the If-Match header. GET the section first to obtain it." });
        }

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        object dto;
        try
        {
            dto = adapter.Deserialize(payload, jsonOptions);
        }
        catch (JsonException ex)
        {
            return BadRequest(new { code = "SETTINGS_BODY_INVALID", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { code = "SETTINGS_BODY_INVALID", message = ex.Message });
        }

        var validationResults = adapter.Validate(dto);
        if (validationResults.Count > 0)
        {
            return BadRequest(new
            {
                code = "SETTINGS_VALIDATION_FAILED",
                errors = validationResults.Select(r => new { fields = r.MemberNames, message = r.ErrorMessage }),
            });
        }

        var oldSnapshot = ReadCurrentSectionObject(descriptor);
        var newSection = adapter.BuildSectionObject(dto, oldSnapshot);

        var mergedConfig = SimulateMergedConfig(descriptor.SectionPath, newSection);
        var issues = BootValidatorRunner.RunAllSafely(mergedConfig);
        var errorIssues = issues.Where(i => i.Severity == BootValidationSeverity.Error).ToList();
        if (errorIssues.Count > 0)
        {
            return BadRequest(new
            {
                code = "SETTINGS_VALIDATION_FAILED",
                message = "Saving these values would prevent the service from booting.",
                errors = errorIssues.Select(e => new { e.ValidatorName, e.ConfigKey, e.Message }),
            });
        }

        StripEnvLockedKeys(newSection, descriptor.SectionPath);

        var username = User.Identity?.Name ?? "unknown";
        var now = DateTimeOffset.UtcNow;
        var restartFor = descriptor.IsHotReloadable ? null : new[] { descriptor.SectionPath };

        var atomic = _writer.TryUpdateSectionAtomic(
            descriptor.SectionPath,
            ifMatch.Trim(),
            newSection,
            restartFor,
            username,
            now);

        if (!atomic.Success)
        {
            var freshResponse = BuildSectionResponseFromJson(adapter, atomic.CurrentSection, atomic.CurrentEtag);
            return StatusCode(StatusCodes.Status412PreconditionFailed, new
            {
                code = "ETAG_MISMATCH",
                message = "The section was modified after you loaded it. Reload and merge your changes.",
                current = freshResponse,
            });
        }

        var diff = ComputeAuditDiff(descriptor, oldSnapshot, atomic.PersistedSection!);
        await _audit.LogAsync(descriptor.AuditCode, "Settings", null, diff, ct);

        return Ok(BuildSectionResponseFromJson(adapter, atomic.PersistedSection, atomic.CurrentEtag));
    }

    [HttpGet("system-info")]
    public ActionResult<SystemInfoResponse> GetSystemInfo()
    {
        var dbProvider = (_configRoot["Database:Provider"] ?? "postgres").Trim().ToLowerInvariant();
        var connKey = dbProvider switch
        {
            "sqlserver" => "ConnectionStrings:DefaultConnection",
            _ => "ConnectionStrings:Postgres",
        };
        var host = TryExtractHost(_configRoot[connKey]);
        var version = typeof(AdminSettingsController).Assembly.GetName().Version?.ToString() ?? "unknown";

        return Ok(new SystemInfoResponse(
            AppVersion: version,
            OverridesPath: _writer.OverridesPath,
            DatabaseProvider: dbProvider,
            DatabaseHost: host,
            SecretsProvider: _protector.ProviderName,
            ClusterEnabled: bool.TryParse(_configRoot["Cluster:Enabled"], out var ce) && ce,
            ClusterNodeId: _clusterState.NodeId,
            ClusterIsLeader: _clusterState.IsLeader,
            JwtIssuer: _configRoot["Jwt:Issuer"] ?? "NodePilot",
            JwtAudience: _configRoot["Jwt:Audience"] ?? "NodePilot"));
    }

    internal static string? TryExtractHost(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        foreach (var token in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0) continue;
            var key = token[..eq].Trim();
            var value = token[(eq + 1)..].Trim();
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Server", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Data Source", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return null;
    }

    [HttpPost("test/smtp")]
    public async Task<ActionResult<SettingsTestProbeResult>> TestSmtp([FromBody] SmtpTestProbeRequest request, CancellationToken ct)
    {
        if (request?.Settings is null) return BadRequest(new { code = "SETTINGS_BODY_INVALID" });

        if (string.Equals(request.Settings.Password, SettingsSchema.UnchangedSecretSentinel, StringComparison.Ordinal))
        {
            request = new SmtpTestProbeRequest(
                new SmtpSettingsDto
                {
                    Host = request.Settings.Host,
                    Port = request.Settings.Port,
                    Username = request.Settings.Username,
                    Password = _smtpOptions.CurrentValue.Password,
                    From = request.Settings.From,
                    EnableSsl = request.Settings.EnableSsl,
                },
                request.ToAddress);
        }

        var result = await _testProbe.TestSmtpAsync(request, ct);
        await _audit.LogAsync(AuditActions.SettingsSmtpTested, "Settings", null,
            AuditDetails.Json(("success", result.Ok), ("host", request.Settings.Host), ("port", request.Settings.Port)),
            ct);
        return Ok(result);
    }

    [HttpPost("test/llm")]
    public async Task<ActionResult<SettingsTestProbeResult>> TestLlm([FromBody] LlmTestProbeRequest request, CancellationToken ct)
    {
        if (request?.Settings is null) return BadRequest(new { code = "SETTINGS_BODY_INVALID" });

        if (string.Equals(request.Settings.ApiKey, SettingsSchema.UnchangedSecretSentinel, StringComparison.Ordinal))
        {
            request = new LlmTestProbeRequest(new LlmSettingsDto
            {
                Enabled = request.Settings.Enabled,
                BaseUrl = request.Settings.BaseUrl,
                ApiKey = _llmOptions.CurrentValue.ApiKey,
                Model = request.Settings.Model,
                MaxTokens = request.Settings.MaxTokens,
                TimeoutSeconds = request.Settings.TimeoutSeconds,
            });
        }

        var result = await _testProbe.TestLlmAsync(request, ct);
        await _audit.LogAsync(AuditActions.SettingsLlmTested, "Settings", null,
            AuditDetails.Json(("success", result.Ok), ("baseUrl", request.Settings.BaseUrl), ("model", request.Settings.Model)),
            ct);
        return Ok(result);
    }

    [HttpPost("test/ldap")]
    public async Task<ActionResult<SettingsTestProbeResult>> TestLdap(
        [FromBody] LdapTestProbeRequest request,
        CancellationToken ct)
    {
        if (request?.Settings is null) return BadRequest(new { code = "SETTINGS_BODY_INVALID" });

        var result = await _testProbe.TestLdapAsync(request, ct);
        await _audit.LogAsync(AuditActions.SettingsAuthenticationTested, "Settings", null,
            AuditDetails.Json(
                ("success", result.Ok),
                ("endpointCount", request.Settings.Endpoints.Count),
                ("legacyServerConfigured", !string.IsNullOrWhiteSpace(request.Settings.Server))),
            ct);
        return Ok(result);
    }

    private SettingsSectionResponse<object> BuildSectionResponse(ISettingsSectionAdapter adapter)
    {
        var etag = _writer.ComputeSectionEtag(adapter.Descriptor.SectionPath);
        return new SettingsSectionResponse<object>
        {
            SectionPath = adapter.Descriptor.SectionPath,
            Payload = adapter.BuildCurrentPayload(),
            Etag = etag,
            IsHotReloadable = adapter.Descriptor.IsHotReloadable,
            EffectiveSource = EffectiveSourceDetector.DetectMany(_configRoot, adapter.ConfigKeys),
        };
    }

    private SettingsSectionResponse<object> BuildSectionResponseFromJson(
        ISettingsSectionAdapter adapter,
        JsonObject? sectionJson,
        string etag)
    {
        return new SettingsSectionResponse<object>
        {
            SectionPath = adapter.Descriptor.SectionPath,
            Payload = adapter.BuildPayloadFromJson(sectionJson),
            Etag = etag,
            IsHotReloadable = adapter.Descriptor.IsHotReloadable,
            EffectiveSource = EffectiveSourceDetector.DetectMany(_configRoot, adapter.ConfigKeys),
        };
    }

    private JsonObject? ReadCurrentSectionObject(SettingsSectionDescriptor descriptor)
    {
        var root = _writer.ReadOrEmpty();
        return RuntimeOverridesWriter.NavigateSection(root, descriptor.SectionPath) as JsonObject;
    }

    private IConfigurationRoot SimulateMergedConfig(string sectionPath, JsonObject newSection)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(CollectExistingNonSectionKeys(sectionPath))
            .AddInMemoryCollection(FlattenSection(sectionPath, newSection))
            .Build();
    }

    private IEnumerable<KeyValuePair<string, string?>> CollectExistingNonSectionKeys(string excludedSectionPath)
    {
        foreach (var kv in _configRoot.AsEnumerable())
        {
            if (kv.Key.StartsWith(excludedSectionPath + ":", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kv.Key, excludedSectionPath, StringComparison.OrdinalIgnoreCase)) continue;
            yield return kv;
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> FlattenSection(string sectionPath, JsonObject section)
    {
        foreach (var (k, v) in FlattenNode(section))
            yield return new KeyValuePair<string, string?>($"{sectionPath}:{k}", v);
    }

    private static IEnumerable<KeyValuePair<string, string?>> FlattenNode(JsonNode? node, string prefix = "")
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (k, child) in obj)
                {
                    var nextPrefix = string.IsNullOrEmpty(prefix) ? k : $"{prefix}:{k}";
                    foreach (var kv in FlattenNode(child, nextPrefix))
                        yield return kv;
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var nextPrefix = string.IsNullOrEmpty(prefix) ? i.ToString() : $"{prefix}:{i}";
                    foreach (var kv in FlattenNode(arr[i], nextPrefix))
                        yield return kv;
                }
                break;
            case JsonValue val:
                yield return new KeyValuePair<string, string?>(prefix, val.ToString());
                break;
        }
    }

    private void StripEnvLockedKeys(JsonObject section, string sectionPath)
    {
        var toRemove = new List<string>();
        foreach (var kvp in section)
        {
            var fullPath = $"{sectionPath}:{kvp.Key}";
            if (kvp.Value is JsonObject child)
            {
                StripEnvLockedKeys(child, fullPath);
                if (child.Count == 0) toRemove.Add(kvp.Key);
            }
            else
            {
                var src = EffectiveSourceDetector.Detect(_configRoot, fullPath);
                if (src == EffectiveSourceDetector.SourceEnv || src == EffectiveSourceDetector.SourceCli)
                    toRemove.Add(kvp.Key);
            }
        }
        foreach (var k in toRemove) section.Remove(k);
    }

    private static string ComputeAuditDiff(
        SettingsSectionDescriptor descriptor,
        JsonObject? oldSection,
        JsonObject newSection)
    {
        var diff = new JsonObject
        {
            ["section"] = descriptor.SectionPath,
            ["before"] = RedactSecrets(oldSection ?? new JsonObject(), descriptor.SecretFieldPaths),
            ["after"] = RedactSecrets(newSection, descriptor.SecretFieldPaths),
        };
        return diff.ToJsonString();
    }

    private static JsonNode RedactSecrets(JsonObject section, System.Collections.Immutable.ImmutableArray<string> secretPaths)
    {
        var copy = JsonNode.Parse(section.ToJsonString())!.AsObject();
        foreach (var path in secretPaths)
        {
            var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
            JsonObject? parent = copy;
            for (var i = 0; i < parts.Length - 1 && parent is not null; i++)
                parent = parent[parts[i]] as JsonObject;

            var leaf = parts.Length == 0 ? path : parts[^1];
            if (parent?[leaf] is not null) parent[leaf] = "***";
        }
        return copy;
    }
}
