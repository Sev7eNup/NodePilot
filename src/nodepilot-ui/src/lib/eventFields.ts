/**
 * Catalog of event fields that an alerting rule filter can reference as `source: 'event'`
 * operands in the ConditionBuilder. The `name` values MUST stay in sync with the keys produced
 * by the backend NotificationContext.ToFieldMap() (src/NodePilot.Core/Models/NotificationContext.cs)
 * — that map is what the engine's ConditionEvaluator matches against at dispatch time.
 *
 * `labelKey` resolves through the `alerts` i18n namespace (alerts:eventFields.<name>).
 * `applies` is informational only: it records which context populates the field (execution vs.
 * signal/gauge). Custom rules now filter over the full field set regardless of `applies`.
 */
export interface EventFieldCatalogEntry {
  name: string;
  labelKey: string;
  applies: 'execution' | 'gauge' | 'both';
}

export const EVENT_FIELD_CATALOG: readonly EventFieldCatalogEntry[] = [
  { name: 'eventType', labelKey: 'eventFields.eventType', applies: 'both' },
  { name: 'severity', labelKey: 'eventFields.severity', applies: 'both' },
  { name: 'workflowName', labelKey: 'eventFields.workflowName', applies: 'execution' },
  { name: 'folderPath', labelKey: 'eventFields.folderPath', applies: 'execution' },
  { name: 'status', labelKey: 'eventFields.status', applies: 'execution' },
  { name: 'errorMessage', labelKey: 'eventFields.errorMessage', applies: 'execution' },
  { name: 'durationMs', labelKey: 'eventFields.durationMs', applies: 'execution' },
  { name: 'triggeredBy', labelKey: 'eventFields.triggeredBy', applies: 'execution' },
  { name: 'callDepth', labelKey: 'eventFields.callDepth', applies: 'execution' },
  { name: 'isSubWorkflow', labelKey: 'eventFields.isSubWorkflow', applies: 'execution' },
  { name: 'cancelledBy', labelKey: 'eventFields.cancelledBy', applies: 'execution' },
  { name: 'sourceKey', labelKey: 'eventFields.sourceKey', applies: 'gauge' },
  // Populated for gauge signals (machine name of the signal source) AND for terminal
  // execution events (resolved machine name of the last-failing step) since the
  // dispatcher joins StepExecution.TargetMachine into the execution context.
  { name: 'targetMachine', labelKey: 'eventFields.targetMachine', applies: 'both' },
  { name: 'signalValue', labelKey: 'eventFields.signalValue', applies: 'gauge' },
];

/** Notification event types the rule's coarse pre-filter can react to (mirror of NotificationEventType). */
export const NOTIFICATION_EVENT_TYPES = [
  'ExecutionFailed',
  'ExecutionSucceeded',
  'ExecutionCancelled',
  'ServiceStale',
  'MachineUnreachable',
  'BacklogHigh',
  'PendingHigh',
  'CancelRateHigh',
  'ExecutionRunningLong',
  'ExecutionQueuedLong',
  'ScheduleMissed',
  'WorkflowNoRecentSuccess',
  'CredentialFailure',
  'CredentialExpiring',
  // Built-in infra/signal alerts (e.g. machine unreachable, backlog high) now live in their
  // own "system alert policy" system rather than as custom rules (see design doc ADR 0008).
  // This entry mirrors the backend enum for completeness; the custom-rule editor actually
  // sources its selectable types from the server catalog, which excludes SystemAlert.
  'SystemAlert',
] as const;
export type NotificationEventType = (typeof NOTIFICATION_EVENT_TYPES)[number];

/**
 * Filter fields offered to a custom alerting rule. Custom rules can only match execution-scoped
 * event types (the infra/signal gauge types were moved into the separate system-alert-policy
 * feature, design doc ADR 0008), so the editor exposes the entire execution+shared field catalog.
 */
export function customEventFields(): readonly EventFieldCatalogEntry[] {
  return EVENT_FIELD_CATALOG;
}

/** Delivery channels available in v1a (mirror of NotificationChannel; Teams/Slack/PagerDuty/Opsgenie land in v1b). */
export const NOTIFICATION_CHANNELS = ['Email', 'GenericWebhook'] as const;
export type NotificationChannel = (typeof NOTIFICATION_CHANNELS)[number];
