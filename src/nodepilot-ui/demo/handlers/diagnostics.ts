/**
 * Support log and support events, derived from the execution history.
 *
 * Nothing is authored here. Every row restates a run or a step that already exists in the
 * world, so the log cannot claim something the executions list contradicts. The file tail is
 * rendered from the same rows, which is also how the product projects them.
 *
 * The two download endpoints stay refused: a file is a claim about a real installation, and
 * handing over a plausible-looking one would be the demo lying in a format people keep.
 */
import { route, type Route } from '../net/router';
import { json } from '../net/respond';
import { getWorld } from '../state/world';
import { demoId } from '../state/ids';
import { DEMO_HOST, DEMO_USER } from '../seed/entities';
import type { SupportEventResponse } from '../../src/api/diagnostics';

/** Serilog levels, as `LEVEL_LABELS` in SupportEventsTable maps them. */
const INFO = 2;
const WARN = 3;
const ERROR = 4;

function shortId(id: string): string {
  return id.slice(0, 8);
}

/** Every event the world implies, newest first. */
function buildEvents(): SupportEventResponse[] {
  const world = getWorld();
  const nameById = new Map(world.workflows.map((w) => [w.id, w.name]));
  const rows: SupportEventResponse[] = [];

  const base = (execution: { id: string; workflowId: string; startedAt: string }) => ({
    workflowId: execution.workflowId,
    workflowName: nameById.get(execution.workflowId) ?? null,
    executionId: execution.id,
    executionShort: shortId(execution.id),
    stepId: null,
    stepLabel: null,
    activityType: null,
    userName: null,
    userId: null,
    traceId: shortId(execution.id),
    spanId: null,
    propertiesJson: null,
  });

  for (const execution of world.executions) {
    rows.push({
      ...base(execution),
      id: demoId(`support:${execution.id}:started`),
      timestamp: execution.startedAt,
      level: INFO,
      eventType: 'EXECUTION_STARTED',
      message: `Execution started (${execution.triggeredBy ?? 'manual'}).`,
      userName: execution.startedByUsername ?? null,
    });

    // The step that ended the run carries the reason, so the failure reads like a log and not
    // like a status word.
    const steps = world.steps.get(execution.id) ?? [];
    const broken = steps.find((s) => s.status === 'Failed');
    if (broken) {
      rows.push({
        ...base(execution),
        id: demoId(`support:${execution.id}:step-failed`),
        timestamp: broken.completedAt ?? broken.startedAt ?? execution.startedAt,
        level: ERROR,
        eventType: 'STEP_FAILED',
        message: broken.errorOutput ?? 'The step failed.',
        stepId: broken.stepId,
        stepLabel: broken.stepName,
        activityType: broken.stepType,
      });
    }

    if (execution.completedAt) {
      const failed = execution.status === 'Failed';
      const cancelled = execution.status === 'Cancelled';
      rows.push({
        ...base(execution),
        id: demoId(`support:${execution.id}:finished`),
        timestamp: execution.completedAt,
        level: failed ? ERROR : cancelled ? WARN : INFO,
        eventType: failed ? 'EXECUTION_FAILED' : cancelled ? 'EXECUTION_CANCELLED' : 'EXECUTION_SUCCEEDED',
        message: failed
          ? (execution.errorMessage ?? 'The execution failed.')
          : cancelled ? 'The execution was cancelled by an operator.'
          : `Execution completed with ${steps.length} step(s).`,
      });
    }
  }

  // One boot line, so the type filter has a non-execution row and the tail starts somewhere.
  const oldest = world.executions.at(-1)?.startedAt;
  if (oldest) {
    rows.push({
      id: demoId('support:boot'),
      timestamp: oldest,
      level: INFO,
      eventType: 'SYSTEM_BOOT',
      message: `NodePilot ${DEMO_HOST.appVersion} started on ${DEMO_HOST.machineName}.`,
      workflowId: null, workflowName: null, executionId: null, executionShort: null,
      stepId: null, stepLabel: null, activityType: null,
      userName: DEMO_USER.username, userId: DEMO_USER.id,
      traceId: null, spanId: null, propertiesJson: null,
    });
  }

  return rows.sort((a, b) => Date.parse(b.timestamp) - Date.parse(a.timestamp));
}

/** Applies the filters the events table sends. Unknown keys are ignored, as the product does. */
function filterEvents(rows: SupportEventResponse[], query: URLSearchParams): SupportEventResponse[] {
  const level = query.get('level');
  const eventType = query.get('eventType');
  const workflowId = query.get('workflowId');
  const executionId = query.get('executionId');
  const text = query.get('q')?.toLowerCase();

  let result = rows;
  if (level) result = result.filter((r) => r.level >= Number(level));
  if (eventType) result = result.filter((r) => r.eventType === eventType);
  if (workflowId) result = result.filter((r) => r.workflowId === workflowId);
  if (executionId) result = result.filter((r) => r.executionId === executionId);
  if (text) {
    result = result.filter((r) =>
      r.message.toLowerCase().includes(text)
      || (r.workflowName ?? '').toLowerCase().includes(text)
      || (r.stepLabel ?? '').toLowerCase().includes(text));
  }
  if (query.get('sortDir') === 'asc') result = [...result].reverse();
  return result;
}

/** cmtrace-ish single line per event, the shape the plain-text viewer renders. */
function logLine(row: SupportEventResponse): string {
  const level = row.level >= 4 ? 'ERR' : row.level === 3 ? 'WRN' : 'INF';
  const scope = row.workflowName ? ` [${row.workflowName}]` : '';
  return `${row.timestamp} [${level}]${scope} ${row.eventType}: ${row.message}`;
}

export const diagnosticsRoutes: Route[] = [
  route('GET', '/diagnostics/support-log', () => {
    const lines = buildEvents().slice(0, 500).map(logLine);
    return json({
      file: `C:\\ProgramData\\NodePilot\\logs\\nodepilot-${new Date().toISOString().slice(0, 10)}.log`,
      lineCount: lines.length,
      lines,
    });
  }),

  route('GET', '/diagnostics/support-events', (ctx) => {
    const take = Math.min(Number(ctx.query.get('take') ?? 200) || 200, 500);
    const rows = filterEvents(buildEvents(), ctx.query);
    const page = rows.slice(0, take);
    return json({
      items: page,
      // A single page is enough for the demo; the table stops asking when hasMore is false.
      nextCursor: null,
      hasMore: false,
    });
  }),
];

/** Row count the tests assert against, so neither side states a total of its own. */
export function demoSupportEventCount(): number {
  return buildEvents().length;
}
