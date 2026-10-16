/**
 * Execution endpoints. The list is also the hydration path the live view falls back on, so
 * it has to agree with whatever the hub has already emitted — the run player writes into the
 * world before delivering an event for exactly that reason.
 */
import { route, type Route } from '../net/router';
import { badRequest, json, notFound, notInDemo } from '../net/respond';
import { findExecution, getWorld, stepsOf } from '../state/world';
import { cancelRun, startRun } from '../run/player';
import type { WorkflowExecution } from '../../src/types/api';

const TERMINAL = new Set(['Succeeded', 'Failed', 'Cancelled', 'Skipped']);

/** The list's sort keys, as GET /api/executions accepts them. */
const SORT_KEYS = ['started', 'status', 'workflow', 'trigger', 'startedBy', 'steps', 'duration'] as const;
type SortKey = typeof SORT_KEYS[number];

function isSortKey(value: string): value is SortKey {
  return (SORT_KEYS as readonly string[]).includes(value);
}

function durationMs(e: WorkflowExecution): number {
  return e.completedAt ? Date.parse(e.completedAt) - Date.parse(e.startedAt) : -1;
}

function sortValue(e: WorkflowExecution, key: SortKey, workflowNames: Map<string, string>): string | number {
  switch (key) {
    case 'started': return Date.parse(e.startedAt);
    case 'status': return e.status;
    case 'workflow': return workflowNames.get(e.workflowId) ?? '';
    case 'trigger': return e.triggeredBy ?? '';
    case 'startedBy': return e.startedByUsername ?? '';
    case 'steps': return e.stepsCompleted ?? 0;
    case 'duration': return durationMs(e);
  }
}

export const executionRoutes: Route[] = [
  route('GET', '/executions', (ctx) => {
    const workflowId = ctx.query.get('workflowId');
    const status = ctx.query.get('status');
    const activeOnly = ctx.query.get('activeOnly') === 'true';
    const terminalOnly = ctx.query.get('terminalOnly') === 'true';
    const page = Number(ctx.query.get('page') ?? 1);
    const pageSize = Number(ctx.query.get('pageSize') ?? 50);
    const sortBy = ctx.query.get('sortBy') ?? 'started';
    const sortDir = ctx.query.get('sortDir') ?? 'desc';
    if (!isSortKey(sortBy) || (sortDir !== 'asc' && sortDir !== 'desc'))
      return badRequest('INVALID_SORT', 'Unknown sortBy or sortDir.');

    let rows = getWorld().executions;
    if (workflowId) rows = rows.filter((e) => e.workflowId === workflowId);
    if (status) rows = rows.filter((e) => e.status === status);
    if (activeOnly) rows = rows.filter((e) => !TERMINAL.has(e.status));
    if (terminalOnly) rows = rows.filter((e) => TERMINAL.has(e.status));

    // The whole result is ordered before paging, newest first on ties, like the server.
    const workflowNames = new Map(getWorld().workflows.map((w) => [w.id, w.name]));
    const direction = sortDir === 'asc' ? 1 : -1;
    rows = [...rows].sort((a, b) => {
      const left = sortValue(a, sortBy, workflowNames);
      const right = sortValue(b, sortBy, workflowNames);
      const comparison = typeof left === 'number' && typeof right === 'number'
        ? left - right : String(left).localeCompare(String(right));
      return comparison * direction
        || Date.parse(b.startedAt) - Date.parse(a.startedAt)
        || b.id.localeCompare(a.id);
    });

    // A paged envelope, not a bare array. `getPage` treats an array as "one complete page",
    // so slicing without a total would make everything past the first page unreachable.
    const start = Math.max(0, (page - 1) * pageSize);
    const items = rows.slice(start, start + pageSize);
    return json({
      items,
      page,
      pageSize,
      total: rows.length,
      totalPages: pageSize > 0 ? Math.ceil(rows.length / pageSize) : 0,
    });
  }),

  route('GET', '/executions/:id', (ctx) => {
    const execution = findExecution(ctx.params.id);
    return execution ? json(execution) : notFound('Execution');
  }),

  route('GET', '/executions/:id/steps', (ctx) => {
    const execution = findExecution(ctx.params.id);
    return execution ? json(stepsOf(execution.id)) : notFound('Execution');
  }),

  route('POST', '/executions/:id/cancel', (ctx) => {
    const execution = findExecution(ctx.params.id);
    if (!execution) return notFound('Execution');
    if (!cancelRun(execution.id)) {
      // Already terminal: mirror the product, which accepts the call and changes nothing.
      return json(execution);
    }
    return json(findExecution(ctx.params.id) ?? execution);
  }),

  route('POST', '/executions/:id/retry', (ctx) => {
    const execution = findExecution(ctx.params.id);
    if (!execution) return notFound('Execution');
    const replay = startRun(execution.workflowId, 'retry', JSON.parse(execution.inputParametersJson ?? '{}') as Record<string, string>);
    return replay ? json(replay, 202) : notFound('Workflow');
  }),

  // Resuming a paused run needs a debugger attached to a real engine.
  route('POST', '/executions/:id/resume', () => notInDemo('Resuming a paused run')),
];
