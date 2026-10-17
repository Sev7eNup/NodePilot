import type { Edge, Node } from '@xyflow/react';
import { randomUuid } from './uuid';
import { renameVariableInNodeData } from './editorGraphHelpers';
import { stripRuntimeDefinition } from './workflowDefinitionSanitizer';

/**
 * Part of a workflow: some nodes and the edges between them, in the same shape as a saved
 * workflow definition. Copy and paste in the designer move it through the system clipboard as
 * JSON text; duplicating moves it directly.
 */
export interface WorkflowFragment {
  nodes: Node[];
  edges: Edge[];
}

/** Canvas state React Flow keeps on nodes and edges. It means nothing outside the editor. */
const VIEW_PROPS = ['selected', 'dragging', 'measured', 'resizing'] as const;
const NODE_TYPES: ReadonlySet<string> = new Set(['activity', 'group', 'stickyNote']);
/** The mask the server writes in place of a secret. */
const REDACTED = '***';

/**
 * The given nodes, the nodes inside any given group, and every edge whose two ends are among
 * them. Node and edge data are copied, so later edits on the canvas do not reach into the
 * fragment.
 */
export function selectFragment(nodes: Node[], edges: Edge[], nodeIds: Iterable<string>): WorkflowFragment {
  const ids = new Set(nodeIds);
  const groupIds = new Set(nodes.filter((n) => n.type === 'group' && ids.has(n.id)).map((n) => n.id));
  for (const n of nodes) if (n.parentId && groupIds.has(n.parentId)) ids.add(n.id);
  return {
    nodes: nodes
      .filter((n) => ids.has(n.id))
      .map((n) => ({ ...n, data: { ...(n.data as object) } as Node['data'] })),
    edges: edges
      .filter((e) => ids.has(e.source) && ids.has(e.target))
      .map((e) => ({ ...e, data: e.data ? { ...(e.data as object) } : undefined })),
  };
}

/** The fragment as indented JSON text: the definition shape, without canvas-only state. */
export function serializeFragment(fragment: WorkflowFragment): string {
  const clean = stripRuntimeDefinition(fragment);
  return JSON.stringify({
    nodes: clean.nodes.map(withoutViewProps),
    edges: clean.edges.map(withoutViewProps),
  }, null, 2);
}

export type FragmentParseError = 'invalidJson' | 'noNodes' | 'invalidNode' | 'duplicateId';

export type FragmentParseResult =
  | {
    ok: true;
    fragment: WorkflowFragment;
    /** Edges left out because one of their ends is not part of the text. */
    droppedEdges: number;
    /** Values that are exactly the server's secret mask; pasted as they are, they stay `***`. */
    redactedValues: number;
  }
  | { ok: false; error: FragmentParseError };

/**
 * Reads pasted text as a fragment. Accepts any object with a `nodes` array, so a complete
 * workflow definition works as well. The text comes from outside the editor, so every node is
 * checked for the fields the canvas needs; edges that do not fit are left out.
 */
export function parseFragment(text: string): FragmentParseResult {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return { ok: false, error: 'invalidJson' };
  }
  if (!isRecord(parsed) || !Array.isArray(parsed.nodes) || parsed.nodes.length === 0) {
    return { ok: false, error: 'noNodes' };
  }
  const nodes: Node[] = [];
  const ids = new Set<string>();
  for (const candidate of parsed.nodes) {
    if (!isNodeShape(candidate)) return { ok: false, error: 'invalidNode' };
    if (ids.has(candidate.id)) return { ok: false, error: 'duplicateId' };
    ids.add(candidate.id);
    nodes.push(candidate);
  }
  const rawEdges: unknown[] = Array.isArray(parsed.edges) ? parsed.edges : [];
  const edges = rawEdges
    .filter(isEdgeShape)
    .filter((e) => ids.has(e.source) && ids.has(e.target));
  const fragment = stripRuntimeDefinition({
    nodes: nodes.map(withoutViewProps),
    edges: edges.map(withoutViewProps),
  });
  return {
    ok: true,
    fragment,
    droppedEdges: rawEdges.length - edges.length,
    redactedValues: countRedacted([...fragment.nodes.map((n) => n.data), ...fragment.edges.map((e) => e.data)]),
  };
}

/**
 * Prepares a fragment for insertion: fresh IDs for every node and edge, and every reference
 * to an old node ID rewritten to the new one (edge ends, group membership, edge conditions,
 * `{{id.…}}` templates in node config). Top-level nodes move by (dx, dy); children keep their
 * position inside their pasted group. Only a non-group node can sit in a group, as in the
 * designer; a child whose group is neither pasted nor in the workflow becomes a top-level node.
 * The result is selected.
 */
