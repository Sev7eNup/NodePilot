import { describe, expect, it } from 'vitest';
import type { Connection, Edge, Node } from '@xyflow/react';
import { insertJunctionForFanIn, requiresJunctionForConnection } from '../../lib/junctionFanIn';

function node(id: string, activityType: string, x: number, y: number): Node {
  return {
    id,
    type: 'activity',
    position: { x, y },
    data: { label: id, activityType, config: {} },
  };
}

describe('junctionFanIn', () => {
  const nodes = [
    node('a', 'runScript', 0, 0),
    node('b', 'runScript', 0, 200),
    node('target', 'log', 500, 100),
  ];
  const existing: Edge = {
    id: 'e-a-target',
    source: 'a',
    target: 'target',
    sourceHandle: 'right',
    targetHandle: 'left',
    type: 'labeled',
    data: { label: 'A succeeded', condition: 'a.success', disabled: false },
  };

  it('requires a junction for the second incoming connection to an ordinary activity', () => {
    expect(requiresJunctionForConnection('target', nodes, [existing])).toBe(true);
  });

  it('allows multiple incoming connections on a junction', () => {
    const withJoin = [...nodes, node('join', 'junction', 300, 100)];
    const joinEdge = { ...existing, target: 'join' };
    expect(requiresJunctionForConnection('join', withJoin, [joinEdge])).toBe(false);
  });

  it('inserts a waitAll junction and preserves existing edge conditions', () => {
    const connection: Connection = {
      source: 'b',
      target: 'target',
      sourceHandle: 'right',
      targetHandle: 'left',
    };

    const result = insertJunctionForFanIn(nodes, [existing], connection, {
      junctionId: 'join-new',
      incomingEdgeId: 'e-b-join',
      outgoingEdgeId: 'e-join-target',
      label: 'Junction',
    });

    expect(result.junctionId).toBe('join-new');
    expect(result.nodes.find((n) => n.id === 'join-new')?.data).toMatchObject({
      activityType: 'junction',
      config: { mode: 'waitAll' },
    });
    expect(result.edges.filter((e) => e.target === 'target')).toEqual([
      expect.objectContaining({ id: 'e-join-target', source: 'join-new' }),
    ]);
    expect(result.edges.find((e) => e.id === 'e-a-target')).toMatchObject({
      source: 'a',
      target: 'join-new',
      data: { label: 'A succeeded', condition: 'a.success', disabled: false },
    });
    expect(result.edges.find((e) => e.id === 'e-b-join')).toMatchObject({
      source: 'b',
      target: 'join-new',
    });
  });

  it('moves an existing edge through the inserted junction without losing its condition', () => {
    const moving: Edge = {
      id: 'e-b-other',
      source: 'b',
      target: 'other',
      sourceHandle: 'bottom',
      targetHandle: 'top',
      type: 'labeled',
      data: { label: 'B failed', condition: 'b.failed', disabled: false },
    };

    const result = insertJunctionForFanIn(nodes, [existing, moving], {
      source: 'b',
      target: 'target',
      sourceHandle: 'bottom',
      targetHandle: 'left',
    }, {
      junctionId: 'join-new',
      incomingEdgeId: 'unused',
      outgoingEdgeId: 'e-join-target',
      label: 'Junction',
      movingEdge: moving,
    });

    expect(result.edges).toHaveLength(3);
    expect(result.edges.find((e) => e.id === moving.id)).toMatchObject({
      source: 'b',
      target: 'join-new',
      data: { label: 'B failed', condition: 'b.failed', disabled: false },
    });
    expect(result.edges.some((e) => e.target === 'other')).toBe(false);
  });
});
