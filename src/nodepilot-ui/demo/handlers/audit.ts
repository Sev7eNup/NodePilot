/**
 * Audit log.
 *
 * Entries are derived from the world — a workflow's creation and publication, and the start of
 * every run — rather than authored. An empty audit log next to forty-odd executions would be a
 * visible contradiction, and the derivation rule keeps the two from drifting apart.
 *
 * The page reads a cursor-paged envelope (`{ items, nextCursor }`), not a bare array, and the
 * export endpoint answers with a real file so the download affordance is not a dead end.
 */
import { route, type Route } from '../net/router';
import { download, json } from '../net/respond';
import { getWorld } from '../state/world';
import { demoId } from '../state/ids';
import { DEMO_USER } from '../seed/entities';

interface DemoAuditEntry {
  id: string;
  timestamp: string;
  userId: string | null;
  username: string | null;
  action: string;
  resourceType: string | null;
  resourceId: string | null;
  details: string | null;
  ipAddress: string | null;
}

const DEMO_IP = '203.0.113.24';

/** Every audit row the demo world implies, newest first. */
function auditEntries(): DemoAuditEntry[] {
  const world = getWorld();
  const entries: DemoAuditEntry[] = [];

  const push = (seed: string, timestamp: string, action: string, resourceType: string | null, resourceId: string | null, details: string | null) => {
    entries.push({
      id: demoId(`audit:${seed}`),
      timestamp,
      userId: DEMO_USER.id,
      username: DEMO_USER.username,
      action,
      resourceType,
      resourceId,
      details,
      ipAddress: DEMO_IP,
    });
  };

  for (const workflow of world.workflows) {
    push(`created:${workflow.id}`, workflow.createdAt, 'WORKFLOW_CREATED', 'Workflow', workflow.id,
      JSON.stringify({ name: workflow.name }));
    push(`published:${workflow.id}`, workflow.updatedAt, 'WORKFLOW_PUBLISHED', 'Workflow', workflow.id,
      JSON.stringify({ name: workflow.name, version: workflow.version }));
  }

  for (const execution of world.executions) {
    const workflow = world.workflows.find((w) => w.id === execution.workflowId);
    push(`execution:${execution.id}`, execution.startedAt, 'EXECUTION_STARTED', 'Execution', execution.id,
      JSON.stringify({ workflowName: workflow?.name ?? null, triggeredBy: execution.triggeredBy }));
  }

  return entries.sort((a, b) => {
    const delta = Date.parse(b.timestamp) - Date.parse(a.timestamp);
    return delta !== 0 ? delta : b.id.localeCompare(a.id);
  });
}

/** Applies the filter parameters the page sends. */
function applyFilters(entries: DemoAuditEntry[], query: URLSearchParams): DemoAuditEntry[] {
  const match = (value: string | null, filter: string | null) =>
    !filter || (value ?? '').toLowerCase().includes(filter.toLowerCase());

  return entries.filter((entry) =>
    match(entry.action, query.get('action'))
    && match(entry.resourceType, query.get('resourceType'))
    && match(entry.resourceId, query.get('resourceId'))
    && match(entry.userId, query.get('userId'))
    && match(entry.ipAddress, query.get('ipAddress')));
}

function csvCell(value: string | null): string {
  const text = value ?? '';
  return /[",\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

export const auditRoutes: Route[] = [
  // Registered before `/audit` so the more specific path wins.
  route('GET', '/audit/export', (ctx) => {
    const rows = applyFilters(auditEntries(), ctx.query);
    const format = ctx.query.get('format') === 'ndjson' ? 'ndjson' : 'csv';

    if (format === 'ndjson') {
      return download(
        rows.map((row) => JSON.stringify(row)).join('\n'),
        'nodepilot-audit.ndjson',
        'application/x-ndjson',
      );
    }

    const header = ['timestamp', 'username', 'action', 'resourceType', 'resourceId', 'ipAddress', 'details'];
    const body = rows.map((row) => [
      row.timestamp, row.username, row.action, row.resourceType, row.resourceId, row.ipAddress, row.details,
    ].map(csvCell).join(','));
    return download([header.join(','), ...body].join('\n'), 'nodepilot-audit.csv', 'text/csv');
  }),

  route('GET', '/audit', (ctx) => {
    const rows = applyFilters(auditEntries(), ctx.query);
    const take = Math.max(1, Math.min(500, Number(ctx.query.get('take') ?? 50)));

    // Cursor paging, keyed the way the page reads it back: everything strictly older than the
    // cursor, ties broken by id.
    const cursorTimestamp = ctx.query.get('cursorTimestamp');
    const cursorId = ctx.query.get('cursorId');
    const start = cursorTimestamp
      ? rows.findIndex((r) => r.timestamp === cursorTimestamp && r.id === cursorId) + 1
      : 0;

    const items = rows.slice(start, start + take);
    const last = items.at(-1);
    const hasMore = start + take < rows.length;
    return json({
      items,
      nextCursor: hasMore && last ? { timestamp: last.timestamp, id: last.id } : null,
    });
  }),
];

/** Row count the demo world implies, for tests. */
export function demoAuditEntryCount(): number {
  return auditEntries().length;
}
