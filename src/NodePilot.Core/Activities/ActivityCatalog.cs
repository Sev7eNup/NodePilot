namespace NodePilot.Core.Activities;

public enum ActivityCategory
{
    Trigger,
    Action,
    ControlFlow,
    Logic,
}

public enum ActivityTimeoutKind
{
    None,
    Always,
    WhenWaitForExit,
    WhenWaitForCompletion,
}

public sealed record ActivityOutputParameterDescriptor(string Name, string Type);

public sealed record ActivityPromptDescriptor(bool IsIncluded, string? ExclusionReason)
{
    public static readonly ActivityPromptDescriptor Included = new(true, null);

    public static ActivityPromptDescriptor Excluded(string reason) => new(false, reason);
}

public sealed record ActivityDescriptor(string Type, ActivityCategory Category, string LabelKey, string Icon)
{
    public bool IsTrigger => Category == ActivityCategory.Trigger;
    public bool IsExternalTrigger { get; init; }
    public bool IsRemote { get; init; }
    public ActivityTimeoutKind Timeout { get; init; } = ActivityTimeoutKind.None;
    public IReadOnlyList<ActivityOutputParameterDescriptor> OutputParameters { get; init; } = [];
    public IReadOnlyList<string> TelemetryParameters { get; init; } = [];
    public ActivityPromptDescriptor Prompt { get; init; } = ActivityPromptDescriptor.Included;
}

