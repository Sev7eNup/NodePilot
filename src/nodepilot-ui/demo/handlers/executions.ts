/**
 * Execution endpoints. The list is also the hydration path the live view falls back on, so
 * it has to agree with whatever the hub has already emitted — the run player writes into the
 * world before delivering an event for exactly that reason.
 */
import { route, type Route } from '../net/router';
import { json, notFound, notInDemo } from '../net/respond';
import { findExecution, getWorld, stepsOf } from '../state/world';
import { cancelRun, startRun } from '../run/player';

const TERMINAL = new Set(['Succeeded', 'Failed', 'Cancelled', 'Skipped']);

export const executionRoutes: Route[] = [
  route('GET', '/executions', (ctx) => {
    const workflowId = ctx.query.get('workflowId');
    const status = ctx.query.get('status');
    const activeOnly = ctx.query.get('activeOnly') === 'true';
    const terminalOnly = ctx.query.get('terminalOnly') === 'true';
    const page = Number(ctx.query.get('page') ?? 1);
    const pageSize = Number(ctx.query.get('pageSize') ?? 50);

    let rows = getWorld().executions;
    if (workflowId) rows = rows.filter((e) => e.workflowId === workflowId);
    if (status) rows = rows.filter((e) => e.status === status);
    if (activeOnly) rows = rows.filter((e) => !TERMINAL.has(e.status));
    if (terminalOnly) rows = rows.filter((e) => TERMINAL.has(e.status));

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
