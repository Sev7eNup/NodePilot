/**
 * Alerting: notification rules and the system-alert policy catalog.
 *
 * The two catalogs are served in full rather than as empty stubs. `SystemAlertsSection` reads
 * `catalog.sources.length` with no guard, so an array where an object belongs throws and takes
 * the whole page down — an empty page and a crashed page look nothing alike to a visitor.
 *
 * Rules and policies start empty, which is the product's own idle state: alerting is opt-in by
 * data and does nothing until someone creates a rule.
 */
import { route, type Route } from '../net/router';
import { json, notInDemo } from '../net/respond';

/** Field shorthand, so the source list below stays readable. */
function numberField(name: string, unit: string | null) {
  return { name, type: 'Number', operators: ['>', '>=', '<', '<=', '==', '!='], unit, enumValues: null };
}

function enumField(name: string, values: string[]) {
  return { name, type: 'Enum', operators: ['==', '!='], unit: null, enumValues: values };
}

/**
 * A representative slice of the catalogued sources (ADR 0008 lists fourteen). Enough for the
 * page to render its categories, fields and presets; the demo cannot evaluate any of them.
 */
const SYSTEM_ALERT_SOURCES = [
  {
    sourceId: 'execution-failed',
    category: 'Execution',
    scopeCapability: 'WorkflowScoped',
    defaultSeverity: 'Error',
    fields: [
      enumField('status', ['Succeeded', 'Failed', 'Cancelled']),
      numberField('durationSeconds', 's'),
      { name: 'workflowName', type: 'String', operators: ['==', 'contains', 'startsWith'], unit: null, enumValues: null },
    ],
    parameters: [
      { name: 'consecutiveFailures', type: 'Number', default: 1, required: false, unit: null, min: 1, max: 100 },
    ],
    presets: [
      { presetId: 'any-failure', severity: 'Error', sustainForSeconds: 0, conditionJson: null, parameters: null },
    ],
    available: true,
  },
  {
    sourceId: 'long-running-execution',
    category: 'Execution',
    scopeCapability: 'WorkflowScoped',
    defaultSeverity: 'Warning',
    fields: [numberField('runningSeconds', 's')],
    parameters: [
      { name: 'thresholdSeconds', type: 'Number', default: 900, required: true, unit: 's', min: 60, max: 86_400 },
    ],
    presets: [
      { presetId: 'over-15-min', severity: 'Warning', sustainForSeconds: 60, conditionJson: null, parameters: { thresholdSeconds: 900 } },
    ],
    available: true,
  },
  {
    sourceId: 'queue-backlog',
    category: 'Queue',
    scopeCapability: 'GlobalOnly',
    defaultSeverity: 'Warning',
    fields: [numberField('pendingCount', null), numberField('oldestPendingSeconds', 's')],
    parameters: [{ name: 'threshold', type: 'Number', default: 50, required: true, unit: null, min: 1, max: 10_000 }],
    presets: [],
    available: true,
  },
  {
    sourceId: 'trigger-unhealthy',
    category: 'Health',
    scopeCapability: 'WorkflowScoped',
    defaultSeverity: 'Error',
    fields: [
      enumField('triggerType', ['fileWatcherTrigger', 'databaseTrigger', 'eventLogTrigger', 'scheduleTrigger']),
      numberField('unhealthySeconds', 's'),
    ],
    parameters: [],
    presets: [],
    available: true,
  },
  {
    sourceId: 'credential-expiring',
    category: 'Credential',
    scopeCapability: 'GlobalOnly',
    defaultSeverity: 'Warning',
    fields: [numberField('daysRemaining', 'd')],
    parameters: [{ name: 'warnWithinDays', type: 'Number', default: 30, required: true, unit: 'd', min: 1, max: 365 }],
    presets: [
      { presetId: 'expiring-30d', severity: 'Warning', sustainForSeconds: 0, conditionJson: null, parameters: { warnWithinDays: 30 } },
    ],
    available: true,
  },
  {
    sourceId: 'audit-event',
    category: 'Security',
    scopeCapability: 'GlobalOnly',
    defaultSeverity: 'Info',
    fields: [
      { name: 'action', type: 'String', operators: ['==', 'startsWith', 'contains'], unit: null, enumValues: null },
      { name: 'actorUserName', type: 'String', operators: ['==', 'contains'], unit: null, enumValues: null },
    ],
    parameters: [],
    presets: [],
    available: true,
  },
];

const ALERTING_CATALOG = {
  eventTypes: [
    { name: 'ExecutionCompleted', category: 'execution', scopeable: true },
    { name: 'ExecutionFailed', category: 'execution', scopeable: true },
    { name: 'ExecutionCancelled', category: 'execution', scopeable: true },
    { name: 'StepFailed', category: 'execution', scopeable: true },
  ],
  eventFields: [
    { name: 'workflowName', applies: 'execution', type: 'string', values: null },
    { name: 'status', applies: 'execution', type: 'enum', values: ['Succeeded', 'Failed', 'Cancelled'] },
    { name: 'durationSeconds', applies: 'execution', type: 'number', values: null },
    { name: 'triggeredBy', applies: 'execution', type: 'string', values: null },
    { name: 'errorMessage', applies: 'execution', type: 'string', values: null },
  ],
  channels: ['Smtp', 'Webhook'],
  dedupTemplateFields: ['workflowId', 'workflowName', 'status'],
};

export const alertingRoutes: Route[] = [
  route('GET', '/alerting/catalog', () => json(ALERTING_CATALOG)),
  route('GET', '/alerting/rules', () => json([])),
  route('GET', '/alerting/deliveries', () => json([])),

  route('GET', '/alerting/system/catalog', () => json({ sources: SYSTEM_ALERT_SOURCES })),
  route('GET', '/alerting/system/policies', () => json([])),

  // Everything that would actually notify someone needs a server.
  route('POST', '/alerting/rules', () => notInDemo('Creating alert rules')),
  route('POST', '/alerting/preview-filter', () => notInDemo('Previewing an alert filter')),
  route('POST', '/alerting/preview-rule', () => notInDemo('Previewing an alert rule')),
  route('POST', '/alerting/system/policies', () => notInDemo('Creating system alert policies')),
  route('POST', '/alerting/system/preview', () => notInDemo('Previewing a system alert')),
];

/** Source ids the demo serves, for tests. */
export const demoSystemAlertSourceIds = SYSTEM_ALERT_SOURCES.map((s) => s.sourceId);