/// <summary>
/// Backend-owned catalog of stable activity facts. Executor configuration schemas remain
/// activity-owned; this catalog only contains cross-cutting metadata that multiple modules
/// need to agree on.
/// </summary>
public static class ActivityCatalog
{
    private static readonly ActivityDescriptor[] _all =
    [
        Action("runScript", "runScript", "terminal",
            isRemote: true,
            timeout: ActivityTimeoutKind.Always,
            // Always-present exit code: the wrapper captures $LASTEXITCODE (reconciled with the
            // real process exit code on the process/isolated path). User `$var = …` params remain
            // dynamic (scanned client-side); exitCode is the one static output.
            outputs:
            [
                Output("exitCode", "number"),
            ]),
        Action("fileOperation", "fileOperation", "description",
            isRemote: true,
            timeout: ActivityTimeoutKind.Always,
            outputs:
            [
                Output("operation", "string"),
                Output("path", "string"),
                Output("destination", "string"),
                Output("newPath", "string"),
                Output("newName", "string"),
                Output("exists", "boolean"),
                Output("fullName", "string"),
                Output("creationTime", "string"),
            ],
            telemetry: ["operation", "exists"]),
        Action("folderOperation", "folderOperation", "folder_open",
            isRemote: true,
            timeout: ActivityTimeoutKind.Always,
            outputs:
            [
                Output("operation", "string"),
                Output("path", "string"),
                Output("destination", "string"),
                Output("newPath", "string"),
                Output("newName", "string"),
                Output("exists", "boolean"),
                Output("fullName", "string"),
                Output("creationTime", "string"),
                Output("items", "array"),
                Output("count", "number"),
                Output("truncated", "boolean"),
            ],
            telemetry: ["operation", "exists", "count"]),
        Action("fileHash", "fileHash", "tag",
            isRemote: true,
            timeout: ActivityTimeoutKind.Always,
            outputs:
            [
                Output("hash", "string"),
                Output("algorithm", "string"),
                Output("match", "boolean"),
            ],
            telemetry: ["algorithm", "match"],
            prompt: ActivityPromptDescriptor.Excluded(
                "Niche file comparison activity; prompt examples are needed before LLM generation is reliable.")),
        Action("zipOperation", "zipOperation", "archive",
            isRemote: true,
            timeout: ActivityTimeoutKind.Always,
            outputs:
            [
                Output("destination", "string"),
                Output("sizeBytes", "number"),
            ],
            telemetry: ["sizeBytes", "operation"],
            prompt: ActivityPromptDescriptor.Excluded(
                "Archive operations have a comparatively complex config surface; defer until prompt examples exist.")),
        Action("serviceManagement", "serviceManagement", "settings",
            isRemote: true,
            timeout: ActivityTimeoutKind.Always,
            outputs:
            [
                Output("name", "string"),
                Output("status", "string"),
                Output("startType", "string"),
            ],
            telemetry: ["action", "status"]),
        Action("scheduledTask", "scheduledTask", "pending_actions",
            isRemote: true,
            timeout: ActivityTimeoutKind.Always,
            outputs:
            [
                Output("taskName", "string"),
                Output("state", "string"),
                Output("lastRunTime", "string"),
                Output("lastTaskResult", "number"),
                Output("nextRunTime", "string"),
            ],
            telemetry: ["state", "lastTaskResult", "action"],
            prompt: ActivityPromptDescriptor.Excluded(
                "Niche Windows Task Scheduler operations; defer until prompt has dedicated examples.")),
        Action("registryOperation", "registryOperation", "database",
            isRemote: true,
            timeout: ActivityTimeoutKind.Always,
            telemetry: ["operation", "exists", "count", "created", "type"]),
        Action("wmiQuery", "wmiQuery", "hard_drive",
            isRemote: true,
            timeout: ActivityTimeoutKind.Always,
            telemetry: ["count"]),
        // wmiQuery has NO static outputParameters: per-property params (`Caption`,
        // `SMBIOSBIOSVersion`, …) and the auto-emitted `count` are *dynamic* — they
        // only exist when the workflow author sets `captureProperties`. The UI's
        // upstream-variables helper inspects each node's captureProperties at design
        // time and surfaces them in the variable picker. Keeping them out of the
        // static catalog avoids promising params that the runtime won't emit when
        // captureProperties is absent (legacy raw-output mode).
        Action("startProgram", "startProgram", "rocket_launch",
            isRemote: true,
            timeout: ActivityTimeoutKind.WhenWaitForExit,
            outputs:
            [
                Output("exitCode", "number"),
                Output("processId", "number"),
                Output("stdout", "string"),
                Output("stderr", "string"),
                Output("waited", "boolean"),
            ],
            telemetry: ["exitCode", "processId"]),
        Action("powerManagement", "powerManagement", "power_settings_new",
            isRemote: true,
            timeout: ActivityTimeoutKind.Always,
            telemetry: ["action"]),
        Action("waitForCondition", "waitForCondition", "hourglass_top",
            isRemote: true,
            timeout: ActivityTimeoutKind.Always,
            outputs:
            [
                Output("attempts", "number"),
                Output("elapsedSeconds", "number"),
                Output("lastResult", "boolean"),
            ],
            prompt: ActivityPromptDescriptor.Excluded(
                "Polling semantics need dedicated examples before LLM generation is reliable.")),
        Action("restApi", "restApi", "language",
            timeout: ActivityTimeoutKind.Always,
            outputs:
            [
                Output("statusCode", "number"),
            ],
            telemetry: ["status", "statusCode", "method", "proxyMode"]),
        Action("sql", "sql", "storage",
            timeout: ActivityTimeoutKind.Always,
            outputs:
            [
                Output("rowCount", "number"),
                Output("rowsAffected", "number"),
            ],
            telemetry: ["rowCount", "rowsAffected", "provider"]),
        Action("xmlQuery", "xmlQuery", "code",
            outputs:
            [
                Output("result", "string"),
                Output("count", "number"),
            ],
            telemetry: ["count"]),
        Action("jsonQuery", "jsonQuery", "data_object",
            outputs:
            [
                Output("result", "string"),
                Output("count", "number"),
            ],
            telemetry: ["count"]),
        Action("emailNotification", "emailNotification", "mail",
            timeout: ActivityTimeoutKind.Always),
        Action("textFileEdit", "textFileEdit", "edit_note",
            isRemote: true,
            timeout: ActivityTimeoutKind.Always,
            outputs:
            [
                Output("operation", "string"),
                Output("path", "string"),
                Output("linesBefore", "number"),
                Output("linesAfter", "number"),
                Output("linesChanged", "number"),
                Output("encoding", "string"),
                Output("lineEnding", "string"),
                Output("backupPath", "string"),
                Output("dryRun", "boolean"),
            ],
            telemetry: ["operation", "linesChanged", "dryRun"]),
        Action("generateText", "generateText", "casino",
            outputs:
            [
                Output("text", "string"),
                Output("length", "number"),
            ]),
        Action("llmQuery", "llmQuery", "smart_toy",
            timeout: ActivityTimeoutKind.Always,
            // Token/finishReason are static outputs: the executor ALWAYS emits these keys
            // (empty string when the server omits `usage`), so downstream templates never break.
            outputs:
            [
                Output("model", "string"),
                Output("promptTokens", "number"),
                Output("completionTokens", "number"),
                Output("totalTokens", "number"),
                Output("finishReason", "string"),
            ],
            // Telemetry: model only — never the prompt/baseUrl/apiKey.
            telemetry: ["model"],
            prompt: ActivityPromptDescriptor.Excluded(
                "LLM-Aufruf-Activity – nicht für Workflow-Auto-Generierung vorgesehen.")),

        Logic("log", "log", "note_add",
            outputs:
            [
                Output("level", "string"),
                Output("message", "string"),
            ]),
        Logic("delay", "delay", "schedule"),

        ControlFlow("junction", "junction", "merge",
            telemetry: ["mode", "satisfied"]),
        ControlFlow("startWorkflow", "startWorkflow", "play_circle",
            timeout: ActivityTimeoutKind.WhenWaitForCompletion,
            outputs:
            [
                Output("__executionId", "string"),
                Output("__status", "string"),
                Output("__workflowId", "string"),
                Output("__workflowName", "string"),
            ],
            telemetry: ["waited", "__status"]),
        ControlFlow("forEach", "forEach", "loop",
            outputs:
            [
                Output("total", "number"),
                Output("succeeded", "number"),
                Output("failed", "number"),
                Output("skipped", "number"),
                Output("results", "array"),
                Output("firstError", "string"),
            ],
            prompt: ActivityPromptDescriptor.Excluded(
                "Loop construct; prompt examples are needed before LLM generation is reliable.")),
        ControlFlow("decision", "decision", "call_split",
            outputs:
            [
                Output("case", "string"),
                Output("matched", "boolean"),
                Output("reason", "string"),
            ],
            telemetry: ["case", "matched", "reason"],
            prompt: ActivityPromptDescriptor.Excluded(
                "Edge conditions already cover branch routing in the prompt; decision nodes are withheld for now.")),
        ControlFlow("returnData", "returnData", "reply"),

        Trigger("manualTrigger", "manualTrigger", "touch_app"),
        Trigger("scheduleTrigger", "scheduleTrigger", "schedule", isExternalTrigger: true,
            outputs:
            [
                Output("firedAt", "string"),
                Output("nextFireAt", "string"),
            ]),
        Trigger("webhookTrigger", "webhookTrigger", "webhook", isExternalTrigger: true,
            outputs:
            [
                // Names match WebhooksController.cs payload — dot-free so the
                // {{step.param.X}} template regex (no nested dots in the param tail) can
                // match them. Header/query keys add a `webhookHeader_` / `webhookQuery_`
                // prefix dynamically at run time and aren't enumerable up front, so they're
                // documented in CLAUDE.md instead of declared here.
                Output("webhookBody", "string"),
                Output("webhookMethod", "string"),
                Output("webhookPath", "string"),
            ]),
        Trigger("fileWatcherTrigger", "fileWatcherTrigger", "folder_supervised", isExternalTrigger: true,
            outputs:
            [
                Output("fileAction", "string"),
                Output("filePath", "string"),
                Output("fileName", "string"),
            ]),
        Trigger("databaseTrigger", "databaseTrigger", "database", isExternalTrigger: true,
            outputs:
            [
                Output("dbSentinel", "string"),
                Output("dbPrevious", "string"),
            ]),
        Trigger("eventLogTrigger", "eventLogTrigger", "event_note", isExternalTrigger: true,
            outputs:
            [
                Output("eventSource", "string"),
                Output("eventEntryType", "string"),
                Output("eventId", "string"),
                Output("eventMessage", "string"),
                Output("eventTimeWritten", "string"),
            ]),
    ];

