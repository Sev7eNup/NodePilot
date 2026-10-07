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
import { DEMO_HOST } from '../seed/entities';
import type { SupportEventResponse } from '../../src/api/diagnostics';

/** Serilog levels, as `LEVEL_LABELS` in SupportEventsTable maps them. */
const INFO = 2;
const WARN = 3;

/** Support-log file tokens from SupportLogFormatter (`[Level:u4]`, "ERR " padded). */
const LEVEL_TOKENS: Record<number, string> = { 0: 'TRCE', 1: 'DBUG', 2: 'INFO', 3: 'WARN', 4: 'ERR ', 5: 'FATL' };

/** Matches `Guid.ToString("N")[..8]` for the demo's ids. */
function shortId(id: string): string {
  return id.replaceAll('-', '').slice(0, 8);
}

/** An event row plus the rendered message the file sink writes for it. */
interface DemoSupportEvent {
  row: SupportEventResponse;
  rendered: string;
}

/**
 * Every event the world implies, oldest first. Messages and levels follow the engine:
 * `row.message` is the `support.message` projection, `rendered` the log template.
 */
function buildEvents(): DemoSupportEvent[] {
  const world = getWorld();
  const nameById = new Map(world.workflows.map((w) => [w.id, w.name]));
  const userIdByName = new Map(world.users.map((u) => [u.username, u.id]));
  const events: DemoSupportEvent[] = [];

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
    const workflowName = nameById.get(execution.workflowId) ?? '';
    const exec = shortId(execution.id);
    const trigger = execution.triggeredBy ?? 'manual';
    const userId = (execution.startedByUsername && userIdByName.get(execution.startedByUsername)) || null;
    events.push({
      row: {
        ...base(execution),
        id: demoId(`support:${execution.id}:started`),
        timestamp: execution.startedAt,
        level: INFO,
        eventType: 'EXECUTION_STARTED',
        message: `trigger=${trigger} user=${userId ?? '-'}`,
        userName: execution.startedByUsername ?? null,
        userId,
      },
      rendered: `EXECUTION_STARTED workflow=${workflowName} exec=${exec} trigger=${trigger} user=${userId ?? '-'}`,
    });

    // StepRunner logs a genuine step failure as a Warning, not an Error.
    const steps = world.steps.get(execution.id) ?? [];
    const broken = steps.find((s) => s.status === 'Failed');
    if (broken) {
      const reason = broken.errorOutput || '(no error message)';
      const label = broken.stepName ?? broken.stepId;
      events.push({
        row: {
          ...base(execution),
          id: demoId(`support:${execution.id}:step-failed`),
          timestamp: broken.completedAt ?? broken.startedAt ?? execution.startedAt,
          level: WARN,
          eventType: 'STEP_FAILED',
          message: reason,
          stepId: broken.stepId,
          stepLabel: label,
          activityType: broken.stepType,
        },
        rendered: `STEP_FAILED exec=${exec} step=${label} activity=${broken.stepType} reason=${reason}`,
      });
    }

    if (execution.completedAt) {
      // WorkflowEngine: Succeeded is Information, every other terminal state a Warning.
      const eventType = execution.status === 'Succeeded' ? 'EXECUTION_SUCCEEDED'
        : execution.status === 'Failed' ? 'EXECUTION_FAILED'
        : execution.status === 'Cancelled' ? 'EXECUTION_CANCELLED'
        : 'EXECUTION_COMPLETED';
      const seconds = Math.max(0, Date.parse(execution.completedAt) - Date.parse(execution.startedAt)) / 1000;
      const count = (status: string) => steps.filter((s) => s.status === status).length;
      const stats = `duration=${seconds.toFixed(1)}s steps=ok:${count('Succeeded')}/fail:${count('Failed')}/skip:${count('Skipped')}`;
      events.push({
        row: {
          ...base(execution),
          id: demoId(`support:${execution.id}:finished`),
          timestamp: execution.completedAt,
          level: eventType === 'EXECUTION_SUCCEEDED' ? INFO : WARN,
          eventType,
          message: stats,
        },
        rendered: `${eventType} workflow=${workflowName} exec=${exec} ${stats}`,
      });
    }
  }

  // One boot line, so the type filter has a non-execution row and the tail starts somewhere.
  const oldest = world.executions.at(-1)?.startedAt;
  if (oldest) {
    const version = DEMO_HOST.appVersion;
    events.push({
      row: {
        id: demoId('support:boot'),
        timestamp: oldest,
        level: INFO,
        eventType: 'SYSTEM_BOOT',
        message: `started version=${version} env=Production db=postgres`,
        workflowId: null, workflowName: null, executionId: null, executionShort: null,
        stepId: null, stepLabel: null, activityType: null,
        userName: null, userId: null,
        traceId: null, spanId: null, propertiesJson: null,
      },
      rendered: `NodePilot.Api started — version=${version} env=Production db=postgres`,
    });
  }

  return events.sort((a, b) => Date.parse(a.row.timestamp) - Date.parse(b.row.timestamp));
}

/** Newest first, the table's default order. */
function buildRows(): SupportEventResponse[] {
  return buildEvents().map((e) => e.row).reverse();
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

function pad(value: number, width = 2): string {
  return String(value).padStart(width, '0');
}

/** `yyyy-MM-dd HH:mm:ss.fff` in local time, as Serilog renders a DateTimeOffset. */
function formatTimestamp(iso: string): string {
  const d = new Date(iso);
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} `
    + `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

/** One line per event, in SupportLogFormatter's layout. */
function logLine(event: DemoSupportEvent): string {
  return `${formatTimestamp(event.row.timestamp)} [${LEVEL_TOKENS[event.row.level] ?? 'INFO'}] ${event.rendered}`;
}

/** Today's daily file name (UTC date), as SupportLogFileResolver builds it. */
function supportLogFileName(): string {
  const now = new Date();
  return `nodepilot-support-${now.getUTCFullYear()}${pad(now.getUTCMonth() + 1)}${pad(now.getUTCDate())}.log`;
}

/** Tail cap, as DiagnosticsController applies it. */
const MAX_TAIL_LINES = 1000;

export const diagnosticsRoutes: Route[] = [
  route('GET', '/diagnostics/support-log', (ctx) => {
    const requested = Number(ctx.query.get('lines') ?? 200) || 200;
    const count = Math.min(Math.max(requested, 1), MAX_TAIL_LINES);
    // The file is append-only, so the tail is the newest lines in file order (oldest first).
    const lines = buildEvents().map(logLine).slice(-count);
    return json({ file: supportLogFileName(), lineCount: lines.length, lines });
  }),

  route('GET', '/diagnostics/support-events', (ctx) => {
    const take = Math.min(Number(ctx.query.get('take') ?? 200) || 200, 500);
    const rows = filterEvents(buildRows(), ctx.query);
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
