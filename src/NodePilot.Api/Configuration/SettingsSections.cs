using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NodePilot.Ai;
using NodePilot.Api.Dtos.Settings;
using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Options;
using NodePilot.Scheduler.Options;
using NodePilot.Telemetry;

namespace NodePilot.Api.Configuration;

public interface ISettingsSectionAdapter
{
    SettingsSectionDescriptor Descriptor { get; }
    IReadOnlyList<string> ConfigKeys { get; }
    object BuildCurrentPayload();
    object BuildPayloadFromJson(JsonObject? section);
    object Deserialize(JsonElement payload, JsonSerializerOptions options);
    IReadOnlyList<ValidationResult> Validate(object payload);
    JsonObject BuildSectionObject(object payload, JsonObject? previousSection);
}

public interface ISettingsSectionAdapterRegistry
{
    IReadOnlyList<ISettingsSectionAdapter> All { get; }
    ISettingsSectionAdapter? Find(string sectionPath);
}

public sealed class SettingsSectionAdapterRegistry : ISettingsSectionAdapterRegistry
{
    private readonly Dictionary<string, ISettingsSectionAdapter> _byPath;

    public SettingsSectionAdapterRegistry(IEnumerable<ISettingsSectionAdapter> adapters)
    {
        All = adapters.ToArray();
        _byPath = All.ToDictionary(a => a.Descriptor.SectionPath, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ISettingsSectionAdapter> All { get; }

    public ISettingsSectionAdapter? Find(string sectionPath)
        => _byPath.GetValueOrDefault(sectionPath);
}

public static class SettingsSectionAdapters
{
    public static ISettingsSectionAdapterRegistry CreateDefault(
        IConfigurationRoot configRoot,
        ISecretProtector protector,
        IOptionsMonitor<SmtpOptions> smtpOptions,
        IOptionsMonitor<LlmOptions> llmOptions,
        IOptionsMonitor<RetentionOptions> retentionOptions,
        IOptionsMonitor<LdapOptions> ldapOptions,
        IOptionsMonitor<WindowsAuthOptions> windowsAuthOptions,
        IOptionsMonitor<NodePilotTelemetryOptions> telemetryOptions,
        IOptionsMonitor<AiKnowledgeOptions> aiKnowledgeOptions)
    {
        return new SettingsSectionAdapterRegistry(new ISettingsSectionAdapter[]
        {
            new DelegateSettingsSectionAdapter<SmtpSettingsDto>(
                Descriptor("Smtp"),
                ["Smtp:Host", "Smtp:Port", "Smtp:Username", "Smtp:Password", "Smtp:From", "Smtp:EnableSsl"],
                () => BuildSmtpDto(smtpOptions.CurrentValue),
                BuildSmtpDtoFromJson,
                (dto, previous) => BuildSmtpSectionObject(dto, previous, protector)),

            new DelegateSettingsSectionAdapter<LlmSettingsDto>(
                Descriptor("Llm"),
                ["Llm:Enabled", "Llm:BaseUrl", "Llm:ApiKey", "Llm:Model", "Llm:MaxTokens", "Llm:TimeoutSeconds", "Llm:EnableToolCalling", "Llm:ToolCallMaxDepth"],
                () => BuildLlmDto(llmOptions.CurrentValue),
                BuildLlmDtoFromJson,
                (dto, previous) => BuildLlmSectionObject(dto, previous, protector)),

            new DelegateSettingsSectionAdapter<AiKnowledgeSettingsDto>(
                Descriptor("AiKnowledge"),
                ["AiKnowledge:Enabled", "AiKnowledge:DocsEnabled", "AiKnowledge:OperationalEnabled", "AiKnowledge:SourceCodeEnabled", "AiKnowledge:DbEnabled", "AiKnowledge:DocsRootPath", "AiKnowledge:SourceCodeRootPath", "AiKnowledge:DocsMaxFileBytes", "AiKnowledge:DocsMaxResults", "AiKnowledge:SourceCodeMaxFileBytes", "AiKnowledge:SourceCodeMaxResults"],
                () => BuildAiKnowledgeDto(aiKnowledgeOptions.CurrentValue),
                BuildAiKnowledgeDtoFromJson,
                (dto, _) => BuildAiKnowledgeSectionObject(dto)),

            new DelegateSettingsSectionAdapter<RetentionSettingsDto>(
                Descriptor("Retention"),
                RetentionConfigKeys,
                () => BuildRetentionDto(retentionOptions.CurrentValue),
                BuildRetentionDtoFromJson,
                (dto, _) => BuildRetentionSectionObject(dto)),

            new DelegateSettingsSectionAdapter<AuthenticationSettingsDto>(
                Descriptor("Authentication"),
                AuthenticationConfigKeys,
                () => BuildAuthenticationDto(ldapOptions.CurrentValue, windowsAuthOptions.CurrentValue, configRoot),
                BuildAuthenticationDtoFromJson,
                (dto, previous) => BuildAuthenticationSectionObject(dto, previous, protector)),

            new DelegateSettingsSectionAdapter<LoggingSettingsDto>(
                Descriptor("Logging"),
                LoggingConfigKeys,
                () => BuildLoggingDto(configRoot),
                BuildLoggingDtoFromJson,
                (dto, _) => BuildLoggingSectionObject(dto)),

            new DelegateSettingsSectionAdapter<OpenTelemetrySettingsDto>(
                Descriptor("OpenTelemetry"),
                OpenTelemetryConfigKeys,
                () => BuildOpenTelemetryDto(telemetryOptions.CurrentValue, configRoot),
                BuildOpenTelemetryDtoFromJson,
                (dto, previous) => BuildOpenTelemetrySectionObject(dto, previous, protector)),

            new DelegateSettingsSectionAdapter<StatsSettingsDto>(
                Descriptor("Stats"),
                ["Stats:RefreshIntervalMinutes", "Stats:WindowDays"],
                () => BuildStatsDto(configRoot),
                BuildStatsDtoFromJson,
                (dto, _) => new JsonObject
                {
                    ["RefreshIntervalMinutes"] = dto.RefreshIntervalMinutes,
                    ["WindowDays"] = dto.WindowDays,
                }),

            new DelegateSettingsSectionAdapter<DbAdminSettingsDto>(
                Descriptor("DbAdmin"),
                ["DbAdmin:AllowWriteQueries", "DbAdmin:QueryTimeoutSeconds", "DbAdmin:QueryMaxRows"],
                () => BuildDbAdminDto(configRoot),
                BuildDbAdminDtoFromJson,
                (dto, _) => new JsonObject
                {
                    ["AllowWriteQueries"] = dto.AllowWriteQueries,
                    ["QueryTimeoutSeconds"] = dto.QueryTimeoutSeconds,
                    ["QueryMaxRows"] = dto.QueryMaxRows,
                }),

            new DelegateSettingsSectionAdapter<RestApiSettingsDto>(
                Descriptor("RestApi"),
                RestApiConfigKeys,
                () => BuildRestApiDto(configRoot),
                BuildRestApiDtoFromJson,
                (dto, previous) => BuildRestApiSectionObject(dto, previous, protector)),

            new DelegateSettingsSectionAdapter<FileSystemOperationSettingsDto>(
                Descriptor("FileSystemOperation"),
                ["FileSystemOperation:RejectTraversal", "FileSystemOperation:AllowedRoots"],
                () => new FileSystemOperationSettingsDto
                {
                    RejectTraversal = BoolDefaultTrue(configRoot["FileSystemOperation:RejectTraversal"]),
                    AllowedRoots = ReadStringArray(configRoot, "FileSystemOperation:AllowedRoots"),
                },
                BuildFileSystemOperationDtoFromJson,
                (dto, _) => new JsonObject
                {
                    ["RejectTraversal"] = dto.RejectTraversal,
                    ["AllowedRoots"] = ToJsonArray(dto.AllowedRoots),
                }),

            new DelegateSettingsSectionAdapter<SqlActivitySettingsDto>(
                Descriptor("SqlActivity"),
                ["SqlActivity:RequireConnectionRef"],
                () => new SqlActivitySettingsDto
                {
                    RequireConnectionRef = !string.Equals(configRoot["SqlActivity:RequireConnectionRef"], "false", StringComparison.OrdinalIgnoreCase),
                },
                BuildSqlActivityDtoFromJson,
                (dto, _) => new JsonObject { ["RequireConnectionRef"] = dto.RequireConnectionRef }),

            new DelegateSettingsSectionAdapter<StartProgramSettingsDto>(
                Descriptor("StartProgram"),
                ["StartProgram:DisallowShellExecute"],
                () => new StartProgramSettingsDto
                {
                    DisallowShellExecute = BoolDefaultTrue(configRoot["StartProgram:DisallowShellExecute"]),
                },
                BuildStartProgramDtoFromJson,
                (dto, _) => new JsonObject { ["DisallowShellExecute"] = dto.DisallowShellExecute }),

            new DelegateSettingsSectionAdapter<WebhookSettingsDto>(
                Descriptor("Webhook"),
                ["Webhook:RequireSecret"],
                () => new WebhookSettingsDto { RequireSecret = BoolDefaultTrue(configRoot["Webhook:RequireSecret"]) },
                BuildWebhookDtoFromJson,
                (dto, _) => new JsonObject { ["RequireSecret"] = dto.RequireSecret }),

            new DelegateSettingsSectionAdapter<ExternalTriggerSettingsDto>(
                Descriptor("ExternalTrigger"),
                ["ExternalTrigger:ApiKey"],
                () => new ExternalTriggerSettingsDto
                {
                    ApiKey = string.IsNullOrEmpty(configRoot["ExternalTrigger:ApiKey"]) ? null : "********",
                },
                BuildExternalTriggerDtoFromJson,
                (dto, previous) =>
                {
                    var section = new JsonObject();
                    WriteSecretField(section, "ApiKey", dto.ApiKey, previous ?? new JsonObject(), protector);
                    return section;
                }),

            new DelegateSettingsSectionAdapter<SecuritySettingsDto>(
                Descriptor("Security"),
                ["Security:StrictAllowedHosts", "AllowedHosts"],
                () => new SecuritySettingsDto
                {
                    StrictAllowedHosts = bool.TryParse(configRoot["Security:StrictAllowedHosts"], out var b) && b,
                    AllowedHosts = configRoot["AllowedHosts"] ?? "*",
                },
                BuildSecurityDtoFromJson,
                (dto, _) => new JsonObject
                {
                    ["StrictAllowedHosts"] = dto.StrictAllowedHosts,
                    ["AllowedHosts"] = dto.AllowedHosts,
                }),

            new DelegateSettingsSectionAdapter<EngineSettingsDto>(
                Descriptor("Engine"),
                EngineConfigKeys,
                () => BuildEngineDto(configRoot),
                BuildEngineDtoFromJson,
                (dto, _) => BuildEngineSectionObject(dto)),

            new DelegateSettingsSectionAdapter<ExecutionDispatchSettingsDto>(
                Descriptor("ExecutionDispatch"),
                ["ExecutionDispatch:Capacity", "ExecutionDispatch:WorkerCount"],
                () => new ExecutionDispatchSettingsDto
                {
                    Capacity = IntOr(configRoot["ExecutionDispatch:Capacity"], 2048),
                    WorkerCount = IntOr(configRoot["ExecutionDispatch:WorkerCount"], 600),
                },
                BuildExecutionDispatchDtoFromJson,
                (dto, _) => new JsonObject
                {
                    ["Capacity"] = dto.Capacity,
                    ["WorkerCount"] = dto.WorkerCount,
                }),

            new DelegateSettingsSectionAdapter<ThreadingSettingsDto>(
                Descriptor("Threading"),
                ["Threading:MinWorkerThreads", "Threading:MinIoCompletionThreads"],
                () => new ThreadingSettingsDto
                {
                    MinWorkerThreads = IntOr(configRoot["Threading:MinWorkerThreads"], 768),
                    MinIoCompletionThreads = IntOr(configRoot["Threading:MinIoCompletionThreads"], 768),
                },
                BuildThreadingDtoFromJson,
                (dto, _) => new JsonObject
                {
                    ["MinWorkerThreads"] = dto.MinWorkerThreads,
                    ["MinIoCompletionThreads"] = dto.MinIoCompletionThreads,
                }),

            new DelegateSettingsSectionAdapter<RemoteSettingsDto>(
                Descriptor("Remote"),
                RemoteConfigKeys,
                () => BuildRemoteDto(configRoot),
                BuildRemoteDtoFromJson,
                (dto, _) => BuildRemoteSectionObject(dto)),
        });
    }

    private sealed class DelegateSettingsSectionAdapter<TDto> : ISettingsSectionAdapter
        where TDto : class
    {
        private readonly Func<TDto> _buildCurrentPayload;
        private readonly Func<JsonObject?, TDto> _buildPayloadFromJson;
        private readonly Func<TDto, JsonObject?, JsonObject> _buildSectionObject;

        public DelegateSettingsSectionAdapter(
            SettingsSectionDescriptor descriptor,
            IReadOnlyList<string> configKeys,
            Func<TDto> buildCurrentPayload,
            Func<JsonObject?, TDto> buildPayloadFromJson,
            Func<TDto, JsonObject?, JsonObject> buildSectionObject)
        {
            Descriptor = descriptor;
            ConfigKeys = configKeys;
            _buildCurrentPayload = buildCurrentPayload;
            _buildPayloadFromJson = buildPayloadFromJson;
            _buildSectionObject = buildSectionObject;
        }

        public SettingsSectionDescriptor Descriptor { get; }
        public IReadOnlyList<string> ConfigKeys { get; }

        public object BuildCurrentPayload() => _buildCurrentPayload();
        public object BuildPayloadFromJson(JsonObject? section) => _buildPayloadFromJson(section);

        public object Deserialize(JsonElement payload, JsonSerializerOptions options)
            => payload.Deserialize<TDto>(options)
               ?? throw new InvalidOperationException("Body must be a JSON object.");

        public IReadOnlyList<ValidationResult> Validate(object payload)
        {
            var validationContext = new ValidationContext(payload);
            var validationResults = new List<ValidationResult>();
            Validator.TryValidateObject(payload, validationContext, validationResults, validateAllProperties: true);
            return validationResults;
        }

        public JsonObject BuildSectionObject(object payload, JsonObject? previousSection)
            => _buildSectionObject((TDto)payload, previousSection);
    }

    private static SettingsSectionDescriptor Descriptor(string sectionPath)
        => SettingsSchema.Find(sectionPath)
           ?? throw new InvalidOperationException($"No Settings Schema entry registered for '{sectionPath}'.");

    private static SmtpSettingsDto BuildSmtpDto(SmtpOptions s) => new()
    {
        Host = s.Host,
        Port = s.Port,
        Username = s.Username,
        Password = string.IsNullOrEmpty(s.Password) ? null : "********",
        From = s.From,
        EnableSsl = s.EnableSsl,
    };

    private static SmtpSettingsDto BuildSmtpDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new SmtpSettingsDto
        {
            Host = section["Host"]?.GetValue<string>() ?? "",
            Port = section["Port"]?.GetValue<int>() ?? 25,
            Username = ReadNullableString(section, "Username"),
            Password = HasNonNullValue(section, "Password") ? "********" : null,
            From = section["From"]?.GetValue<string>() ?? "",
            // Missing key keeps the safe-default behaviour from the options object.
            EnableSsl = section["EnableSsl"]?.GetValue<bool>() ?? true,
        };
    }

    private static JsonObject BuildSmtpSectionObject(
        SmtpSettingsDto dto,
        JsonObject? previousSection,
        ISecretProtector protector)
    {
        var section = new JsonObject
        {
            ["Host"] = dto.Host,
            ["Port"] = dto.Port,
            ["From"] = dto.From,
            ["EnableSsl"] = dto.EnableSsl,
        };
        WriteOrExplicitNull(section, "Username", dto.Username);
        WriteSecretField(section, "Password", dto.Password, previousSection ?? new JsonObject(), protector);
        return section;
    }

    private static LlmSettingsDto BuildLlmDto(LlmOptions s) => new()
    {
        Enabled = s.Enabled,
        BaseUrl = s.BaseUrl,
        ApiKey = string.IsNullOrEmpty(s.ApiKey) ? null : "********",
        Model = s.Model,
        MaxTokens = s.MaxTokens,
        TimeoutSeconds = s.TimeoutSeconds,
        EnableToolCalling = s.EnableToolCalling,
        ToolCallMaxDepth = s.ToolCallMaxDepth,
    };

    private static LlmSettingsDto BuildLlmDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new LlmSettingsDto
        {
            Enabled = section["Enabled"]?.GetValue<bool>() ?? false,
            BaseUrl = section["BaseUrl"]?.GetValue<string>() ?? "",
            ApiKey = HasNonNullValue(section, "ApiKey") ? "********" : null,
            Model = section["Model"]?.GetValue<string>() ?? "",
            MaxTokens = section["MaxTokens"]?.GetValue<int>() ?? 4096,
            TimeoutSeconds = section["TimeoutSeconds"]?.GetValue<int>() ?? 90,
            EnableToolCalling = section["EnableToolCalling"]?.GetValue<bool>() ?? false,
            ToolCallMaxDepth = section["ToolCallMaxDepth"]?.GetValue<int>() ?? 4,
        };
    }

