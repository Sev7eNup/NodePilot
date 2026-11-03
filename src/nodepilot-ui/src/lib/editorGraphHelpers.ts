import type { Edge } from '@xyflow/react';
import { rewriteEdgeReferences, rewriteNodeReferences } from './workflowReferences';

/**
 * Pure helpers for the workflow-editor page, kept out of `WorkflowEditorPage.tsx` so the page
 * stays focused on JSX and state wiring. None of these read or write component state; every
 * input is passed in explicitly.
 */

/** BFS the outgoing edges from <c>startId</c> and return every reachable downstream node id. */
export function getDownstreamNodeIds(startId: string, edges: Edge[]): Set<string> {
  const result = new Set<string>();
  const queue = [startId];
  while (queue.length > 0) {
    const cur = queue.shift()!;
    for (const e of edges) {
      if (e.source === cur && !result.has(e.target)) {
        result.add(e.target);
        queue.push(e.target);
      }
    }
  }
  return result;
}

/**
 * Marks the elements of <c>next</c> as selected exactly when the element with the same id was
 * selected in <c>prev</c>. Used when the server graph is re-adopted for the open workflow, so the
 * user's selection (and the properties panel behind it) survives. A <c>selected</c> flag stored in
 * the saved JSON is overwritten.
 */
export function carrySelection<T extends { id: string; selected?: boolean }>(
  next: T[],
  prev: ReadonlyArray<{ id: string; selected?: boolean }>,
): T[] {
  const selectedIds = new Set(prev.filter((el) => el.selected).map((el) => el.id));
  return next.map((el) => ({ ...el, selected: selectedIds.has(el.id) }));
}

/**
 * Rename references in configuration, decision expressions and dynamic node bindings.
 */
export function renameVariableInNodeData(
  data: Record<string, unknown>,
  oldAlias: string,
  newAlias: string,
): Record<string, unknown> {
  return rewriteNodeReferences(data, new Map([[oldAlias, newAlias]]));
}

/** Rewrite legacy condition shortcuts and conditionExpression variable refs on an edge. */
export function renameVariableInEdge(edge: Edge, oldAlias: string, newAlias: string): Edge {
  if (!edge.data) return edge;
  const data = rewriteEdgeReferences(edge.data, new Map([[oldAlias, newAlias]]));
  return data === edge.data ? edge : { ...edge, data };
}
