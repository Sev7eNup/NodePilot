import type { Edge } from '@xyflow/react';

/**
 * Pure helpers for the workflow-editor page. Lifted out of `WorkflowEditorPage.tsx`
 * so the page itself stays focused on JSX + state wiring rather than carrying its own
 * graph-walking utilities. None of these read/write component state — every input is
 * passed in explicitly.
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
 * Rewrite every <c>{{oldAlias.…}}</c> token in the JSON-serialised node config to use
 * the new alias. Used when an outputVariable rename needs to propagate to downstream
 * nodes that already reference the old name.
 */
export function renameVariableInNodeData(
  data: Record<string, unknown>,
  oldAlias: string,
  newAlias: string,
): Record<string, unknown> {
  const raw = JSON.stringify(data.config ?? {});
  const pattern = new RegExp(`\\{\\{\\s*${oldAlias.replaceAll(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\.`, 'g');
  const updated = raw.replace(pattern, `{{${newAlias}.`);
  try {
    return { ...data, config: JSON.parse(updated) };
  } catch {
    return data;
  }
}

/** Rewrite legacy condition shortcuts and conditionExpression variable refs on an edge. */
export function renameVariableInEdge(edge: Edge, oldAlias: string, newAlias: string): Edge {
  const data = (edge.data as Record<string, unknown>) || {};
  let changed = false;
  const next: Record<string, unknown> = { ...data };
  // Legacy: condition: 'stepId.success' or 'stepId.failed'
  if (typeof data.condition === 'string' && (data.condition === `${oldAlias}.success` || data.condition === `${oldAlias}.failed`)) {
    next.condition = data.condition.replace(`${oldAlias}.`, `${newAlias}.`);
    changed = true;
  }
  // ConditionExpression: walk recursively replacing { variable: oldAlias } operands
  const walk = (v: unknown): unknown => {
    if (!v || typeof v !== 'object') return v;
    if (Array.isArray(v)) return v.map(walk);
    const obj = v as Record<string, unknown>;
    const out: Record<string, unknown> = {};
    for (const [k, val] of Object.entries(obj)) {
      if (k === 'variable' && val === oldAlias) { out[k] = newAlias; changed = true; }
      else out[k] = walk(val);
    }
    return out;
  };
  if (data.conditionExpression) next.conditionExpression = walk(data.conditionExpression);
  return changed ? { ...edge, data: next } : edge;
}
