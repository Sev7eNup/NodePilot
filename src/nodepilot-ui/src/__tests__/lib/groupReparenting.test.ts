import { describe, it, expect } from 'vitest';
import type { Node } from '@xyflow/react';
import { reparentDraggedNodes, findDropTargetGroupId } from '../../lib/groupReparenting';

// Minimal node factory. Groups size via `style`, activities via `measured` — matching the designer.
function mk(id: string, type: string, pos: { x: number; y: number }, opts: Partial<Node> = {}): Node {
  return { id, type, position: pos, data: {}, ...opts } as Node;
}
const group = (id: string, x: number, y: number, w: number, h: number, data: Record<string, unknown> = {}) =>
  mk(id, 'group', { x, y }, { style: { width: w, height: h }, data });
const activity = (id: string, x: number, y: number, opts: Partial<Node> = {}) =>
  mk(id, 'activity', { x, y }, { measured: { width: 220, height: 60 }, ...opts });

const byId = (nodes: Node[] | null) => new Map((nodes ?? []).map((n) => [n.id, n]));

describe('reparentDraggedNodes', () => {
  it('adoptsTopLevelNodeWhoseCenterEntersGroup', () => {
    // Group rect [100..400]x[100..300]; node at (150,150) → center (260,180) is inside.
    const nodes = [group('g', 100, 100, 300, 200), activity('a', 150, 150)];
    const out = reparentDraggedNodes(nodes, ['a']);
    expect(out).not.toBeNull();
    const a = byId(out).get('a')!;
    expect(a.parentId).toBe('g');
    // Position becomes group-relative so the node does not visually jump.
    expect(a.position).toEqual({ x: 50, y: 50 });
  });

  it('detachesChildDraggedOutOfItsGroup', () => {
    // Child relative (350,50) → abs (450,150), center (560,180) is past the group's right edge (400).
    const nodes = [
      group('g', 100, 100, 300, 200),
      activity('a', 350, 50, { parentId: 'g' }),
    ];
    const out = reparentDraggedNodes(nodes, ['a']);
    expect(out).not.toBeNull();
    const a = byId(out).get('a')!;
    expect(a.parentId).toBeUndefined();
    // Restored to absolute canvas coordinates.
    expect(a.position).toEqual({ x: 450, y: 150 });
  });

  it('movesChildFromOneGroupToAnother', () => {
    const nodes = [
      group('g1', 100, 100, 200, 200), // [100..300]
      group('g2', 500, 100, 200, 200), // [500..700]
      activity('a', 420, 20, { parentId: 'g1' }), // abs (520,120), center (630,150) → inside g2
    ];
    const out = reparentDraggedNodes(nodes, ['a']);
    const a = byId(out).get('a')!;
    expect(a.parentId).toBe('g2');
    expect(a.position).toEqual({ x: 20, y: 20 }); // 520-500, 120-100
  });

  it('isNoOpWhenNodeStaysInsideSameGroup', () => {
    const nodes = [group('g', 100, 100, 300, 200), activity('a', 50, 50, { parentId: 'g' })];
    expect(reparentDraggedNodes(nodes, ['a'])).toBeNull();
  });

  it('isNoOpWhenThereAreNoGroups', () => {
    const nodes = [activity('a', 10, 10), activity('b', 300, 300)];
    expect(reparentDraggedNodes(nodes, ['a'])).toBeNull();
  });

  it('neverReparentsAGroupItself', () => {
    const nodes = [group('outer', 0, 0, 800, 600), group('g', 100, 100, 200, 200)];
    expect(reparentDraggedNodes(nodes, ['g'])).toBeNull();
  });

  it('ignoresPartialOverlapWhenCenterIsOutside', () => {
    // Node abs (380,150), center (490,180) — overlaps the group edge but its center is past x=400.
    const nodes = [group('g', 100, 100, 300, 200), activity('a', 380, 150)];
    expect(reparentDraggedNodes(nodes, ['a'])).toBeNull();
  });

  it('doesNotAdoptIntoCollapsedGroups', () => {
    const nodes = [
      group('g', 100, 100, 300, 200, { collapsed: true }),
      activity('a', 150, 150), // center inside the full bounds, but the group renders collapsed
    ];
    expect(reparentDraggedNodes(nodes, ['a'])).toBeNull();
  });

  it('keepsGroupsFirstInTheReturnedArray', () => {
    // Group listed AFTER the activity; result must reorder so the group precedes its new child.
    const nodes = [activity('a', 150, 150), group('g', 100, 100, 300, 200)];
    const out = reparentDraggedNodes(nodes, ['a'])!;
    expect(out[0].id).toBe('g');
    expect(out[1].id).toBe('a');
    expect(out[1].parentId).toBe('g');
  });

  it('reparentsEveryNodeOfAMultiSelectDrag', () => {
    const nodes = [
      group('g', 100, 100, 400, 400),
      activity('a', 150, 150),
      activity('b', 200, 250),
    ];
    const out = reparentDraggedNodes(nodes, ['a', 'b'])!;
    expect(byId(out).get('a')!.parentId).toBe('g');
    expect(byId(out).get('b')!.parentId).toBe('g');
  });

  it('picksTheTopmostGroupWhenTwoOverlap', () => {
    // Both groups cover the node's center; the LAST in array order renders on top → wins.
    const nodes = [
      group('back', 0, 0, 500, 500),
      group('front', 100, 100, 300, 300), // [100..400]; rendered after → on top
      activity('a', 150, 150), // center (260,180) inside both
    ];
    const out = reparentDraggedNodes(nodes, ['a'])!;
    expect(byId(out).get('a')!.parentId).toBe('front');
  });
});

describe('findDropTargetGroupId', () => {
  it('returnsGroupIdWhenCenterIsInside', () => {
    const nodes = [group('g', 100, 100, 300, 200), activity('a', 150, 150)];
    expect(findDropTargetGroupId(nodes, nodes[1])).toBe('g');
  });

  it('returnsNullWhenCenterIsOutsideEveryGroup', () => {
    const nodes = [group('g', 100, 100, 300, 200), activity('a', 600, 600)];
    expect(findDropTargetGroupId(nodes, nodes[1])).toBeNull();
  });

  it('usesTheDraggedNodesLivePositionForChildren', () => {
    // A child with a live (in-flight) relative position whose absolute center moved into g2.
    const g1 = group('g1', 100, 100, 200, 200);
    const g2 = group('g2', 500, 100, 200, 200);
    const dragged = activity('a', 420, 20, { parentId: 'g1' }); // abs (520,120) → center inside g2
    expect(findDropTargetGroupId([g1, g2, dragged], dragged)).toBe('g2');
  });

  it('ignoresCollapsedGroups', () => {
    const nodes = [group('g', 100, 100, 300, 200, { collapsed: true }), activity('a', 150, 150)];
    expect(findDropTargetGroupId(nodes, nodes[1])).toBeNull();
  });

  it('returnsNullForADraggedGroup', () => {
    const nodes = [group('outer', 0, 0, 800, 600), group('g', 100, 100, 200, 200)];
    expect(findDropTargetGroupId(nodes, nodes[1])).toBeNull();
  });

  it('picksTheTopmostGroupOnOverlap', () => {
    const nodes = [
      group('back', 0, 0, 500, 500),
      group('front', 100, 100, 300, 300),
      activity('a', 150, 150),
    ];
    expect(findDropTargetGroupId(nodes, nodes[2])).toBe('front');
  });
});