    private static readonly IReadOnlyDictionary<string, ActivityDescriptor> _byType =
        _all.ToDictionary(a => a.Type, StringComparer.Ordinal);

    public static IReadOnlyList<ActivityDescriptor> All => _all;

    public static IReadOnlyDictionary<string, ActivityDescriptor> ByType => _byType;

    public static IReadOnlySet<string> TriggerTypes { get; } =
        _all.Where(a => a.IsTrigger).Select(a => a.Type).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> ExternalTriggerTypes { get; } =
        _all.Where(a => a.IsExternalTrigger).Select(a => a.Type).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> RemoteTypes { get; } =
        _all.Where(a => a.IsRemote).Select(a => a.Type).ToHashSet(StringComparer.Ordinal);

    public static bool TryGet(string? activityType, out ActivityDescriptor? descriptor)
    {
        if (activityType is null)
        {
            descriptor = null;
            return false;
        }

        return _byType.TryGetValue(activityType, out descriptor);
    }

    public static ActivityDescriptor GetRequired(string activityType) =>
        _byType.TryGetValue(activityType, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"Unknown activity type '{activityType}'.");

    private static ActivityOutputParameterDescriptor Output(string name, string type) => new(name, type);

    private static ActivityDescriptor Trigger(
        string type,
        string labelKey,
        string icon,
        bool isExternalTrigger = false,
        IReadOnlyList<ActivityOutputParameterDescriptor>? outputs = null) =>
        new(type, ActivityCategory.Trigger, labelKey, icon)
        {
            IsExternalTrigger = isExternalTrigger,
            OutputParameters = outputs ?? [],
        };

