import type { Edge, Node } from '@xyflow/react';

/**
 * Generic, id-stable diff between two workflow definitions. Unlike the older
 * version-history diff (which compared edges by `source→target` and therefore missed
 * handle/id/layout changes), this diff keys nodes and edges by their stable `id` and
 * compares `data`/`config`, edge `source`/`target`/`sourceHandle`/`targetHandle`, and
 * node `position`/`type`/`parentId`.
 */
export interface DefinitionDiff {
  addedNodes: string[];
  removedNodes: string[];
  changedNodes: string[];
  addedEdges: string[];
  removedEdges: string[];
  changedEdges: string[];
}

export interface WorkflowDefinition {
  nodes: Node[];
  edges: Edge[];
}

/** Stable JSON serialization (keys sorted recursively) for deterministic comparisons. */
function stableStringify(value: unknown): string {
  if (value === null || typeof value !== 'object') return JSON.stringify(value) ?? 'null';
  if (Array.isArray(value)) return `[${value.map(stableStringify).join(',')}]`;
  const obj = value as Record<string, unknown>;
  const keys = Object.keys(obj).sort();
  return `{${keys.map((k) => `${JSON.stringify(k)}:${stableStringify(obj[k])}`).join(',')}}`;
}

function nodeSignature(node: Node): string {
  return stableStringify({
    type: node.type ?? 'activity',
    parentId: (node as { parentId?: unknown }).parentId ?? null,
    position: node.position ?? null,
    data: node.data ?? null,
  });
}

/** Signature without position — lets us tell a pure layout move apart from an actual content change. */
function nodeSignatureNoPosition(node: Node): string {
  return stableStringify({
    type: node.type ?? 'activity',
    parentId: (node as { parentId?: unknown }).parentId ?? null,
    data: node.data ?? null,
  });
}

/** True when a changed node differs ONLY in position (no actual content change). */
export function isLayoutOnlyNodeChange(prev: Node, node: Node): boolean {
  return nodeSignatureNoPosition(prev) === nodeSignatureNoPosition(node);
}

function edgeSignature(edge: Edge): string {
  return stableStringify({
    source: edge.source,
    target: edge.target,
    sourceHandle: edge.sourceHandle ?? null,
    targetHandle: edge.targetHandle ?? null,
    type: edge.type ?? null,
    data: edge.data ?? null,
  });
}

export function computeDefinitionDiff(base: WorkflowDefinition, current: WorkflowDefinition): DefinitionDiff {
  const baseNodes = new Map(base.nodes.map((n) => [n.id, n]));
  const curNodes = new Map(current.nodes.map((n) => [n.id, n]));
  const addedNodes: string[] = [];
  const removedNodes: string[] = [];
  const changedNodes: string[] = [];
  for (const [id, node] of curNodes) {
    const prev = baseNodes.get(id);
    if (!prev) addedNodes.push(id);
    else if (nodeSignature(prev) !== nodeSignature(node)) changedNodes.push(id);
  }
  for (const id of baseNodes.keys()) {
    if (!curNodes.has(id)) removedNodes.push(id);
  }

  const baseEdges = new Map(base.edges.map((e) => [e.id, e]));
  const curEdges = new Map(current.edges.map((e) => [e.id, e]));
  const addedEdges: string[] = [];
  const removedEdges: string[] = [];
  const changedEdges: string[] = [];
  for (const [id, edge] of curEdges) {
    const prev = baseEdges.get(id);
    if (!prev) addedEdges.push(id);
    else if (edgeSignature(prev) !== edgeSignature(edge)) changedEdges.push(id);
  }
  for (const id of baseEdges.keys()) {
    if (!curEdges.has(id)) removedEdges.push(id);
  }

  return { addedNodes, removedNodes, changedNodes, addedEdges, removedEdges, changedEdges };
}

// ---- Structured changelog + selective apply -------------------------------

export type ChangeKind = 'add' | 'remove' | 'change';

export interface ChangelogEntry {
  id: string;
  kind: ChangeKind;
  target: 'node' | 'edge';
  /** Node label, or "src → tgt" for edges. */
  label: string;
  /** activityType (nodes only). */
  activityType?: string;
  /** Only set for changed nodes: a pure position move (not applied by default). */
  layoutOnly?: boolean;
}

const nodeLabelOf = (n?: Node): string => (n?.data?.label as string) || n?.id || '?';

