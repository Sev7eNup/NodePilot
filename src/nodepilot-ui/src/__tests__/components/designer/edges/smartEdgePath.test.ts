import { describe, it, expect } from 'vitest';
import { Position } from '@xyflow/react';
import { edgeArrowPath, defaultControlPoints, getSmartEdgePath, EDGE_ARROW_STROKE_FRAC } from '../../../../components/designer/edges/smartEdgePath';

/**
 * Pins:
 *   - User-defined controlPoints produce a `M…C…` cubic Bezier path with the exact CPs
 *   - Label position is De Casteljau evaluated at t=0.5 (the visual midpoint of the curve)
 *   - controlPoints override even backward-edge U-loops (one segment, not two)
 *   - defaultControlPoints projects outward per port direction (Right/Left/Top/Bottom)
 */

describe('smartEdgePath — controlPoints override', () => {
  const baseParams = {
    sourceX: 0, sourceY: 0,
    targetX: 200, targetY: 0,
    sourcePosition: Position.Right,
    targetPosition: Position.Left,
  };

  it('producesCubicBezierPath_whenControlPointsProvided', () => {
    const segs = getSmartEdgePath({
      ...baseParams,
      controlPoints: { cp1x: 50, cp1y: -100, cp2x: 150, cp2y: -100 },
    });
    expect(segs).toHaveLength(1);
    expect(segs[0][0]).toBe('M 0,0 C 50,-100 150,-100 200,0');
  });

  it('labelPosition_isDeCasteljauAtT05', () => {
    // Symmetric Bezier: cp1=(50,-100), cp2=(150,-100). Visual midpoint should be at
    // x = (sourceX+targetX)/2 = 100 and y = 0.75 * (-100) = -75.
    const segs = getSmartEdgePath({
      ...baseParams,
      controlPoints: { cp1x: 50, cp1y: -100, cp2x: 150, cp2y: -100 },
    });
    const [, labelX, labelY] = segs[0];
    expect(labelX).toBeCloseTo(100, 5);
    expect(labelY).toBeCloseTo(-75, 5);
  });

  it('controlPointsOverride_evenForBackwardEdge', () => {
    // Without controlPoints this would be a 2-segment U-loop. With them, single segment.
    const backwardParams = {
      sourceX: 500, sourceY: 0,
      targetX: 0, targetY: 100,
      sourcePosition: Position.Right,
      targetPosition: Position.Left,
    };
    const noOverride = getSmartEdgePath(backwardParams);
    expect(noOverride).toHaveLength(2);
    const withOverride = getSmartEdgePath({
      ...backwardParams,
      controlPoints: { cp1x: 250, cp1y: 200, cp2x: 250, cp2y: 200 },
    });
    expect(withOverride).toHaveLength(1);
    expect(withOverride[0][0]).toBe('M 500,0 C 250,200 250,200 0,100');
  });
});