    private static ActivityDescriptor Action(
        string type,
        string labelKey,
        string icon,
        bool isRemote = false,
        ActivityTimeoutKind timeout = ActivityTimeoutKind.None,
        IReadOnlyList<ActivityOutputParameterDescriptor>? outputs = null,
        IReadOnlyList<string>? telemetry = null,
        ActivityPromptDescriptor? prompt = null) =>
        new(type, ActivityCategory.Action, labelKey, icon)
        {
            IsRemote = isRemote,
            Timeout = timeout,
            OutputParameters = outputs ?? [],
            TelemetryParameters = telemetry ?? [],
            Prompt = prompt ?? ActivityPromptDescriptor.Included,
        };

    private static ActivityDescriptor Logic(
        string type,
        string labelKey,
        string icon,
        IReadOnlyList<ActivityOutputParameterDescriptor>? outputs = null) =>
        new(type, ActivityCategory.Logic, labelKey, icon)
        {
            OutputParameters = outputs ?? [],
        };

    private static ActivityDescriptor ControlFlow(
        string type,
        string labelKey,
        string icon,
        ActivityTimeoutKind timeout = ActivityTimeoutKind.None,
        IReadOnlyList<ActivityOutputParameterDescriptor>? outputs = null,
        IReadOnlyList<string>? telemetry = null,
        ActivityPromptDescriptor? prompt = null) =>
        new(type, ActivityCategory.ControlFlow, labelKey, icon)
        {
            Timeout = timeout,
            OutputParameters = outputs ?? [],
            TelemetryParameters = telemetry ?? [],
            Prompt = prompt ?? ActivityPromptDescriptor.Included,
        };
}
