// HAND-MAINTAINED MIRROR — despite the filename, there is NO codegen step.
//
// This file mirrors the backend catalog `NodePilot.Core.Activities.ActivityCatalog` by hand
// (per CLAUDE.md convention). Edit it directly whenever the backend catalog changes; the
// backend parity test `ActivityCatalogFrontendSyncTests` (NodePilot.Engine.Tests) parses this
// file and fails CI on any drift in types, categories, flags, or derived sets.
//
// The derived exports at the bottom (ACTIVITY_TYPES, REMOTE_ACTIVITY_TYPES,
// TIMEOUT_ACTIVITY_TYPES, …) are computed from ACTIVITY_CATALOG — extend the array, never
// the derived sets. Custom activities (`custom:<key>`) are deliberately NOT in here: their
// catalog is runtime data (lib/customActivities.ts, hydrated via useCustomActivityCatalog).
export const ACTIVITY_CATALOG = [
  {
    "type": "runScript",
    "category": "action",
    "labelKey": "runScript",
    "icon": "terminal",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [
      { "name": "exitCode", "type": "number" }
    ],
    "telemetryParameters": [],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "fileOperation",
    "category": "action",
    "labelKey": "fileOperation",
    "icon": "description",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [
      { "name": "operation", "type": "string" },
      { "name": "path", "type": "string" },
      { "name": "destination", "type": "string" },
      { "name": "newPath", "type": "string" },
      { "name": "newName", "type": "string" },
      { "name": "exists", "type": "boolean" },
      { "name": "fullName", "type": "string" },
      { "name": "creationTime", "type": "string" }
    ],
    "telemetryParameters": ["operation", "exists"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "folderOperation",
    "category": "action",
    "labelKey": "folderOperation",
    "icon": "folder_open",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [
      { "name": "operation", "type": "string" },
      { "name": "path", "type": "string" },
      { "name": "destination", "type": "string" },
      { "name": "newPath", "type": "string" },
      { "name": "newName", "type": "string" },
      { "name": "exists", "type": "boolean" },
      { "name": "fullName", "type": "string" },
      { "name": "creationTime", "type": "string" },
      { "name": "items", "type": "array" },
      { "name": "count", "type": "number" },
      { "name": "truncated", "type": "boolean" }
    ],
    "telemetryParameters": ["operation", "exists", "count"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "fileHash",
    "category": "action",
    "labelKey": "fileHash",
    "icon": "tag",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [
      { "name": "hash", "type": "string" },
      { "name": "algorithm", "type": "string" },
      { "name": "match", "type": "boolean" }
    ],
    "telemetryParameters": ["algorithm", "match"],
    "prompt": { "included": false, "exclusionReason": "Niche file comparison activity; prompt examples are needed before LLM generation is reliable." }
  },
  {
    "type": "zipOperation",
    "category": "action",
    "labelKey": "zipOperation",
    "icon": "archive",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [
      { "name": "destination", "type": "string" },
      { "name": "sizeBytes", "type": "number" }
    ],
    "telemetryParameters": ["sizeBytes", "operation"],
    "prompt": { "included": false, "exclusionReason": "Archive operations have a comparatively complex config surface; defer until prompt examples exist." }
  },
  {
    "type": "serviceManagement",
    "category": "action",
    "labelKey": "serviceManagement",
    "icon": "settings",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [
      { "name": "name", "type": "string" },
      { "name": "status", "type": "string" },
      { "name": "startType", "type": "string" }
    ],
    "telemetryParameters": ["action", "status"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "scheduledTask",
    "category": "action",
    "labelKey": "scheduledTask",
    "icon": "pending_actions",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [
      { "name": "taskName", "type": "string" },
      { "name": "state", "type": "string" },
      { "name": "lastRunTime", "type": "string" },
      { "name": "lastTaskResult", "type": "number" },
      { "name": "nextRunTime", "type": "string" }
    ],
    "telemetryParameters": ["state", "lastTaskResult", "action"],
    "prompt": { "included": false, "exclusionReason": "Niche Windows Task Scheduler operations; defer until prompt has dedicated examples." }
  },
  {
    "type": "registryOperation",
    "category": "action",
    "labelKey": "registryOperation",
    "icon": "database",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [],
    "telemetryParameters": ["operation", "exists", "count", "created", "type"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "wmiQuery",
    "category": "action",
    "labelKey": "wmiQuery",
    "icon": "hard_drive",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [],
    "telemetryParameters": ["count"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "startProgram",
    "category": "action",
    "labelKey": "startProgram",
    "icon": "rocket_launch",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "whenWaitForExit",
    "outputParameters": [
      { "name": "exitCode", "type": "number" },
      { "name": "processId", "type": "number" },
      { "name": "stdout", "type": "string" },
      { "name": "stderr", "type": "string" },
      { "name": "waited", "type": "boolean" }
    ],
    "telemetryParameters": ["exitCode", "processId"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "powerManagement",
    "category": "action",
    "labelKey": "powerManagement",
    "icon": "power_settings_new",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [],
    "telemetryParameters": ["action"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "waitForCondition",
    "category": "action",
    "labelKey": "waitForCondition",
    "icon": "hourglass_top",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [
      { "name": "attempts", "type": "number" },
      { "name": "elapsedSeconds", "type": "number" },
      { "name": "lastResult", "type": "boolean" }
    ],
    "telemetryParameters": [],
    "prompt": { "included": false, "exclusionReason": "Polling semantics need dedicated examples before LLM generation is reliable." }
  },
  {
    "type": "restApi",
    "category": "action",
    "labelKey": "restApi",
    "icon": "language",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [
      { "name": "statusCode", "type": "number" }
    ],
    "telemetryParameters": ["status", "statusCode", "method", "proxyMode"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "sql",
    "category": "action",
    "labelKey": "sql",
    "icon": "storage",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [
      { "name": "rowCount", "type": "number" },
      { "name": "rowsAffected", "type": "number" }
    ],
    "telemetryParameters": ["rowCount", "rowsAffected", "provider"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "xmlQuery",
    "category": "action",
    "labelKey": "xmlQuery",
    "icon": "code",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "none",
    "outputParameters": [
      { "name": "result", "type": "string" },
      { "name": "count", "type": "number" }
    ],
    "telemetryParameters": ["count"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "jsonQuery",
    "category": "action",
    "labelKey": "jsonQuery",
    "icon": "data_object",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "none",
    "outputParameters": [
      { "name": "result", "type": "string" },
      { "name": "count", "type": "number" }
    ],
    "telemetryParameters": ["count"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "emailNotification",
    "category": "action",
    "labelKey": "emailNotification",
    "icon": "mail",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [],
    "telemetryParameters": [],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "textFileEdit",
    "category": "action",
    "labelKey": "textFileEdit",
    "icon": "edit_note",
    "isRemote": true,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [
      { "name": "operation", "type": "string" },
      { "name": "path", "type": "string" },
      { "name": "linesBefore", "type": "number" },
      { "name": "linesAfter", "type": "number" },
      { "name": "linesChanged", "type": "number" },
      { "name": "encoding", "type": "string" },
      { "name": "lineEnding", "type": "string" },
      { "name": "backupPath", "type": "string" },
      { "name": "dryRun", "type": "boolean" }
    ],
    "telemetryParameters": ["operation", "linesChanged", "dryRun"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "generateText",
    "category": "action",
    "labelKey": "generateText",
    "icon": "casino",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "none",
    "outputParameters": [
      { "name": "text", "type": "string" },
      { "name": "length", "type": "number" }
    ],
    "telemetryParameters": [],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "llmQuery",
    "category": "action",
    "labelKey": "llmQuery",
    "icon": "smart_toy",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "always",
    "outputParameters": [
      { "name": "model", "type": "string" },
      { "name": "promptTokens", "type": "number" },
      { "name": "completionTokens", "type": "number" },
      { "name": "totalTokens", "type": "number" },
      { "name": "finishReason", "type": "string" }
    ],
    "telemetryParameters": ["model"],
    "prompt": { "included": false, "exclusionReason": "LLM-Aufruf-Activity – nicht für Workflow-Auto-Generierung vorgesehen." }
  },
  {
    "type": "log",
    "category": "logic",
    "labelKey": "log",
    "icon": "note_add",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "none",
    "outputParameters": [
      { "name": "level", "type": "string" },
      { "name": "message", "type": "string" }
    ],
    "telemetryParameters": [],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "delay",
    "category": "logic",
    "labelKey": "delay",
    "icon": "schedule",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "none",
    "outputParameters": [],
    "telemetryParameters": [],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "junction",
    "category": "controlFlow",
    "labelKey": "junction",
    "icon": "merge",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "none",
    "outputParameters": [],
    "telemetryParameters": ["mode", "satisfied"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "startWorkflow",
    "category": "controlFlow",
    "labelKey": "startWorkflow",
    "icon": "play_circle",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "whenWaitForCompletion",
    "outputParameters": [
      { "name": "__executionId", "type": "string" },
      { "name": "__status", "type": "string" },
      { "name": "__workflowId", "type": "string" },
      { "name": "__workflowName", "type": "string" }
    ],
    "telemetryParameters": ["waited", "__status"],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "forEach",
    "category": "controlFlow",
    "labelKey": "forEach",
    "icon": "loop",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "none",
    "outputParameters": [
      { "name": "total", "type": "number" },
      { "name": "succeeded", "type": "number" },
      { "name": "failed", "type": "number" },
      { "name": "skipped", "type": "number" },
      { "name": "results", "type": "array" },
      { "name": "firstError", "type": "string" }
    ],
    "telemetryParameters": [],
    "prompt": { "included": false, "exclusionReason": "Loop construct; prompt examples are needed before LLM generation is reliable." }
  },
  {
    "type": "decision",
    "category": "controlFlow",
    "labelKey": "decision",
    "icon": "call_split",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "none",
    "outputParameters": [
      { "name": "case", "type": "string" },
      { "name": "matched", "type": "boolean" },
      { "name": "reason", "type": "string" }
    ],
    "telemetryParameters": ["case", "matched", "reason"],
    "prompt": { "included": false, "exclusionReason": "Edge conditions already cover branch routing in the prompt; decision nodes are withheld for now." }
  },
  {
    "type": "returnData",
    "category": "controlFlow",
    "labelKey": "returnData",
    "icon": "reply",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "none",
    "outputParameters": [],
    "telemetryParameters": [],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "manualTrigger",
    "category": "trigger",
    "labelKey": "manualTrigger",
    "icon": "touch_app",
    "isRemote": false,
    "isExternalTrigger": false,
    "timeout": "none",
    "outputParameters": [],
    "telemetryParameters": [],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "scheduleTrigger",
    "category": "trigger",
    "labelKey": "scheduleTrigger",
    "icon": "schedule",
    "isRemote": false,
    "isExternalTrigger": true,
    "timeout": "none",
    "outputParameters": [
      { "name": "firedAt", "type": "string" },
      { "name": "nextFireAt", "type": "string" }
    ],
    "telemetryParameters": [],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "webhookTrigger",
    "category": "trigger",
    "labelKey": "webhookTrigger",
    "icon": "webhook",
    "isRemote": false,
    "isExternalTrigger": true,
    "timeout": "none",
    "outputParameters": [
      { "name": "webhookBody", "type": "string" },
      { "name": "webhookMethod", "type": "string" },
      { "name": "webhookPath", "type": "string" }
    ],
    "telemetryParameters": [],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "fileWatcherTrigger",
    "category": "trigger",
    "labelKey": "fileWatcherTrigger",
    "icon": "folder_supervised",
    "isRemote": false,
    "isExternalTrigger": true,
    "timeout": "none",
    "outputParameters": [
      { "name": "fileAction", "type": "string" },
      { "name": "filePath", "type": "string" },
      { "name": "fileName", "type": "string" }
    ],
    "telemetryParameters": [],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "databaseTrigger",
    "category": "trigger",
    "labelKey": "databaseTrigger",
    "icon": "database",
    "isRemote": false,
    "isExternalTrigger": true,
    "timeout": "none",
    "outputParameters": [
      { "name": "dbSentinel", "type": "string" },
      { "name": "dbPrevious", "type": "string" }
    ],
    "telemetryParameters": [],
    "prompt": { "included": true, "exclusionReason": null }
  },
  {
    "type": "eventLogTrigger",
    "category": "trigger",
    "labelKey": "eventLogTrigger",
    "icon": "event_note",
    "isRemote": false,
    "isExternalTrigger": true,
    "timeout": "none",
    "outputParameters": [
      { "name": "eventSource", "type": "string" },
      { "name": "eventEntryType", "type": "string" },
      { "name": "eventId", "type": "string" },
      { "name": "eventMessage", "type": "string" },
      { "name": "eventTimeWritten", "type": "string" }
    ],
    "telemetryParameters": [],
    "prompt": { "included": true, "exclusionReason": null }
  }
] as const;

export type ActivityCatalogEntry = typeof ACTIVITY_CATALOG[number];
export type ActivityType = ActivityCatalogEntry["type"];
export type ActivityCategory = ActivityCatalogEntry["category"];
export type ActivityTimeout = ActivityCatalogEntry["timeout"];
export type ActivityOutputParameter = ActivityCatalogEntry["outputParameters"][number];

export const ACTIVITY_TYPES = {
  RUN_SCRIPT: "runScript",
  FILE_OPERATION: "fileOperation",
  FOLDER_OPERATION: "folderOperation",
  FILE_HASH: "fileHash",
  ZIP_OPERATION: "zipOperation",
  SERVICE_MANAGEMENT: "serviceManagement",
  SCHEDULED_TASK: "scheduledTask",
  REGISTRY_OPERATION: "registryOperation",
  WMI_QUERY: "wmiQuery",
  START_PROGRAM: "startProgram",
  POWER_MANAGEMENT: "powerManagement",
  WAIT_FOR_CONDITION: "waitForCondition",
  REST_API: "restApi",
  SQL: "sql",
  XML_QUERY: "xmlQuery",
  JSON_QUERY: "jsonQuery",
  EMAIL_NOTIFICATION: "emailNotification",
  TEXT_FILE_EDIT: "textFileEdit",
  GENERATE_TEXT: "generateText",
  LLM_QUERY: "llmQuery",
  LOG: "log",
  DELAY: "delay",
  JUNCTION: "junction",
  START_WORKFLOW: "startWorkflow",
  FOR_EACH: "forEach",
  DECISION: "decision",
  RETURN_DATA: "returnData",
  MANUAL_TRIGGER: "manualTrigger",
  SCHEDULE_TRIGGER: "scheduleTrigger",
  WEBHOOK_TRIGGER: "webhookTrigger",
  FILE_WATCHER_TRIGGER: "fileWatcherTrigger",
  DATABASE_TRIGGER: "databaseTrigger",
  EVENT_LOG_TRIGGER: "eventLogTrigger"
} as const satisfies Record<string, ActivityType>;

export const ACTIVITY_CATALOG_BY_TYPE = Object.fromEntries(
  ACTIVITY_CATALOG.map((entry) => [entry.type, entry])
) as Record<ActivityType, ActivityCatalogEntry>;

export const TRIGGER_ACTIVITY_TYPES = new Set<string>(
  ACTIVITY_CATALOG.filter((entry) => entry.category === "trigger").map((entry) => entry.type)
);

export const EXTERNAL_TRIGGER_TYPES = ACTIVITY_CATALOG
  .filter((entry) => entry.isExternalTrigger)
  .map((entry) => entry.type) as ReadonlyArray<ActivityType>;

export const REMOTE_ACTIVITY_TYPES = new Set<string>(
  ACTIVITY_CATALOG.filter((entry) => entry.isRemote).map((entry) => entry.type)
);

export const TIMEOUT_ACTIVITY_TYPES = new Set<string>(
  ACTIVITY_CATALOG.filter((entry) => entry.timeout === "always").map((entry) => entry.type)
);

export const CONTROL_FLOW_ACTIVITY_TYPES = new Set<string>(
  ACTIVITY_CATALOG
    .filter((entry) => entry.category === "controlFlow" && entry.type !== "returnData")
    .map((entry) => entry.type)
);

export const ACTIVITY_ICONS = Object.fromEntries(
  ACTIVITY_CATALOG.map((entry) => [entry.type, entry.icon])
) as Record<string, string>;

export const ACTIVITY_LABEL_KEYS = Object.fromEntries(
  ACTIVITY_CATALOG.map((entry) => [entry.type, entry.labelKey])
) as Record<string, string>;

export const STATIC_OUTPUT_PARAMETERS_BY_TYPE = Object.fromEntries(
  ACTIVITY_CATALOG
    .filter((entry) => entry.outputParameters.length > 0)
    .map((entry) => [entry.type, entry.outputParameters])
) as Record<string, ReadonlyArray<{ readonly name: string; readonly type: ActivityOutputParameter["type"] }>>;

export function isActivityType(activityType: string): activityType is ActivityType {
  return activityType in ACTIVITY_CATALOG_BY_TYPE;
}
