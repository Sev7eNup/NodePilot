import { describe, it, expect } from 'vitest';
import type { Edge } from '@xyflow/react';
import { classifyReattachTarget, detachedSourcePoint } from '../../lib/edgeDetach';

/**
 * Edge detach ("release target" in the edge context menu): classification of a clicked node
 * including verdict precedence, plus the anchor point of the preview line per port side.
 * The current target node counts as a valid target, because reattaching there is how the port
 * side is changed; the duplicate rule therefore excludes the edge being moved. Whether the drop
 * changes anything at all is decided by the caller in WorkflowEditorPage.
 */

const EDGES: Edge[] = [
  { id: 'e1', source: 'a', target: 'b' },
  { id: 'e2', source: 'a', target: 'c' },
];

function classify(candidateId: string, type: string | undefined, edges: Edge[] = EDGES) {
  return classifyReattachTarget({
    edges,
    edgeId: 'e1',
    sourceId: 'a',
    candidate: { id: candidateId, type },
  });
}

/** The common case: an activity node. */
const activity = (candidateId: string, edges?: Edge[]) => classify(candidateId, 'activity', edges);

describe('classifyReattachTarget', () => {
  it('freeActivityNode_returnsOk', () => {
    expect(activity('d')).toBe('ok');
  });

  it('currentTarget_returnsOk_soThePortCanBeRepicked', () => {
    // Reattaching to the current target is the only way to change the port side without
    // deleting the edge and its condition. The `e.id !== edgeId` filter keeps the edge from
    // seeing itself as a duplicate.
    expect(activity('b')).toBe('ok');
  });

  it('sourceNode_returnsSelfLoop', () => {
    expect(activity('a')).toBe('selfLoop');
  });

  it('groupNode_returnsInvalidTarget', () => {
    expect(classify('d', 'group')).toBe('invalidTarget');
  });

  it('stickyNoteNode_returnsInvalidTarget', () => {
    expect(classify('d', 'stickyNote')).toBe('invalidTarget');
  });

  it('nodeWithoutType_returnsInvalidTarget', () => {
    expect(classify('d', undefined)).toBe('invalidTarget');
  });

  it('nodeAlreadyReachedFromSameSource_returnsDuplicate', () => {
    expect(activity('c')).toBe('duplicate');
  });

  it('sameTargetFromDifferentSource_returnsOk', () => {
    // Another node may point at the same target: the rule is one edge per node pair, not one
    // incoming neighbor.
    const edges: Edge[] = [{ id: 'e1', source: 'a', target: 'b' }, { id: 'e2', source: 'x', target: 'c' }];
    expect(activity('c', edges)).toBe('ok');
  });

  it('edgeBeingMoved_isExcludedFromDuplicateCheck', () => {
    // Only e1 exists: clicking a free target must not fail because of the edge's own entry.
    const edges: Edge[] = [{ id: 'e1', source: 'a', target: 'b' }];
    expect(activity('z', edges)).toBe('ok');
  });

  it('sourceNodeThatIsAlsoAGroup_prefersSelfLoopVerdict', () => {
    // Precedence: the more specific verdict wins over the generic type rejection.
    expect(classify('a', 'group')).toBe('selfLoop');
  });
});

describe('detachedSourcePoint', () => {
  const node = { measured: { width: 200, height: 80 }, internals: { positionAbsolute: { x: 100, y: 50 } } };

  it('rightPort_returnsMidRightEdge', () => {
    expect(detachedSourcePoint(node, 'right')).toEqual({ x: 300, y: 90 });
  });

  it('leftPort_returnsMidLeftEdge', () => {
    expect(detachedSourcePoint(node, 'left')).toEqual({ x: 100, y: 90 });
  });

  it('topPort_returnsMidTopEdge', () => {
    expect(detachedSourcePoint(node, 'top')).toEqual({ x: 200, y: 50 });
  });

  it('bottomPort_returnsMidBottomEdge', () => {
    expect(detachedSourcePoint(node, 'bottom')).toEqual({ x: 200, y: 130 });
  });

  it('missingNode_returnsNull', () => {
    expect(detachedSourcePoint(null, 'right')).toBeNull();
    expect(detachedSourcePoint(undefined, 'right')).toBeNull();
  });

  it('unmeasuredNode_returnsNull', () => {
    // React Flow measures nodes only after the first layout pass. Without a size there is no
    // sensible anchor, so the caller renders nothing instead of pointing at (0,0).
    expect(detachedSourcePoint({ measured: {}, internals: { positionAbsolute: { x: 1, y: 2 } } }, 'right')).toBeNull();
  });
});