export function remapFragment(
  fragment: WorkflowFragment,
  { existingGroupIds, dx, dy }: { existingGroupIds: ReadonlySet<string>; dx: number; dy: number },
): WorkflowFragment {
  const idMap = new Map<string, string>();
  for (const n of fragment.nodes) idMap.set(n.id, `${idPrefix(n.type)}-${randomUuid().slice(0, 8)}`);
  const pastedGroupIds = new Set(fragment.nodes.filter((n) => n.type === 'group').map((n) => n.id));

  // React Flow needs a group before its children.
  const ordered = [
    ...fragment.nodes.filter((n) => n.type === 'group'),
    ...fragment.nodes.filter((n) => n.type !== 'group'),
  ];
  const nodes = ordered.map((n): Node => {
    const { parentId: oldParentId, extent, ...rest } = n;
    const parentRef = n.type === 'group' ? undefined : oldParentId;
    const pastedParent = parentRef && pastedGroupIds.has(parentRef) ? idMap.get(parentRef) : undefined;
    const parentId = pastedParent ?? (parentRef && existingGroupIds.has(parentRef) ? parentRef : undefined);
    let data: Record<string, unknown> = { ...(n.data as Record<string, unknown>), __liveStatus: undefined };
    for (const [oldId, newId] of idMap) data = renameVariableInNodeData(data, oldId, newId);
    return {
      ...rest,
      ...(parentId ? { parentId, ...(extent ? { extent } : {}) } : {}),
      id: idMap.get(n.id) ?? n.id,
      position: pastedParent ? n.position : { x: n.position.x + dx, y: n.position.y + dy },
      selected: true,
      data,
    };
  });

  const edges = fragment.edges.flatMap((e): Edge[] => {
    const source = idMap.get(e.source);
    const target = idMap.get(e.target);
    if (!source || !target) return [];
    return [{
      ...e,
      id: `edge-${randomUuid().slice(0, 8)}`,
      source,
      target,
      selected: true,
      data: e.data ? remapEdgeData(e.data as Record<string, unknown>, idMap) : e.data,
    }];
  });

  return { nodes, edges };
}

/**
 * Rewrites the step ID in a shortcut condition such as `step-1.success`. A condition naming a
 * step outside `idMap` stays as it is.
 */
export function remapConditionStep(condition: string | undefined, idMap: ReadonlyMap<string, string>): string {
  if (!condition) return '';
  const dot = condition.lastIndexOf('.');
  if (dot <= 0) return condition;
  const mapped = idMap.get(condition.slice(0, dot));
  return mapped ? `${mapped}${condition.slice(dot)}` : condition;
}

function remapEdgeData(data: Record<string, unknown>, idMap: ReadonlyMap<string, string>): Record<string, unknown> {
  const next: Record<string, unknown> = { ...data };
  if (typeof data.condition === 'string') next.condition = remapConditionStep(data.condition, idMap);
  if (data.conditionExpression) next.conditionExpression = remapStepIds(data.conditionExpression, idMap);
  return next;
}

/** Rewrites `stepId` in every variable operand of a condition expression. */
function remapStepIds(value: unknown, idMap: ReadonlyMap<string, string>): unknown {
  if (Array.isArray(value)) return value.map((v) => remapStepIds(v, idMap));
  if (!isRecord(value)) return value;
  const out: Record<string, unknown> = {};
  for (const [key, v] of Object.entries(value)) {
    out[key] = key === 'stepId' && typeof v === 'string' ? (idMap.get(v) ?? v) : remapStepIds(v, idMap);
  }
  return out;
}

function idPrefix(type: string | undefined): string {
  if (type === 'group') return 'group';
  if (type === 'stickyNote') return 'note';
  return 'step';
}

function withoutViewProps<T extends object>(element: T): T {
  const copy = { ...element } as Record<string, unknown>;
  for (const prop of VIEW_PROPS) delete copy[prop];
  return copy as T;
}

function countRedacted(values: unknown[]): number {
  let count = 0;
  const visit = (v: unknown) => {
    if (v === REDACTED) count += 1;
    else if (Array.isArray(v)) v.forEach(visit);
    else if (isRecord(v)) Object.values(v).forEach(visit);
  };
  values.forEach(visit);
  return count;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isNodeShape(value: unknown): value is Node {
  if (!isRecord(value)) return false;
  const { id, type, position, data } = value;
  return typeof id === 'string' && id.length > 0
    && typeof type === 'string' && NODE_TYPES.has(type)
    && isRecord(position) && Number.isFinite(position.x) && Number.isFinite(position.y)
    && isRecord(data);
}

function isEdgeShape(value: unknown): value is Edge {
  if (!isRecord(value)) return false;
  const { id, source, target } = value;
  return typeof id === 'string' && typeof source === 'string' && typeof target === 'string';
}
