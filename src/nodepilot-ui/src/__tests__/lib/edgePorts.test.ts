import { describe, it, expect } from 'vitest';
import { Position, type Edge } from '@xyflow/react';
import {
  EDGE_PORT_SIDES,
  DEFAULT_SOURCE_PORT,
  DEFAULT_TARGET_PORT,
  isEdgePortSide,
  normalizePort,
  edgeSourcePort,
  edgeTargetPort,
  withDefaultEdgePorts,
  oppositePort,
  portToPosition,
  getPortPoint,
} from '../../lib/edgePorts';

describe('constants', () => {
  it('lists the four sides and sane defaults', () => {
    expect(EDGE_PORT_SIDES).toEqual(['top', 'right', 'bottom', 'left']);
    expect(DEFAULT_SOURCE_PORT).toBe('right');
    expect(DEFAULT_TARGET_PORT).toBe('left');
  });
});

describe('isEdgePortSide', () => {
  it('accepts the four valid sides', () => {
    for (const side of ['top', 'right', 'bottom', 'left']) {
      expect(isEdgePortSide(side)).toBe(true);
    }
  });
  it('rejects anything else', () => {
    for (const bad of ['diagonal', '', 'Top', null, undefined, 42, {}]) {
      expect(isEdgePortSide(bad)).toBe(false);
    }
  });
});

describe('normalizePort', () => {
  it('returns the value when it is a valid side', () => {
    expect(normalizePort('top', 'left')).toBe('top');
    expect(normalizePort('bottom', 'right')).toBe('bottom');
  });
  it('falls back for invalid or missing values', () => {
    expect(normalizePort('nonsense', 'left')).toBe('left');
    expect(normalizePort(undefined, 'right')).toBe('right');
    expect(normalizePort(null, 'top')).toBe('top');
    expect(normalizePort(123, 'bottom')).toBe('bottom');
  });
});

describe('edgeSourcePort / edgeTargetPort', () => {
  it('reads a valid handle through unchanged', () => {
    expect(edgeSourcePort({ sourceHandle: 'top' })).toBe('top');
    expect(edgeTargetPort({ targetHandle: 'bottom' })).toBe('bottom');
  });
  it('applies the per-role default when the handle is missing/invalid', () => {
    expect(edgeSourcePort({ sourceHandle: null })).toBe(DEFAULT_SOURCE_PORT);
    expect(edgeTargetPort({ targetHandle: 'junk' })).toBe(DEFAULT_TARGET_PORT);
  });
});

describe('withDefaultEdgePorts', () => {
  it('preserves an already-set port identity (returns the same object)', () => {
    const edge = { id: 'e1', source: 'a', target: 'b', sourceHandle: 'top', targetHandle: 'bottom' } as Edge;
    const result = withDefaultEdgePorts(edge);
    expect(result).toBe(edge); // no allocation when handles are already normalized
  });

  it('fills in defaults when handles are missing', () => {
    const edge = { id: 'e1', source: 'a', target: 'b' } as Edge;
    const result = withDefaultEdgePorts(edge);
    expect(result).not.toBe(edge);
    expect(result.sourceHandle).toBe('right');
    expect(result.targetHandle).toBe('left');
  });

  it('normalizes an invalid handle to the default', () => {
    const edge = { id: 'e1', source: 'a', target: 'b', sourceHandle: 'diagonal', targetHandle: 'top' } as unknown as Edge;
    const result = withDefaultEdgePorts(edge);
    expect(result).not.toBe(edge);
    expect(result.sourceHandle).toBe('right'); // invalid → default
    expect(result.targetHandle).toBe('top');   // valid → kept
  });
});

describe('oppositePort', () => {
  it('mirrors each side', () => {
    expect(oppositePort('top')).toBe('bottom');
    expect(oppositePort('bottom')).toBe('top');
    expect(oppositePort('left')).toBe('right');
    expect(oppositePort('right')).toBe('left');
  });
});

describe('portToPosition', () => {
  it('maps each side to the React Flow Position enum', () => {
    expect(portToPosition('top')).toBe(Position.Top);
    expect(portToPosition('right')).toBe(Position.Right);
    expect(portToPosition('bottom')).toBe(Position.Bottom);
    expect(portToPosition('left')).toBe(Position.Left);
  });
});

describe('getPortPoint', () => {
  const bounds = { x: 10, y: 20, w: 100, h: 40 };

  it('anchors the top port at the top-center', () => {
    expect(getPortPoint(bounds, 'top')).toEqual({ x: 60, y: 20 });
  });
  it('anchors the right port at the right-middle', () => {
    expect(getPortPoint(bounds, 'right')).toEqual({ x: 110, y: 40 });
  });
  it('anchors the bottom port at the bottom-center', () => {
    expect(getPortPoint(bounds, 'bottom')).toEqual({ x: 60, y: 60 });
  });
  it('anchors the left port at the left-middle', () => {
    expect(getPortPoint(bounds, 'left')).toEqual({ x: 10, y: 40 });
  });
});
