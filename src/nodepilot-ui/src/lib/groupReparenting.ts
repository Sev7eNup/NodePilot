import type { Node } from '@xyflow/react';

/**
 * Drag-into-group reparenting. When a drag ends, a node whose center lands inside a group frame
 * becomes that group's child (React Flow `parentId`), and a child whose center leaves its group is
 * detached. Positions convert between absolute and parent-relative so the node does not jump.
 *
 * Groups are top-level and sized via `style.width/height`; children carry `parentId` and a position
 * relative to the group origin. Nested groups are not supported.
 */

type XY = { x: number; y: number };

const DEFAULT_NODE_WIDTH = 220;
const DEFAULT_NODE_HEIGHT = 60;

function pickNumber(...vals: Array<unknown>): number | undefined {
  for (const v of vals) if (typeof v === 'number' && Number.isFinite(v)) return v;
  return undefined;
}

/** Render box of a node: groups carry their size in `style`, activities in `measured`. */
function nodeSize(n: Node): { w: number; h: number } {
  const w = pickNumber(n.style?.width, n.width, n.measured?.width) ?? DEFAULT_NODE_WIDTH;
  const h = pickNumber(n.style?.height, n.height, n.measured?.height) ?? DEFAULT_NODE_HEIGHT;
  return { w, h };
}

/** Absolute canvas position, walking the (cycle-guarded) parent chain. */
function absolutePosition(n: Node, byId: Map<string, Node>): XY {
  let x = n.position.x;
  let y = n.position.y;
  let pid = n.parentId;
  const seen = new Set<string>([n.id]);
  while (pid && !seen.has(pid)) {
    seen.add(pid);
    const p = byId.get(pid);
    if (!p) break;
    x += p.position.x;
    y += p.position.y;
    pid = p.parentId;
  }
  return { x, y };
}

function isCollapsed(n: Node): boolean {
  return (n.data as Record<string, unknown> | undefined)?.collapsed === true;
}

/**
 * @param nodes      current node list (source of truth, not the collapsed render view)
 * @param draggedIds ids of the nodes that just finished dragging (single or multi-select)
 */
export function reparentDraggedNodes(nodes: Node[], draggedIds: string[]): Node[] | null {
  if (draggedIds.length === 0) return null;
  const byId = new Map(nodes.map((n) => [n.id, n]));

  // Drop targets are expanded groups only. A collapsed group renders small, so its full-size
  // bounds would be a misleading hit area.
  const groupRects = nodes
    .filter((n) => n.type === 'group' && !isCollapsed(n))
    .map((g) => {
      const pos = absolutePosition(g, byId);
      const { w, h } = nodeSize(g);
      return { id: g.id, x: pos.x, y: pos.y, w, h };
    });

  const changes = new Map<string, { parentId: string | null; position: XY }>();

  for (const id of draggedIds) {
    const node = byId.get(id);
    if (!node || node.type === 'group') continue; // never reparent a group itself

    const abs = absolutePosition(node, byId);
    const { w, h } = nodeSize(node);
    const center = { x: abs.x + w / 2, y: abs.y + h / 2 };

    // Among overlapping groups, the last in array order renders on top and wins.
    let target: { id: string; x: number; y: number } | null = null;
    for (const r of groupRects) {
      if (r.id === id) continue;
      if (center.x >= r.x && center.x <= r.x + r.w && center.y >= r.y && center.y <= r.y + r.h) {
        target = { id: r.id, x: r.x, y: r.y };
      }
    }

    const currentParent = node.parentId ?? null;
    const targetId = target?.id ?? null;
    if (currentParent === targetId) continue; // unchanged

    changes.set(id, target
      ? { parentId: target.id, position: { x: abs.x - target.x, y: abs.y - target.y } }
      : { parentId: null, position: abs });
  }

  if (changes.size === 0) return null;

  const updated = nodes.map((n) => {
    const c = changes.get(n.id);
    if (!c) return n;
    if (c.parentId) return { ...n, parentId: c.parentId, position: c.position };
    const { parentId: _p, extent: _e, ...rest } = n;
    void _p; void _e;
    return { ...rest, position: c.position };
  });

  // React Flow renders in array order and requires a parent before its children. Keep all groups
  // first (stable), matching the invariant the rest of the editor maintains.
  const groups = updated.filter((n) => n.type === 'group');
  const rest = updated.filter((n) => n.type !== 'group');
  return [...groups, ...rest];
}

/**
 * The id of the expanded group the dragged node's center currently sits over, or null. Drives the
 * live drop-target highlight while dragging, with the same containment rule as
 * {@link reparentDraggedNodes}. Returns the topmost group on overlap; the dragged node's in-flight
 * position comes from `dragged`, so callers do not need fresh state.
 */
export function findDropTargetGroupId(nodes: Node[], dragged: Node): string | null {
  if (dragged.type === 'group') return null;
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const abs = absolutePosition(dragged, byId);
  const { w, h } = nodeSize(dragged);
  const center = { x: abs.x + w / 2, y: abs.y + h / 2 };

  let target: string | null = null;
  for (const g of nodes) {
    if (g.type !== 'group' || g.id === dragged.id || isCollapsed(g)) continue;
    const gpos = absolutePosition(g, byId);
    const { w: gw, h: gh } = nodeSize(g);
    if (center.x >= gpos.x && center.x <= gpos.x + gw && center.y >= gpos.y && center.y <= gpos.y + gh) {
      target = g.id;
    }
  }
  return target;
}