    private static JsonObject BuildLlmSectionObject(
        LlmSettingsDto dto,
        JsonObject? previousSection,
        ISecretProtector protector)
    {
        var section = new JsonObject
        {
            ["Enabled"] = dto.Enabled,
            ["BaseUrl"] = dto.BaseUrl,
            ["Model"] = dto.Model,
            ["MaxTokens"] = dto.MaxTokens,
            ["TimeoutSeconds"] = dto.TimeoutSeconds,
            ["EnableToolCalling"] = dto.EnableToolCalling,
            ["ToolCallMaxDepth"] = dto.ToolCallMaxDepth,
        };
        WriteSecretField(section, "ApiKey", dto.ApiKey, previousSection ?? new JsonObject(), protector);
        return section;
    }

    private static AiKnowledgeSettingsDto BuildAiKnowledgeDto(AiKnowledgeOptions s) => new()
    {
        Enabled = s.Enabled,
        DocsEnabled = s.DocsEnabled,
        OperationalEnabled = s.OperationalEnabled,
        SourceCodeEnabled = s.SourceCodeEnabled,
        DbEnabled = s.DbEnabled,
        DocsRootPath = s.DocsRootPath,
        SourceCodeRootPath = s.SourceCodeRootPath,
        DocsMaxFileBytes = s.DocsMaxFileBytes,
        DocsMaxResults = s.DocsMaxResults,
        SourceCodeMaxFileBytes = s.SourceCodeMaxFileBytes,
        SourceCodeMaxResults = s.SourceCodeMaxResults,
    };