describe('smartEdgePath — arrowhead allocation', () => {
  it('shortensCubicAtTheArrowBase_andKeepsArrowTipAtOriginalTarget', () => {
    const segs = getSmartEdgePath({
      sourceX: 0, sourceY: 0,
      targetX: 200, targetY: 0,
      sourcePosition: Position.Right,
      targetPosition: Position.Left,
      controlPoints: { cp1x: 50, cp1y: 0, cp2x: 150, cp2y: 0 },
      markerEndLength: 12,
    });

    const [path, , , arrow] = segs[0];
    expect(path).not.toContain('200,0');
    expect(arrow).toBeDefined();
    expect(arrow?.tipX).toBeCloseTo(200, 5);
    expect(arrow?.tipY).toBeCloseTo(0, 5);
    expect(arrow?.baseX).toBeCloseTo(188, 1);
    expect(arrow?.baseY).toBeCloseTo(0, 1);
    expect(arrow?.angle).toBeCloseTo(0, 5);
  });

  it('arrowPathStartsAtTip_andUsesBaseWidthAroundTheTrimPoint', () => {
    const segs = getSmartEdgePath({
      sourceX: 0, sourceY: 0,
      targetX: 200, targetY: 0,
      sourcePosition: Position.Right,
      targetPosition: Position.Left,
      controlPoints: { cp1x: 50, cp1y: -100, cp2x: 150, cp2y: -100 },
      markerEndLength: 12,
    });

    const arrow = segs[0][3];
    expect(arrow).toBeDefined();
    // The playful dart is a straight-sided 4-point polygon (tip, barb, notch, barb). Its core
    // geometry is inset by half the rounding stroke, so the path's first point sits slightly
    // BEHIND the true tip along the axis — the stroked (rounded) silhouette ends at the tip.
    const path = edgeArrowPath(arrow!);
    expect(path).toMatch(/^M [-\d.]+,[-\d.]+ L /);
    expect((path.match(/ L /g) ?? []).length).toBe(3); // 4 corners: tip + 2 barbs + back notch
    const [, mx, my] = /^M ([-\d.]+),([-\d.]+) /.exec(path)!;
    const insetDist = Math.hypot(Number(mx) - arrow!.tipX, Number(my) - arrow!.tipY);
    expect(insetDist).toBeCloseTo((12 * EDGE_ARROW_STROKE_FRAC) / 2, 1);
    expect(Math.hypot(arrow!.tipX - arrow!.baseX, arrow!.tipY - arrow!.baseY)).toBeCloseTo(12, 1);
  });
});

describe('defaultControlPoints — port-aware projection', () => {
  it('rightLeft_projectsHorizontally', () => {
    const cps = defaultControlPoints({
      sourceX: 0, sourceY: 0, sourcePosition: Position.Right,
      targetX: 200, targetY: 0, targetPosition: Position.Left,
    });
    // distance = 0.25 * hypot(200, 0) = 50
    expect(cps.cp1x).toBeCloseTo(50);
    expect(cps.cp1y).toBeCloseTo(0);
    expect(cps.cp2x).toBeCloseTo(150);
    expect(cps.cp2y).toBeCloseTo(0);
  });

  it('topBottom_projectsVertically', () => {
    // Source has Bottom port (flow goes down), target has Top port (flow comes from above).
    const cps = defaultControlPoints({
      sourceX: 100, sourceY: 0, sourcePosition: Position.Bottom,
      targetX: 100, targetY: 200, targetPosition: Position.Top,
    });
    // distance = 0.25 * hypot(0, 200) = 50
    expect(cps.cp1x).toBeCloseTo(100);
    expect(cps.cp1y).toBeCloseTo(50);
    expect(cps.cp2x).toBeCloseTo(100);
    expect(cps.cp2y).toBeCloseTo(150);
  });

  it('mixedPorts_eachEndpointProjectsOwnDirection', () => {
    // Right port → +x, Top port → -y (target's "outward" is upward, away from target node)
    const cps = defaultControlPoints({
      sourceX: 0, sourceY: 0, sourcePosition: Position.Right,
      targetX: 100, targetY: 100, targetPosition: Position.Top,
    });
    const distance = 0.25 * Math.hypot(100, 100);
    expect(cps.cp1x).toBeCloseTo(distance);
    expect(cps.cp1y).toBeCloseTo(0);
    expect(cps.cp2x).toBeCloseTo(100);
    expect(cps.cp2y).toBeCloseTo(100 - distance);
  });

  it('leftPort_projectsNegativeX', () => {
    const cps = defaultControlPoints({
      sourceX: 100, sourceY: 0, sourcePosition: Position.Left,
      targetX: 0, targetY: 0, targetPosition: Position.Right,
    });
    // distance = 0.25 * 100 = 25; source projects -x, target projects +x.
    expect(cps.cp1x).toBeCloseTo(75);
    expect(cps.cp2x).toBeCloseTo(25);
  });
});