/** Labeled, ordered change list (adds, changes, removes) for the proposal card. */
export function buildChangelog(base: WorkflowDefinition, proposed: WorkflowDefinition): ChangelogEntry[] {
  const diff = computeDefinitionDiff(base, proposed);
  const baseN = new Map(base.nodes.map((n) => [n.id, n]));
  const propN = new Map(proposed.nodes.map((n) => [n.id, n]));
  const labelAt = (id: string) => nodeLabelOf(propN.get(id) ?? baseN.get(id));
  const baseE = new Map(base.edges.map((e) => [e.id, e]));
  const propE = new Map(proposed.edges.map((e) => [e.id, e]));
  const edgeLabel = (e?: Edge): string => (e ? `${labelAt(e.source)} → ${labelAt(e.target)}` : '?');

  const out: ChangelogEntry[] = [];
  for (const id of diff.addedNodes) {
    const n = propN.get(id);
    out.push({ id, kind: 'add', target: 'node', label: nodeLabelOf(n), activityType: n?.data?.activityType as string });
  }
  for (const id of diff.changedNodes) {
    const b = baseN.get(id); const p = propN.get(id);
    out.push({ id, kind: 'change', target: 'node', label: nodeLabelOf(p), activityType: p?.data?.activityType as string, layoutOnly: !!(b && p && isLayoutOnlyNodeChange(b, p)) });
  }
  for (const id of diff.removedNodes) {
    const n = baseN.get(id);
    out.push({ id, kind: 'remove', target: 'node', label: nodeLabelOf(n), activityType: n?.data?.activityType as string });
  }
  for (const id of diff.addedEdges) out.push({ id, kind: 'add', target: 'edge', label: edgeLabel(propE.get(id)) });
  for (const id of diff.changedEdges) out.push({ id, kind: 'change', target: 'edge', label: edgeLabel(propE.get(id)) });
  for (const id of diff.removedEdges) out.push({ id, kind: 'remove', target: 'edge', label: edgeLabel(baseE.get(id)) });
  return out;
}

/**
 * Builds the definition to apply from the current canvas plus the SELECTED (by id) changelog
 * entries. Conflict rules: deleting a node also removes its incident edges; edges left with
 * a missing endpoint (a deleted/not-selected node) are dropped (tracked via `droppedEdges`).
 * The caller gates this on staleness (current === base), so diffing against `current` here
 * matches the changelog exactly.
 */
export function assembleSelectiveDefinition(
  current: WorkflowDefinition,
  proposed: WorkflowDefinition,
  selectedIds: ReadonlySet<string>,
): { nodes: Node[]; edges: Edge[]; droppedEdges: number } {
  const diff = computeDefinitionDiff(current, proposed);
  const propN = new Map(proposed.nodes.map((n) => [n.id, n]));
  const propE = new Map(proposed.edges.map((e) => [e.id, e]));
  const nodes = new Map(current.nodes.map((n) => [n.id, n]));
  const edges = new Map(current.edges.map((e) => [e.id, e]));

  for (const id of [...diff.addedNodes, ...diff.changedNodes]) {
    if (selectedIds.has(id) && propN.has(id)) nodes.set(id, propN.get(id)!);
  }
  for (const id of diff.removedNodes) if (selectedIds.has(id)) nodes.delete(id);
  for (const id of [...diff.addedEdges, ...diff.changedEdges]) {
    if (selectedIds.has(id) && propE.has(id)) edges.set(id, propE.get(id)!);
  }
  for (const id of diff.removedEdges) if (selectedIds.has(id)) edges.delete(id);

  let droppedEdges = 0;
  for (const [id, e] of [...edges]) {
    if (!nodes.has(e.source) || !nodes.has(e.target)) { edges.delete(id); droppedEdges++; }
  }
  return { nodes: [...nodes.values()], edges: [...edges.values()], droppedEdges };
}

export function diffIsEmpty(diff: DefinitionDiff): boolean {
  return (
    diff.addedNodes.length === 0 &&
    diff.removedNodes.length === 0 &&
    diff.changedNodes.length === 0 &&
    diff.addedEdges.length === 0 &&
    diff.removedEdges.length === 0 &&
    diff.changedEdges.length === 0
  );
}

/**
 * Stable hash of a definition, used to guard against stale proposals: if the canvas changes
 * between "ask the question" and "apply the proposal", the freshly computed hash won't match
 * the hash that shipped with the proposal, and the apply is blocked. Uses FNV-1a over the
 * id-sorted, stably serialized projection (exactly the fields the diff compares).
 */
export function hashDefinition(def: WorkflowDefinition): string {
  const nodes = [...def.nodes]
    .sort((a, b) => a.id.localeCompare(b.id))
    .map((n) => ({ id: n.id, sig: nodeSignature(n) }));
  const edges = [...def.edges]
    .sort((a, b) => a.id.localeCompare(b.id))
    .map((e) => ({ id: e.id, sig: edgeSignature(e) }));
  const canonical = stableStringify({ nodes, edges });

  let hash = 0x811c9dc5;
  for (let i = 0; i < canonical.length; i++) {
    hash ^= canonical.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16).padStart(8, '0');
}