    private static AiKnowledgeSettingsDto BuildAiKnowledgeDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new AiKnowledgeSettingsDto
        {
            Enabled = section["Enabled"]?.GetValue<bool>() ?? false,
            DocsEnabled = section["DocsEnabled"]?.GetValue<bool>() ?? true,
            OperationalEnabled = section["OperationalEnabled"]?.GetValue<bool>() ?? true,
            SourceCodeEnabled = section["SourceCodeEnabled"]?.GetValue<bool>() ?? false,
            DbEnabled = section["DbEnabled"]?.GetValue<bool>() ?? false,
            DocsRootPath = ReadNullableString(section, "DocsRootPath"),
            SourceCodeRootPath = ReadNullableString(section, "SourceCodeRootPath"),
            DocsMaxFileBytes = section["DocsMaxFileBytes"]?.GetValue<int>() ?? 262_144,
            DocsMaxResults = section["DocsMaxResults"]?.GetValue<int>() ?? 20,
            SourceCodeMaxFileBytes = section["SourceCodeMaxFileBytes"]?.GetValue<int>() ?? 262_144,
            SourceCodeMaxResults = section["SourceCodeMaxResults"]?.GetValue<int>() ?? 20,
        };
    }

    private static JsonObject BuildAiKnowledgeSectionObject(AiKnowledgeSettingsDto dto)
    {
        var section = new JsonObject
        {
            ["Enabled"] = dto.Enabled,
            ["DocsEnabled"] = dto.DocsEnabled,
            ["OperationalEnabled"] = dto.OperationalEnabled,
            ["SourceCodeEnabled"] = dto.SourceCodeEnabled,
            ["DbEnabled"] = dto.DbEnabled,
            ["DocsMaxFileBytes"] = dto.DocsMaxFileBytes,
            ["DocsMaxResults"] = dto.DocsMaxResults,
            ["SourceCodeMaxFileBytes"] = dto.SourceCodeMaxFileBytes,
            ["SourceCodeMaxResults"] = dto.SourceCodeMaxResults,
        };
        WriteOrExplicitNull(section, "DocsRootPath", dto.DocsRootPath);
        WriteOrExplicitNull(section, "SourceCodeRootPath", dto.SourceCodeRootPath);
        return section;
    }

    private static readonly string[] RetentionConfigKeys =
    [
        "Retention:Executions:Enabled", "Retention:Executions:MaxAgeDays", "Retention:Executions:IntervalMinutes", "Retention:Executions:BatchSize", "Retention:Executions:ArchivePath",
        "Retention:AuditLog:Enabled", "Retention:AuditLog:MaxAgeDays", "Retention:AuditLog:IntervalMinutes", "Retention:AuditLog:BatchSize", "Retention:AuditLog:ArchivePath",
        "Retention:WorkflowVersions:Enabled", "Retention:WorkflowVersions:MaxVersionsPerWorkflow", "Retention:WorkflowVersions:IntervalMinutes", "Retention:WorkflowVersions:BatchSize",
    ];

    private static RetentionSettingsDto BuildRetentionDto(RetentionOptions s) => new()
    {
        Executions = new ExecutionsRetentionDto
        {
            Enabled = s.Executions.Enabled,
            MaxAgeDays = s.Executions.MaxAgeDays,
            IntervalMinutes = s.Executions.IntervalMinutes,
            BatchSize = s.Executions.BatchSize,
            ArchivePath = s.Executions.ArchivePath,
        },
        AuditLog = new AuditLogRetentionDto
        {
            Enabled = s.AuditLog.Enabled,
            MaxAgeDays = s.AuditLog.MaxAgeDays,
            IntervalMinutes = s.AuditLog.IntervalMinutes,
            BatchSize = s.AuditLog.BatchSize,
            ArchivePath = s.AuditLog.ArchivePath,
        },
        WorkflowVersions = new WorkflowVersionsRetentionDto
        {
            Enabled = s.WorkflowVersions.Enabled,
            MaxVersionsPerWorkflow = s.WorkflowVersions.MaxVersionsPerWorkflow,
            IntervalMinutes = s.WorkflowVersions.IntervalMinutes,
            BatchSize = s.WorkflowVersions.BatchSize,
        },
    };

    private static RetentionSettingsDto BuildRetentionDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        var ex = section["Executions"] as JsonObject ?? new JsonObject();
        var au = section["AuditLog"] as JsonObject ?? new JsonObject();
        var wv = section["WorkflowVersions"] as JsonObject ?? new JsonObject();
        return new RetentionSettingsDto
        {
            Executions = new ExecutionsRetentionDto
            {
                Enabled = ex["Enabled"]?.GetValue<bool>() ?? true,
                MaxAgeDays = ex["MaxAgeDays"]?.GetValue<int>() ?? 30,
                IntervalMinutes = ex["IntervalMinutes"]?.GetValue<int>() ?? 60,
                BatchSize = ex["BatchSize"]?.GetValue<int>() ?? 500,
                ArchivePath = ReadNullableString(ex, "ArchivePath"),
            },
            AuditLog = new AuditLogRetentionDto
            {
                Enabled = au["Enabled"]?.GetValue<bool>() ?? true,
                MaxAgeDays = au["MaxAgeDays"]?.GetValue<int>() ?? 365,
                IntervalMinutes = au["IntervalMinutes"]?.GetValue<int>() ?? 720,
                BatchSize = au["BatchSize"]?.GetValue<int>() ?? 1000,
                ArchivePath = ReadNullableString(au, "ArchivePath"),
            },
            WorkflowVersions = new WorkflowVersionsRetentionDto
            {
                Enabled = wv["Enabled"]?.GetValue<bool>() ?? true,
                MaxVersionsPerWorkflow = wv["MaxVersionsPerWorkflow"]?.GetValue<int>() ?? 50,
                IntervalMinutes = wv["IntervalMinutes"]?.GetValue<int>() ?? 1440,
                BatchSize = wv["BatchSize"]?.GetValue<int>() ?? 500,
            },
        };
    }

    private static JsonObject BuildRetentionSectionObject(RetentionSettingsDto dto)
    {
        var executions = new JsonObject
        {
            ["Enabled"] = dto.Executions.Enabled,
            ["MaxAgeDays"] = dto.Executions.MaxAgeDays,
            ["IntervalMinutes"] = dto.Executions.IntervalMinutes,
            ["BatchSize"] = dto.Executions.BatchSize,
        };
        WriteOrExplicitNull(executions, "ArchivePath", dto.Executions.ArchivePath);

        var audit = new JsonObject
        {
            ["Enabled"] = dto.AuditLog.Enabled,
            ["MaxAgeDays"] = dto.AuditLog.MaxAgeDays,
            ["IntervalMinutes"] = dto.AuditLog.IntervalMinutes,
            ["BatchSize"] = dto.AuditLog.BatchSize,
        };
        WriteOrExplicitNull(audit, "ArchivePath", dto.AuditLog.ArchivePath);

        return new JsonObject
        {
            ["Executions"] = executions,
            ["AuditLog"] = audit,
            ["WorkflowVersions"] = new JsonObject
            {
                ["Enabled"] = dto.WorkflowVersions.Enabled,
                ["MaxVersionsPerWorkflow"] = dto.WorkflowVersions.MaxVersionsPerWorkflow,
                ["IntervalMinutes"] = dto.WorkflowVersions.IntervalMinutes,
                ["BatchSize"] = dto.WorkflowVersions.BatchSize,
            },
        };
    }

    private static readonly string[] AuthenticationConfigKeys =
    [
        "Authentication:LocalLoginMode", "Authentication:SessionAbsoluteLifetimeHours",
        "Authentication:MaxAuthorizationStalenessMinutes",
        "Authentication:Ldap:Enabled", "Authentication:Ldap:Server", "Authentication:Ldap:Port",
        "Authentication:Ldap:Endpoints", "Authentication:Ldap:AllowedGroupSids",
        "Authentication:Ldap:DirectorySyncIntervalMinutes", "Authentication:Ldap:DirectorySyncMaxConcurrency",
        "Authentication:Ldap:UseSsl", "Authentication:Ldap:BaseDn", "Authentication:Ldap:UpnSuffix",
        "Authentication:Ldap:BindTimeoutSeconds", "Authentication:Ldap:ServiceBindDn",
        "Authentication:Ldap:ServicePassword", "Authentication:Ldap:JitUserDefaultRootRole",
        "Authentication:Windows:Enabled", "Authentication:Windows:AllowNtlmFallback",
        "Authentication:Windows:NtlmDisabledByPolicy",
        "Authentication:Oidc:Enabled", "Authentication:Oidc:Authority",
        "Authentication:Oidc:ClientId", "Authentication:Oidc:ClientSecret",
        "Authentication:Oidc:DisplayName", "Authentication:Oidc:NameClaimType",
        "Authentication:Oidc:GroupsClaimType", "Authentication:Oidc:Scopes",
        "Authentication:Oidc:AllowedGroupIds", "Authentication:Oidc:GlobalRoleMappings",
        "Authentication:Scim:Enabled", "Authentication:Scim:BearerToken",
        "Authentication:Scim:PreviousBearerToken", "Authentication:Scim:Authority",
    ];

    private static AuthenticationSettingsDto BuildAuthenticationDto(
        LdapOptions ldap,
        WindowsAuthOptions win,
        IConfiguration config)
    {
        var localMode = Enum.TryParse<NodePilot.Api.Security.LocalLoginMode>(
            config["Authentication:LocalLoginMode"], true, out var parsedLocalMode)
            ? parsedLocalMode
            : NodePilot.Api.Security.LocalLoginMode.BreakGlassOnly;
        return new AuthenticationSettingsDto
        {
            LocalLoginMode = localMode,
            SessionAbsoluteLifetimeHours = config.GetValue("Authentication:SessionAbsoluteLifetimeHours", 8),
            MaxAuthorizationStalenessMinutes = config.GetValue("Authentication:MaxAuthorizationStalenessMinutes", 15),
            Ldap = new LdapAuthenticationDto
            {
                Enabled = ldap.Enabled,
                Server = ldap.Server,
                Endpoints = ldap.Endpoints.ToList(),
                Port = ldap.Port,
                UseSsl = ldap.UseSsl,
                BaseDn = ldap.BaseDn,
                UpnSuffix = ldap.UpnSuffix,
                BindTimeoutSeconds = ldap.BindTimeoutSeconds,
                ServiceBindDn = ldap.ServiceBindDn,
                ServicePassword = string.IsNullOrEmpty(ldap.ServicePassword) ? null : "********",
                AllowedGroupSids = ldap.AllowedGroupSids.ToList(),
                DirectorySyncIntervalMinutes = ldap.DirectorySyncIntervalMinutes,
                DirectorySyncMaxConcurrency = ldap.DirectorySyncMaxConcurrency,
                GlobalRoleMappings = ldap.GlobalRoleMappings
                    .Select(m => new GlobalRoleMappingDto { GroupSid = m.GroupSid, Role = m.Role })
                    .ToList(),
                JitUserDefaultRootRole = ldap.JitUserDefaultRootRole,
            },
            Windows = new WindowsAuthenticationDto
            {
                Enabled = win.Enabled,
                AllowNtlmFallback = win.AllowNtlmFallback,
                NtlmDisabledByPolicy = win.NtlmDisabledByPolicy,
            },
            Oidc = new OidcAuthenticationDto
            {
                Enabled = config.GetValue<bool>("Authentication:Oidc:Enabled"),
                Authority = config["Authentication:Oidc:Authority"],
                ClientId = config["Authentication:Oidc:ClientId"],
                ClientSecret = string.IsNullOrEmpty(config["Authentication:Oidc:ClientSecret"]) ? null : "********",
                DisplayName = config["Authentication:Oidc:DisplayName"] ?? "Single Sign-On",
                NameClaimType = config["Authentication:Oidc:NameClaimType"] ?? "preferred_username",
                GroupsClaimType = config["Authentication:Oidc:GroupsClaimType"] ?? "groups",
                Scopes = config.GetSection("Authentication:Oidc:Scopes").Get<List<string>>()
                    ?? ["openid", "profile", "email"],
                AllowedGroupIds = config.GetSection("Authentication:Oidc:AllowedGroupIds").Get<List<string>>() ?? [],
                GlobalRoleMappings = (config.GetSection("Authentication:Oidc:GlobalRoleMappings")
                    .Get<List<OidcRoleMapping>>() ?? [])
                    .Select(mapping => new OidcRoleMappingDto
                    {
                        GroupId = mapping.GroupId,
                        Role = mapping.Role,
                    }).ToList(),
            },
            Scim = new ScimAuthenticationDto
            {
                Enabled = config.GetValue<bool>("Authentication:Scim:Enabled"),
                BearerToken = string.IsNullOrEmpty(config["Authentication:Scim:BearerToken"]) ? null : "********",
                PreviousBearerToken = string.IsNullOrEmpty(config["Authentication:Scim:PreviousBearerToken"]) ? null : "********",
                Authority = config["Authentication:Scim:Authority"],
            },
        };
    }

    private static JsonObject BuildAuthenticationSectionObject(
        AuthenticationSettingsDto dto,
        JsonObject? previousSection,
        ISecretProtector protector)
    {
        var ldap = new JsonObject
        {
            ["Enabled"] = dto.Ldap.Enabled,
            ["Port"] = dto.Ldap.Port,
            ["UseSsl"] = dto.Ldap.UseSsl,
            ["BindTimeoutSeconds"] = dto.Ldap.BindTimeoutSeconds,
            ["DirectorySyncIntervalMinutes"] = dto.Ldap.DirectorySyncIntervalMinutes,
            ["DirectorySyncMaxConcurrency"] = dto.Ldap.DirectorySyncMaxConcurrency,
        };
        ldap["Endpoints"] = new JsonArray(dto.Ldap.Endpoints
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => JsonValue.Create(e.Trim()))
            .ToArray());
        ldap["AllowedGroupSids"] = new JsonArray(dto.Ldap.AllowedGroupSids
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => JsonValue.Create(s.Trim()))
            .ToArray());
        WriteOrExplicitNull(ldap, "Server", dto.Ldap.Server);
        WriteOrExplicitNull(ldap, "BaseDn", dto.Ldap.BaseDn);
        WriteOrExplicitNull(ldap, "UpnSuffix", dto.Ldap.UpnSuffix);
        WriteOrExplicitNull(ldap, "ServiceBindDn", dto.Ldap.ServiceBindDn);
        WriteSecretField(
            ldap,
            "ServicePassword",
            dto.Ldap.ServicePassword,
            previousSection?["Ldap"] as JsonObject ?? new JsonObject(),
            protector);

        var mappingsArray = new JsonArray();
        foreach (var m in dto.Ldap.GlobalRoleMappings)
        {
            mappingsArray.Add(new JsonObject
            {
                ["GroupSid"] = m.GroupSid,
                ["Role"] = m.Role.ToString(),
            });
        }
        ldap["GlobalRoleMappings"] = mappingsArray;
        ldap["JitUserDefaultRootRole"] = dto.Ldap.JitUserDefaultRootRole is null
            ? null
            : JsonValue.Create(dto.Ldap.JitUserDefaultRootRole.Value.ToString());

        var oidc = new JsonObject
        {
            ["Enabled"] = dto.Oidc.Enabled,
            ["DisplayName"] = dto.Oidc.DisplayName,
            ["NameClaimType"] = dto.Oidc.NameClaimType,
            ["GroupsClaimType"] = dto.Oidc.GroupsClaimType,
            ["Scopes"] = new JsonArray(dto.Oidc.Scopes
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["AllowedGroupIds"] = new JsonArray(dto.Oidc.AllowedGroupIds
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        };
        WriteOrExplicitNull(oidc, "Authority", dto.Oidc.Authority);
        WriteOrExplicitNull(oidc, "ClientId", dto.Oidc.ClientId);
        WriteSecretField(
            oidc, "ClientSecret", dto.Oidc.ClientSecret,
            previousSection?["Oidc"] as JsonObject ?? new JsonObject(), protector);
        oidc["GlobalRoleMappings"] = new JsonArray(dto.Oidc.GlobalRoleMappings
            .Select(mapping => (JsonNode)new JsonObject
            {
                ["GroupId"] = mapping.GroupId,
                ["Role"] = mapping.Role.ToString(),
            }).ToArray());

        var scim = new JsonObject { ["Enabled"] = dto.Scim.Enabled };
        WriteOrExplicitNull(scim, "Authority", dto.Scim.Authority);
        WriteSecretField(
            scim, "BearerToken", dto.Scim.BearerToken,
            previousSection?["Scim"] as JsonObject ?? new JsonObject(), protector);
        WriteSecretField(
            scim, "PreviousBearerToken", dto.Scim.PreviousBearerToken,
            previousSection?["Scim"] as JsonObject ?? new JsonObject(), protector);

        return new JsonObject
        {
            ["LocalLoginMode"] = dto.LocalLoginMode.ToString(),
            ["SessionAbsoluteLifetimeHours"] = dto.SessionAbsoluteLifetimeHours,
            ["MaxAuthorizationStalenessMinutes"] = dto.MaxAuthorizationStalenessMinutes,
            ["Ldap"] = ldap,
            ["Windows"] = new JsonObject
            {
                ["Enabled"] = dto.Windows.Enabled,
                ["AllowNtlmFallback"] = dto.Windows.AllowNtlmFallback,
                ["NtlmDisabledByPolicy"] = dto.Windows.NtlmDisabledByPolicy,
            },
            ["Oidc"] = oidc,
            ["Scim"] = scim,
        };
    }

    private static AuthenticationSettingsDto BuildAuthenticationDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        var ldap = section["Ldap"] as JsonObject ?? new JsonObject();
        var win = section["Windows"] as JsonObject ?? new JsonObject();
        var oidc = section["Oidc"] as JsonObject ?? new JsonObject();
        var scim = section["Scim"] as JsonObject ?? new JsonObject();

        var mappings = new List<GlobalRoleMappingDto>();
        if (ldap["GlobalRoleMappings"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject obj) continue;
                var sid = obj["GroupSid"]?.GetValue<string>() ?? "";
                var roleStr = obj["Role"]?.GetValue<string>();
                if (!Enum.TryParse<UserRole>(roleStr, ignoreCase: true, out var role))
                    role = UserRole.Viewer;
                mappings.Add(new GlobalRoleMappingDto { GroupSid = sid, Role = role });
            }
        }

        SharedFolderRole? jitRole = null;
        if (ldap["JitUserDefaultRootRole"] is JsonValue jitV
            && jitV.TryGetValue(out string? jitStr)
            && !string.IsNullOrEmpty(jitStr)
            && Enum.TryParse<SharedFolderRole>(jitStr, ignoreCase: true, out var jitParsed))
        {
            jitRole = jitParsed;
        }

        var oidcMappings = new List<OidcRoleMappingDto>();
        if (oidc["GlobalRoleMappings"] is JsonArray oidcMappingsArray)
        {
            foreach (var node in oidcMappingsArray.OfType<JsonObject>())
            {
                var groupId = node["GroupId"]?.GetValue<string>() ?? string.Empty;
                if (!Enum.TryParse<UserRole>(node["Role"]?.GetValue<string>(), true, out var mappedRole))
                    mappedRole = UserRole.Viewer;
                oidcMappings.Add(new OidcRoleMappingDto { GroupId = groupId, Role = mappedRole });
            }
        }

        return new AuthenticationSettingsDto
        {
            LocalLoginMode = Enum.TryParse<NodePilot.Api.Security.LocalLoginMode>(
                section["LocalLoginMode"]?.GetValue<string>(), true, out var localMode)
                ? localMode
                : NodePilot.Api.Security.LocalLoginMode.BreakGlassOnly,
            SessionAbsoluteLifetimeHours = section["SessionAbsoluteLifetimeHours"]?.GetValue<int>() ?? 8,
            MaxAuthorizationStalenessMinutes = section["MaxAuthorizationStalenessMinutes"]?.GetValue<int>() ?? 15,
            Ldap = new LdapAuthenticationDto
            {
                Enabled = ldap["Enabled"]?.GetValue<bool>() ?? false,
                Server = ReadNullableString(ldap, "Server"),
                Endpoints = ReadJsonStringArray(ldap, "Endpoints"),
                Port = ldap["Port"]?.GetValue<int>() ?? 636,
                UseSsl = ldap["UseSsl"]?.GetValue<bool>() ?? true,
                BaseDn = ReadNullableString(ldap, "BaseDn"),
                UpnSuffix = ReadNullableString(ldap, "UpnSuffix"),
                BindTimeoutSeconds = ldap["BindTimeoutSeconds"]?.GetValue<int>() ?? 5,
                ServiceBindDn = ReadNullableString(ldap, "ServiceBindDn"),
                ServicePassword = HasNonNullValue(ldap, "ServicePassword") ? "********" : null,
                AllowedGroupSids = ReadJsonStringArray(ldap, "AllowedGroupSids"),
                DirectorySyncIntervalMinutes = ldap["DirectorySyncIntervalMinutes"]?.GetValue<int>() ?? 5,
                DirectorySyncMaxConcurrency = ldap["DirectorySyncMaxConcurrency"]?.GetValue<int>() ?? 16,
                GlobalRoleMappings = mappings,
                JitUserDefaultRootRole = jitRole,
            },
            Windows = new WindowsAuthenticationDto
            {
                Enabled = win["Enabled"]?.GetValue<bool>() ?? false,
                AllowNtlmFallback = win["AllowNtlmFallback"]?.GetValue<bool>() ?? false,
                NtlmDisabledByPolicy = win["NtlmDisabledByPolicy"]?.GetValue<bool>() ?? false,
            },
            Oidc = new OidcAuthenticationDto
            {
                Enabled = oidc["Enabled"]?.GetValue<bool>() ?? false,
                Authority = ReadNullableString(oidc, "Authority"),
                ClientId = ReadNullableString(oidc, "ClientId"),
                ClientSecret = HasNonNullValue(oidc, "ClientSecret") ? "********" : null,
                DisplayName = oidc["DisplayName"]?.GetValue<string>() ?? "Single Sign-On",
                NameClaimType = oidc["NameClaimType"]?.GetValue<string>() ?? "preferred_username",
                GroupsClaimType = oidc["GroupsClaimType"]?.GetValue<string>() ?? "groups",
                Scopes = ReadJsonStringArray(oidc, "Scopes") is { Count: > 0 } scopes
                    ? scopes : ["openid", "profile", "email"],
                AllowedGroupIds = ReadJsonStringArray(oidc, "AllowedGroupIds"),
                GlobalRoleMappings = oidcMappings,
            },
            Scim = new ScimAuthenticationDto
            {
                Enabled = scim["Enabled"]?.GetValue<bool>() ?? false,
                BearerToken = HasNonNullValue(scim, "BearerToken") ? "********" : null,
                PreviousBearerToken = HasNonNullValue(scim, "PreviousBearerToken") ? "********" : null,
                Authority = ReadNullableString(scim, "Authority"),
            },
        };
    }

    private static List<string> ReadJsonStringArray(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is not JsonArray array) return [];
        return array
            .Select(node => node?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();
    }

    private static readonly string[] RestApiConfigKeys =
    [
        "RestApi:BlockPrivateNetworks",
        "RestApi:AllowedHosts",
        "RestApi:Proxy:Enabled", "RestApi:Proxy:Address", "RestApi:Proxy:BypassList",
        "RestApi:Proxy:Username", "RestApi:Proxy:Password",
    ];

    private static RestApiSettingsDto BuildRestApiDto(IConfigurationRoot configRoot) => new()
    {
        BlockPrivateNetworks = BoolDefaultTrue(configRoot["RestApi:BlockPrivateNetworks"]),
        AllowedHosts = ReadStringArray(configRoot, "RestApi:AllowedHosts"),
        Proxy = new RestApiProxyDto
        {
            Enabled = bool.TryParse(configRoot["RestApi:Proxy:Enabled"], out var p) && p,
            Address = configRoot["RestApi:Proxy:Address"] ?? "",
            Username = configRoot["RestApi:Proxy:Username"],
            Password = string.IsNullOrEmpty(configRoot["RestApi:Proxy:Password"]) ? null : "********",
            BypassList = ReadStringArray(configRoot, "RestApi:Proxy:BypassList"),
        },
    };

    private static JsonObject BuildRestApiSectionObject(
        RestApiSettingsDto dto,
        JsonObject? previousSection,
        ISecretProtector protector)
    {
        var proxy = new JsonObject
        {
            ["Enabled"] = dto.Proxy.Enabled,
            ["Address"] = dto.Proxy.Address,
            ["BypassList"] = ToJsonArray(dto.Proxy.BypassList),
        };
        WriteOrExplicitNull(proxy, "Username", dto.Proxy.Username);
        WriteSecretField(proxy, "Password", dto.Proxy.Password, previousSection?["Proxy"] as JsonObject ?? new JsonObject(), protector);
        return new JsonObject
        {
            ["BlockPrivateNetworks"] = dto.BlockPrivateNetworks,
            ["AllowedHosts"] = ToJsonArray(dto.AllowedHosts),
            ["Proxy"] = proxy,
        };
    }

    private static RestApiSettingsDto BuildRestApiDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        var proxy = section["Proxy"] as JsonObject ?? new JsonObject();
        return new RestApiSettingsDto
        {
            BlockPrivateNetworks = section["BlockPrivateNetworks"]?.GetValue<bool>() ?? true,
            AllowedHosts = ReadStringArray(section, "AllowedHosts"),
            Proxy = new RestApiProxyDto
            {
                Enabled = proxy["Enabled"]?.GetValue<bool>() ?? false,
                Address = proxy["Address"]?.GetValue<string>() ?? "",
                Username = ReadNullableString(proxy, "Username"),
                Password = HasNonNullValue(proxy, "Password") ? "********" : null,
                BypassList = ReadStringArray(proxy, "BypassList"),
            },
        };
    }

    private static FileSystemOperationSettingsDto BuildFileSystemOperationDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new FileSystemOperationSettingsDto
        {
            RejectTraversal = section["RejectTraversal"]?.GetValue<bool>() ?? true,
            AllowedRoots = ReadStringArray(section, "AllowedRoots"),
        };
    }

    private static SqlActivitySettingsDto BuildSqlActivityDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new SqlActivitySettingsDto
        {
            RequireConnectionRef = section["RequireConnectionRef"]?.GetValue<bool>() ?? true,
        };
    }

    private static StartProgramSettingsDto BuildStartProgramDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new StartProgramSettingsDto
        {
            DisallowShellExecute = section["DisallowShellExecute"]?.GetValue<bool>() ?? true,
        };
    }

    private static WebhookSettingsDto BuildWebhookDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new WebhookSettingsDto
        {
            RequireSecret = section["RequireSecret"]?.GetValue<bool>() ?? true,
        };
    }

    private static ExternalTriggerSettingsDto BuildExternalTriggerDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new ExternalTriggerSettingsDto
        {
            ApiKey = HasNonNullValue(section, "ApiKey") ? "********" : null,
        };
    }

    private static SecuritySettingsDto BuildSecurityDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new SecuritySettingsDto
        {
            StrictAllowedHosts = section["StrictAllowedHosts"]?.GetValue<bool>() ?? false,
            AllowedHosts = section["AllowedHosts"]?.GetValue<string>() ?? "*",
        };
    }

    private static readonly string[] EngineConfigKeys =
    [
        "Engine:Debug:MaxPauseMinutes",
        "Engine:MaxConcurrentExecutions:Global", "Engine:MaxConcurrentExecutions:PerUser",
        "Engine:MaxConcurrentSteps",
        "Engine:Runspace:MinRunspaces", "Engine:Runspace:MaxRunspaces",
    ];

    private static EngineSettingsDto BuildEngineDto(IConfigurationRoot configRoot) => new()
    {
        Debug = new DebugSettingsDto
        {
            MaxPauseMinutes = IntOr(configRoot["Engine:Debug:MaxPauseMinutes"], 10),
        },
        MaxConcurrentExecutions = new MaxConcurrentExecutionsDto
        {
            Global = IntOr(configRoot["Engine:MaxConcurrentExecutions:Global"], 5000),
            PerUser = IntOr(configRoot["Engine:MaxConcurrentExecutions:PerUser"], 2000),
        },
        MaxConcurrentSteps = IntOr(configRoot["Engine:MaxConcurrentSteps"], 600),
        Runspace = new RunspaceSettingsDto
        {
            MinRunspaces = IntOr(configRoot["Engine:Runspace:MinRunspaces"], 256),
            MaxRunspaces = IntOr(configRoot["Engine:Runspace:MaxRunspaces"], 768),
        },
    };

    private static JsonObject BuildEngineSectionObject(EngineSettingsDto dto) => new()
    {
        ["Debug"] = new JsonObject { ["MaxPauseMinutes"] = dto.Debug.MaxPauseMinutes },
        ["MaxConcurrentExecutions"] = new JsonObject
        {
            ["Global"] = dto.MaxConcurrentExecutions.Global,
            ["PerUser"] = dto.MaxConcurrentExecutions.PerUser,
        },
        ["MaxConcurrentSteps"] = dto.MaxConcurrentSteps,
        ["Runspace"] = new JsonObject
        {
            ["MinRunspaces"] = dto.Runspace.MinRunspaces,
            ["MaxRunspaces"] = dto.Runspace.MaxRunspaces,
        },
    };

    private static EngineSettingsDto BuildEngineDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        var dbg = section["Debug"] as JsonObject ?? new JsonObject();
        var mce = section["MaxConcurrentExecutions"] as JsonObject ?? new JsonObject();
        var rs = section["Runspace"] as JsonObject ?? new JsonObject();
        return new EngineSettingsDto
        {
            Debug = new DebugSettingsDto { MaxPauseMinutes = dbg["MaxPauseMinutes"]?.GetValue<int>() ?? 10 },
            MaxConcurrentExecutions = new MaxConcurrentExecutionsDto
            {
                Global = mce["Global"]?.GetValue<int>() ?? 5000,
                PerUser = mce["PerUser"]?.GetValue<int>() ?? 2000,
            },
            MaxConcurrentSteps = section["MaxConcurrentSteps"]?.GetValue<int>() ?? 600,
            Runspace = new RunspaceSettingsDto
            {
                MinRunspaces = rs["MinRunspaces"]?.GetValue<int>() ?? 256,
                MaxRunspaces = rs["MaxRunspaces"]?.GetValue<int>() ?? 768,
            },
        };
    }

    private static ExecutionDispatchSettingsDto BuildExecutionDispatchDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new ExecutionDispatchSettingsDto
        {
            Capacity = section["Capacity"]?.GetValue<int>() ?? 2048,
            WorkerCount = section["WorkerCount"]?.GetValue<int>() ?? 600,
        };
    }

    private static ThreadingSettingsDto BuildThreadingDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new ThreadingSettingsDto
        {
            MinWorkerThreads = section["MinWorkerThreads"]?.GetValue<int>() ?? 768,
            MinIoCompletionThreads = section["MinIoCompletionThreads"]?.GetValue<int>() ?? 768,
        };
    }

    private static readonly string[] RemoteConfigKeys =
    [
        "Remote:RequireWinRmSsl",
        "Remote:WinRm:OperationTimeoutSeconds", "Remote:WinRm:OpenTimeoutSeconds",
        "Remote:Pool:Enabled", "Remote:Pool:MaxConcurrentPerMachine",
        "Remote:Pool:MaxIdlePerKey", "Remote:Pool:IdleTtlSeconds",
    ];

    private static RemoteSettingsDto BuildRemoteDto(IConfigurationRoot configRoot) => new()
    {
        RequireWinRmSsl = BoolDefaultTrue(configRoot["Remote:RequireWinRmSsl"]),
        WinRm = new WinRmSubSettingsDto
        {
            OperationTimeoutSeconds = IntOr(configRoot["Remote:WinRm:OperationTimeoutSeconds"], 300),
            OpenTimeoutSeconds = IntOr(configRoot["Remote:WinRm:OpenTimeoutSeconds"], 30),
        },
        Pool = new RemotePoolDto
        {
            Enabled = BoolDefaultTrue(configRoot["Remote:Pool:Enabled"]),
            MaxConcurrentPerMachine = IntOr(configRoot["Remote:Pool:MaxConcurrentPerMachine"], 5),
            MaxIdlePerKey = IntOr(configRoot["Remote:Pool:MaxIdlePerKey"], 5),
            IdleTtlSeconds = IntOr(configRoot["Remote:Pool:IdleTtlSeconds"], 120),
        },
    };

    private static JsonObject BuildRemoteSectionObject(RemoteSettingsDto dto) => new()
    {
        ["RequireWinRmSsl"] = dto.RequireWinRmSsl,
        ["WinRm"] = new JsonObject
        {
            ["OperationTimeoutSeconds"] = dto.WinRm.OperationTimeoutSeconds,
            ["OpenTimeoutSeconds"] = dto.WinRm.OpenTimeoutSeconds,
        },
        ["Pool"] = new JsonObject
        {
            ["Enabled"] = dto.Pool.Enabled,
            ["MaxConcurrentPerMachine"] = dto.Pool.MaxConcurrentPerMachine,
            ["MaxIdlePerKey"] = dto.Pool.MaxIdlePerKey,
            ["IdleTtlSeconds"] = dto.Pool.IdleTtlSeconds,
        },
    };

    private static RemoteSettingsDto BuildRemoteDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        var winrm = section["WinRm"] as JsonObject ?? new JsonObject();
        var pool = section["Pool"] as JsonObject ?? new JsonObject();
        return new RemoteSettingsDto
        {
            RequireWinRmSsl = section["RequireWinRmSsl"]?.GetValue<bool>() ?? true,
            WinRm = new WinRmSubSettingsDto
            {
                OperationTimeoutSeconds = winrm["OperationTimeoutSeconds"]?.GetValue<int>() ?? 300,
                OpenTimeoutSeconds = winrm["OpenTimeoutSeconds"]?.GetValue<int>() ?? 30,
            },
            Pool = new RemotePoolDto
            {
                Enabled = pool["Enabled"]?.GetValue<bool>() ?? true,
                MaxConcurrentPerMachine = pool["MaxConcurrentPerMachine"]?.GetValue<int>() ?? 5,
                MaxIdlePerKey = pool["MaxIdlePerKey"]?.GetValue<int>() ?? 5,
                IdleTtlSeconds = pool["IdleTtlSeconds"]?.GetValue<int>() ?? 120,
            },
        };
    }

    private static readonly string[] LoggingConfigKeys =
    [
        "Logging:Format",
        "Logging:LogLevel:Default", "Logging:LogLevel:Microsoft.AspNetCore",
        "Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command",
        "Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Connection",
        "Logging:LogLevel:Microsoft.EntityFrameworkCore.Infrastructure",
        "Logging:StepDetail:Enabled", "Logging:StepDetail:MaxOutputChars",
        "Logging:File:RetainedFileCountLimit", "Logging:File:FileSizeLimitBytes", "Logging:File:Async",
        "Logging:Redaction:Enabled",
        "Logging:SupportLog:Enabled", "Logging:SupportLog:Path",
        "Logging:SupportLog:RetainedFileCountLimit", "Logging:SupportLog:FileSizeLimitBytes",
        "Logging:SupportLog:DbProjectionEnabled",
    ];

    private static LoggingSettingsDto BuildLoggingDto(IConfigurationRoot configRoot) => new()
    {
        Format = configRoot["Logging:Format"] ?? "text",
        LogLevel = new LogLevelsDto
        {
            Default = configRoot["Logging:LogLevel:Default"] ?? "Warning",
            AspNetCore = configRoot["Logging:LogLevel:Microsoft.AspNetCore"] ?? "Warning",
            EfCoreCommand = configRoot["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] ?? "Warning",
            EfCoreConnection = configRoot["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Connection"] ?? "Warning",
            EfCoreInfrastructure = configRoot["Logging:LogLevel:Microsoft.EntityFrameworkCore.Infrastructure"] ?? "Warning",
        },
        StepDetail = new StepDetailDto
        {
            Enabled = bool.TryParse(configRoot["Logging:StepDetail:Enabled"], out var s) && s,
            MaxOutputChars = IntOr(configRoot["Logging:StepDetail:MaxOutputChars"], 10_000),
        },
        File = new FileSinkDto
        {
            RetainedFileCountLimit = IntOr(configRoot["Logging:File:RetainedFileCountLimit"], 7),
            FileSizeLimitBytes = LongOr(configRoot["Logging:File:FileSizeLimitBytes"], 100L * 1024 * 1024),
            Async = BoolDefaultTrue(configRoot["Logging:File:Async"]),
        },
        Redaction = new RedactionDto { Enabled = BoolDefaultTrue(configRoot["Logging:Redaction:Enabled"]) },
        SupportLog = new SupportLogDto
        {
            Enabled = BoolDefaultTrue(configRoot["Logging:SupportLog:Enabled"]),
            Path = configRoot["Logging:SupportLog:Path"] ?? "",
            RetainedFileCountLimit = IntOr(configRoot["Logging:SupportLog:RetainedFileCountLimit"], 90),
            FileSizeLimitBytes = LongOr(configRoot["Logging:SupportLog:FileSizeLimitBytes"], 10L * 1024 * 1024),
            DbProjectionEnabled = BoolDefaultTrue(configRoot["Logging:SupportLog:DbProjectionEnabled"]),
        },
    };

    private static JsonObject BuildLoggingSectionObject(LoggingSettingsDto dto) => new()
    {
        ["Format"] = dto.Format,
        ["LogLevel"] = new JsonObject
        {
            ["Default"] = dto.LogLevel.Default,
            ["Microsoft.AspNetCore"] = dto.LogLevel.AspNetCore,
            ["Microsoft.EntityFrameworkCore.Database.Command"] = dto.LogLevel.EfCoreCommand,
            ["Microsoft.EntityFrameworkCore.Database.Connection"] = dto.LogLevel.EfCoreConnection,
            ["Microsoft.EntityFrameworkCore.Infrastructure"] = dto.LogLevel.EfCoreInfrastructure,
        },
        ["StepDetail"] = new JsonObject
        {
            ["Enabled"] = dto.StepDetail.Enabled,
            ["MaxOutputChars"] = dto.StepDetail.MaxOutputChars,
        },
        ["File"] = new JsonObject
        {
            ["RetainedFileCountLimit"] = dto.File.RetainedFileCountLimit,
            ["FileSizeLimitBytes"] = dto.File.FileSizeLimitBytes,
            ["Async"] = dto.File.Async,
        },
        ["Redaction"] = new JsonObject { ["Enabled"] = dto.Redaction.Enabled },
        ["SupportLog"] = new JsonObject
        {
            ["Enabled"] = dto.SupportLog.Enabled,
            ["Path"] = dto.SupportLog.Path,
            ["RetainedFileCountLimit"] = dto.SupportLog.RetainedFileCountLimit,
            ["FileSizeLimitBytes"] = dto.SupportLog.FileSizeLimitBytes,
            ["DbProjectionEnabled"] = dto.SupportLog.DbProjectionEnabled,
        },
    };

    private static LoggingSettingsDto BuildLoggingDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        var levels = section["LogLevel"] as JsonObject ?? new JsonObject();
        var sd = section["StepDetail"] as JsonObject ?? new JsonObject();
        var f = section["File"] as JsonObject ?? new JsonObject();
        var red = section["Redaction"] as JsonObject ?? new JsonObject();
        var sl = section["SupportLog"] as JsonObject ?? new JsonObject();
        return new LoggingSettingsDto
        {
            Format = section["Format"]?.GetValue<string>() ?? "text",
            LogLevel = new LogLevelsDto
            {
                Default = levels["Default"]?.GetValue<string>() ?? "Warning",
                AspNetCore = levels["Microsoft.AspNetCore"]?.GetValue<string>() ?? "Warning",
                EfCoreCommand = levels["Microsoft.EntityFrameworkCore.Database.Command"]?.GetValue<string>() ?? "Warning",
                EfCoreConnection = levels["Microsoft.EntityFrameworkCore.Database.Connection"]?.GetValue<string>() ?? "Warning",
                EfCoreInfrastructure = levels["Microsoft.EntityFrameworkCore.Infrastructure"]?.GetValue<string>() ?? "Warning",
            },
            StepDetail = new StepDetailDto
            {
                Enabled = sd["Enabled"]?.GetValue<bool>() ?? false,
                MaxOutputChars = sd["MaxOutputChars"]?.GetValue<int>() ?? 10_000,
            },
            File = new FileSinkDto
            {
                RetainedFileCountLimit = f["RetainedFileCountLimit"]?.GetValue<int>() ?? 7,
                FileSizeLimitBytes = f["FileSizeLimitBytes"]?.GetValue<long>() ?? 100L * 1024 * 1024,
                Async = f["Async"]?.GetValue<bool>() ?? true,
            },
            Redaction = new RedactionDto { Enabled = red["Enabled"]?.GetValue<bool>() ?? true },
            SupportLog = new SupportLogDto
            {
                Enabled = sl["Enabled"]?.GetValue<bool>() ?? true,
                Path = sl["Path"]?.GetValue<string>() ?? "",
                RetainedFileCountLimit = sl["RetainedFileCountLimit"]?.GetValue<int>() ?? 90,
                FileSizeLimitBytes = sl["FileSizeLimitBytes"]?.GetValue<long>() ?? 10L * 1024 * 1024,
                DbProjectionEnabled = sl["DbProjectionEnabled"]?.GetValue<bool>() ?? true,
            },
        };
    }

    private static readonly string[] OpenTelemetryConfigKeys =
    [
        "OpenTelemetry:Enabled", "OpenTelemetry:ServiceName", "OpenTelemetry:Environment",
        "OpenTelemetry:RedactHostnames", "OpenTelemetry:MetricExportIntervalSeconds",
        "OpenTelemetry:Otlp:Endpoint", "OpenTelemetry:Otlp:Protocol",
        "OpenTelemetry:Otlp:Headers", "OpenTelemetry:Otlp:BrowserEndpoint",
        "OpenTelemetry:Sampling:Mode", "OpenTelemetry:Sampling:Ratio",
        "OpenTelemetry:Exporters:Traces", "OpenTelemetry:Exporters:Metrics", "OpenTelemetry:Exporters:Logs",
        "OpenTelemetry:Exporters:PrometheusScrape", "OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous",
        "OpenTelemetry:TraceUi:UrlTemplate", "OpenTelemetry:TraceUi:BackendName",
        "OpenTelemetry:Prometheus:QueryEndpoint", "OpenTelemetry:Prometheus:Username",
        "OpenTelemetry:Prometheus:Password", "OpenTelemetry:Prometheus:BearerToken",
        "OpenTelemetry:Prometheus:TimeoutSeconds",
        "OpenTelemetry:GrafanaBaseUrl",
    ];

    private static OpenTelemetrySettingsDto BuildOpenTelemetryDto(
        NodePilotTelemetryOptions t,
        IConfigurationRoot configRoot)
    {
        return new OpenTelemetrySettingsDto
        {
            Enabled = t.Enabled,
            ServiceName = t.ServiceName ?? "nodepilot-api",
            Environment = t.Environment ?? "dev",
            RedactHostnames = t.RedactHostnames,
            MetricExportIntervalSeconds = t.MetricExportIntervalSeconds,
            Otlp = new OtlpSettingsDto
            {
                Endpoint = t.Otlp.Endpoint ?? "http://localhost:4317",
                Protocol = t.Otlp.Protocol ?? "grpc",
                Headers = t.Otlp.Headers ?? "",
                BrowserEndpoint = configRoot["OpenTelemetry:Otlp:BrowserEndpoint"] ?? "",
            },
            Sampling = new SamplingSettingsDto
            {
                Mode = t.Sampling.Mode ?? "ParentBasedTraceIdRatio",
                Ratio = t.Sampling.Ratio,
            },
            Exporters = new ExportersSettingsDto
            {
                Traces = t.Exporters.Traces,
                Metrics = t.Exporters.Metrics,
                Logs = t.Exporters.Logs,
                PrometheusScrape = t.Exporters.PrometheusScrape,
                PrometheusScrapeAllowAnonymous = bool.TryParse(
                    configRoot["OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous"], out var psa) && psa,
            },
            TraceUi = new TraceUiSettingsDto
            {
                UrlTemplate = t.TraceUi.UrlTemplate ?? "",
                BackendName = t.TraceUi.BackendName ?? "Tempo",
            },
            Prometheus = new PrometheusSettingsDto
            {
                QueryEndpoint = t.Prometheus.QueryEndpoint ?? "",
                Username = t.Prometheus.Username ?? "",
                Password = string.IsNullOrEmpty(t.Prometheus.Password) ? null : "********",
                BearerToken = string.IsNullOrEmpty(t.Prometheus.BearerToken) ? null : "********",
                TimeoutSeconds = t.Prometheus.TimeoutSeconds,
            },
            GrafanaBaseUrl = t.GrafanaBaseUrl ?? "",
        };
    }

    private static JsonObject BuildOpenTelemetrySectionObject(
        OpenTelemetrySettingsDto dto,
        JsonObject? previousSection,
        ISecretProtector protector)
    {
        var prevProm = previousSection?["Prometheus"] as JsonObject ?? new JsonObject();
        var prom = new JsonObject
        {
            ["QueryEndpoint"] = dto.Prometheus.QueryEndpoint,
            ["Username"] = dto.Prometheus.Username,
            ["TimeoutSeconds"] = dto.Prometheus.TimeoutSeconds,
        };
        WriteSecretField(prom, "Password", dto.Prometheus.Password, prevProm, protector);
        WriteSecretField(prom, "BearerToken", dto.Prometheus.BearerToken, prevProm, protector);

        return new JsonObject
        {
            ["Enabled"] = dto.Enabled,
            ["ServiceName"] = dto.ServiceName,
            ["Environment"] = dto.Environment,
            ["RedactHostnames"] = dto.RedactHostnames,
            ["MetricExportIntervalSeconds"] = dto.MetricExportIntervalSeconds,
            ["Otlp"] = new JsonObject
            {
                ["Endpoint"] = dto.Otlp.Endpoint,
                ["Protocol"] = dto.Otlp.Protocol,
                ["Headers"] = dto.Otlp.Headers,
                ["BrowserEndpoint"] = dto.Otlp.BrowserEndpoint,
            },
            ["Sampling"] = new JsonObject
            {
                ["Mode"] = dto.Sampling.Mode,
                ["Ratio"] = dto.Sampling.Ratio,
            },
            ["Exporters"] = new JsonObject
            {
                ["Traces"] = dto.Exporters.Traces,
                ["Metrics"] = dto.Exporters.Metrics,
                ["Logs"] = dto.Exporters.Logs,
                ["PrometheusScrape"] = dto.Exporters.PrometheusScrape,
                ["PrometheusScrapeAllowAnonymous"] = dto.Exporters.PrometheusScrapeAllowAnonymous,
            },
            ["TraceUi"] = new JsonObject
            {
                ["UrlTemplate"] = dto.TraceUi.UrlTemplate,
                ["BackendName"] = dto.TraceUi.BackendName,
            },
            ["Prometheus"] = prom,
            ["GrafanaBaseUrl"] = dto.GrafanaBaseUrl,
        };
    }

    private static OpenTelemetrySettingsDto BuildOpenTelemetryDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        var otlp = section["Otlp"] as JsonObject ?? new JsonObject();
        var samp = section["Sampling"] as JsonObject ?? new JsonObject();
        var exp = section["Exporters"] as JsonObject ?? new JsonObject();
        var tu = section["TraceUi"] as JsonObject ?? new JsonObject();
        var prom = section["Prometheus"] as JsonObject ?? new JsonObject();
        return new OpenTelemetrySettingsDto
        {
            Enabled = section["Enabled"]?.GetValue<bool>() ?? false,
            ServiceName = section["ServiceName"]?.GetValue<string>() ?? "nodepilot-api",
            Environment = section["Environment"]?.GetValue<string>() ?? "dev",
            RedactHostnames = section["RedactHostnames"]?.GetValue<bool>() ?? true,
            MetricExportIntervalSeconds = section["MetricExportIntervalSeconds"]?.GetValue<int>() ?? 30,
            Otlp = new OtlpSettingsDto
            {
                Endpoint = otlp["Endpoint"]?.GetValue<string>() ?? "http://localhost:4317",
                Protocol = otlp["Protocol"]?.GetValue<string>() ?? "grpc",
                Headers = otlp["Headers"]?.GetValue<string>() ?? "",
                BrowserEndpoint = otlp["BrowserEndpoint"]?.GetValue<string>() ?? "",
            },
            Sampling = new SamplingSettingsDto
            {
                Mode = samp["Mode"]?.GetValue<string>() ?? "ParentBasedTraceIdRatio",
                Ratio = samp["Ratio"]?.GetValue<double>() ?? 1.0,
            },
            Exporters = new ExportersSettingsDto
            {
                Traces = exp["Traces"]?.GetValue<bool>() ?? true,
                Metrics = exp["Metrics"]?.GetValue<bool>() ?? true,
                Logs = exp["Logs"]?.GetValue<bool>() ?? true,
                PrometheusScrape = exp["PrometheusScrape"]?.GetValue<bool>() ?? false,
                PrometheusScrapeAllowAnonymous = exp["PrometheusScrapeAllowAnonymous"]?.GetValue<bool>() ?? false,
            },
            TraceUi = new TraceUiSettingsDto
            {
                UrlTemplate = tu["UrlTemplate"]?.GetValue<string>() ?? "",
                BackendName = tu["BackendName"]?.GetValue<string>() ?? "Tempo",
            },
            Prometheus = new PrometheusSettingsDto
            {
                QueryEndpoint = prom["QueryEndpoint"]?.GetValue<string>() ?? "",
                Username = prom["Username"]?.GetValue<string>() ?? "",
                Password = HasNonNullValue(prom, "Password") ? "********" : null,
                BearerToken = HasNonNullValue(prom, "BearerToken") ? "********" : null,
                TimeoutSeconds = prom["TimeoutSeconds"]?.GetValue<int>() ?? 10,
            },
            GrafanaBaseUrl = section["GrafanaBaseUrl"]?.GetValue<string>() ?? "",
        };
    }

    private static StatsSettingsDto BuildStatsDto(IConfigurationRoot configRoot) => new()
    {
        RefreshIntervalMinutes = IntOr(configRoot["Stats:RefreshIntervalMinutes"], 5),
        WindowDays = IntOr(configRoot["Stats:WindowDays"], 7),
    };

    private static StatsSettingsDto BuildStatsDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new StatsSettingsDto
        {
            RefreshIntervalMinutes = section["RefreshIntervalMinutes"]?.GetValue<int>() ?? 5,
            WindowDays = section["WindowDays"]?.GetValue<int>() ?? 7,
        };
    }

    // DbAdmin defaults must mirror DbAdminOptions exactly — they live in two places
    // (the DI-bound POCO and this UI adapter) but represent the same setting. When
    // changing a default, update both.
    private static DbAdminSettingsDto BuildDbAdminDto(IConfigurationRoot configRoot) => new()
    {
        AllowWriteQueries = BoolDefaultFalse(configRoot["DbAdmin:AllowWriteQueries"]),
        QueryTimeoutSeconds = IntOr(configRoot["DbAdmin:QueryTimeoutSeconds"], 30),
        QueryMaxRows = IntOr(configRoot["DbAdmin:QueryMaxRows"], 10_000),
    };

    private static DbAdminSettingsDto BuildDbAdminDtoFromJson(JsonObject? section)
    {
        section ??= new JsonObject();
        return new DbAdminSettingsDto
        {
            AllowWriteQueries = section["AllowWriteQueries"]?.GetValue<bool>() ?? false,
            QueryTimeoutSeconds = section["QueryTimeoutSeconds"]?.GetValue<int>() ?? 30,
            QueryMaxRows = section["QueryMaxRows"]?.GetValue<int>() ?? 10_000,
        };
    }

    private static bool HasNonNullValue(JsonObject section, string key)
    {
        if (!section.ContainsKey(key)) return false;
        return section[key] is not null;
    }

    private static string? ReadNullableString(JsonObject section, string key)
    {
        if (section[key] is JsonValue v && v.TryGetValue(out string? s)) return s;
        return null;
    }

    private static void WriteOrExplicitNull(JsonObject section, string key, string? value)
    {
        section[key] = string.IsNullOrEmpty(value) ? null : JsonValue.Create(value);
    }

    private static void WriteSecretField(
        JsonObject section,
        string key,
        string? incoming,
        JsonObject previousSection,
        ISecretProtector protector)
    {
        if (string.Equals(incoming, SettingsSchema.UnchangedSecretSentinel, StringComparison.Ordinal))
        {
            if (previousSection[key] is JsonValue prev && prev.TryGetValue(out string? prevStr))
                section[key] = prevStr;
        }
        else if (!string.IsNullOrEmpty(incoming))
        {
            section[key] = EncryptingJsonConfigurationProvider.EncryptForPersist(incoming, protector);
        }
        else
        {
            section[key] = null;
        }
    }

    private static List<string> ReadStringArray(IConfigurationRoot configRoot, string sectionKey)
    {
        var list = new List<string>();
        var section = configRoot.GetSection(sectionKey);
        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrEmpty(child.Value)) list.Add(child.Value);
        }
        return list;
    }

    private static List<string> ReadStringArray(JsonObject section, string key)
    {
        var list = new List<string>();
        if (section[key] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s))
                    list.Add(s);
            }
        }
        return list;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var value in values) arr.Add(value);
        return arr;
    }

    private static bool BoolDefaultTrue(string? raw)
        => !bool.TryParse(raw, out var parsed) || parsed;

    private static bool BoolDefaultFalse(string? raw)
        => bool.TryParse(raw, out var parsed) && parsed;

    private static int IntOr(string? raw, int fallback)
        => int.TryParse(raw, out var parsed) ? parsed : fallback;

    private static long LongOr(string? raw, long fallback)
        => long.TryParse(raw, out var parsed) ? parsed : fallback;
}
