import type { Edge, XYPosition } from '@xyflow/react';
import { getPortPoint, isEdgePortSide, nearestPortPoint, type EdgePortSide, type PortPoint } from './edgePorts';

/**
 * Edge detach: the edge context menu releases the target end of an edge, and the next click on
 * a node reattaches it there. The edge is not mutated while detached; the detach state lives in
 * WorkflowEditorPage, so every way out (Esc, pane click, right click) leaves no history entry
 * and no `isDirty`.
 *
 * This file holds the two parts that are testable without React Flow: classifying a click
 * target and computing the source endpoint for the preview line.
 */

/**
 * Result of clicking a node while one end of an edge is detached.
 *
 * - `ok`            — attach here. Includes the current target node, so reattaching there is
 *                     the way to change only the port side. Whether anything actually changes
 *                     is up to the caller (same node and same port is a no-op).
 * - `selfLoop`      — the edge's own source node. A self-edge carries no meaning in the graph.
 * - `duplicate`     — another edge from the same source already reaches this node. Same rule
 *                     as `onConnect`/`onReconnect`: at most one edge per node pair.
 * - `invalidTarget` — a group or sticky note. Neither has execution semantics.
 */
export type ReattachVerdict = 'ok' | 'selfLoop' | 'duplicate' | 'invalidTarget';

export interface ReattachCandidate {
  id: string;
  type?: string;
}

export function classifyReattachTarget({
  edges,
  edgeId,
  sourceId,
  candidate,
}: {
  edges: Edge[];
  edgeId: string;
  sourceId: string;
  candidate: ReattachCandidate;
}): ReattachVerdict {
  // Order matters: the source node gets its own verdict instead of the generic type rejection.
  if (candidate.id === sourceId) return 'selfLoop';
  if (candidate.type !== 'activity') return 'invalidTarget';
  // `e.id !== edgeId` excludes the edge being moved, so reattaching it to its own target
  // does not count as a duplicate.
  if (edges.some((e) => e.id !== edgeId && e.source === sourceId && e.target === candidate.id)) {
    return 'duplicate';
  }
  return 'ok';
}

/**
 * Minimal slice of React Flow's `InternalNode`. Structurally typed so tests do not have to
 * build a complete node-internals object.
 */
export interface SourceNodeGeometry {
  measured: { width?: number; height?: number };
  internals: { positionAbsolute: XYPosition };
}

/**
 * Anchor point of the preview line: the center of the source port in flow coordinates. Uses
 * the same port geometry as the rest of the designer (`getPortPoint`), so the preview starts
 * exactly where the committed edge will start.
 *
 * Returns `null` while React Flow has not measured the node yet; the caller then renders
 * nothing.
 */
export function detachedSourcePoint(
  node: SourceNodeGeometry | null | undefined,
  side: EdgePortSide,
): XYPosition | null {
  if (!node) return null;
  const w = node.measured.width;
  const h = node.measured.height;
  if (!w || !h) return null;
  const { x, y } = node.internals.positionAbsolute;
  return getPortPoint({ x, y, w, h }, side);
}

/**
 * The four port centers of a node in screen coordinates, measured from the DOM.
 *
 * Measured rather than computed: the outer `.react-flow__node` rectangle also covers the label
 * below the shape, and `portHandleStyle` insets individual sides per shape via `handleInset`,
 * so points derived from `width`/`height` would sit next to the real handles and let the wrong
 * port win. `getBoundingClientRect` is zoom-correct as well.
 *
 * React Flow puts `data-handleid` (here the port side, see the `id={side}` handles in
 * ActivityNode) and `data-nodeid` on every handle. Filtering on `data-nodeid` keeps the handles
 * of nested node elements out.
 */
export function readHandlePoints(nodeEl: Element, nodeId: string): PortPoint[] {
  const points: PortPoint[] = [];
  for (const handle of nodeEl.querySelectorAll('.react-flow__handle')) {
    if (handle.getAttribute('data-nodeid') !== nodeId) continue;
    const port = handle.getAttribute('data-handleid');
    if (!isEdgePortSide(port)) continue;
    const r = handle.getBoundingClientRect();
    points.push({ port, x: r.left + r.width / 2, y: r.top + r.height / 2 });
  }
  return points;
}

export interface DockTarget {
  nodeId: string;
  port: EdgePortSide;
  /** Center of the chosen handle in screen coordinates. */
  screenPoint: { x: number; y: number };
}

/**
 * Resolves where a pointer at `(clientX, clientY)` would dock.
 *
 * The single resolution path for both sides of edge detach: the preview line on `pointermove`
 * and the actual reattach on click. Sharing it keeps the preview from showing something other
 * than what the click produces.
 *
 * `canDockTo` filters out nodes that would be rejected as a target anyway (source node,
 * duplicate, group or sticky note), so neither the line nor the hover ring reaches them.
 */
export function resolveDockTarget(
  target: EventTarget | null,
  clientX: number,
  clientY: number,
  canDockTo: (nodeId: string) => boolean,
): DockTarget | null {
  if (!(target instanceof Element)) return null;
  const nodeEl = target.closest('.react-flow__node');
  const nodeId = nodeEl?.getAttribute('data-id');
  if (!nodeEl || !nodeId || !canDockTo(nodeId)) return null;
  const nearest = nearestPortPoint(readHandlePoints(nodeEl, nodeId), clientX, clientY);
  if (!nearest) return null;
  return { nodeId, port: nearest.port, screenPoint: { x: nearest.x, y: nearest.y } };
}
