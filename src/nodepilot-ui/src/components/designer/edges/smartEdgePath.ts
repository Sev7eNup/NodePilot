import { getSmoothStepPath, Position } from '@xyflow/react';
import type { EdgeRouting } from '../../../stores/designStore';

export interface ControlPoints {
  cp1x: number; cp1y: number;
  cp2x: number; cp2y: number;
}

export interface SmartEdgePathParams {
  sourceX: number; sourceY: number;
  targetX: number; targetY: number;
  sourcePosition: Position; targetPosition: Position;
  routing?: EdgeRouting;
  controlPoints?: ControlPoints;
  markerEndLength?: number;
}

export interface EdgeArrowGeometry {
  tipX: number;
  tipY: number;
  baseX: number;
  baseY: number;
  angle: number;
  width: number;
}

export type EdgeSegment = [path: string, labelX: number, labelY: number, arrow?: EdgeArrowGeometry];

const BACKWARD_THRESHOLD  = 60;   // kept wider than n8n's 20px for NodePilot's handle geometry
const EDGE_PADDING_BOTTOM = 130;  // n8n: EDGE_PADDING_BOTTOM — how far below nodes the U-loop descends
const EDGE_BORDER_RADIUS  = 16;   // n8n: EDGE_BORDER_RADIUS
const EDGE_OFFSET         = 40;   // n8n: EDGE_PADDING_X — smoothstep lateral offset
const MIN_PATH_BEFORE_ARROW = 6;
const CUBIC_LENGTH_STEPS = 28;

interface Point {
  x: number;
  y: number;
}

function calculateControlOffset(distance: number, curvature: number): number {
  if (distance >= 0) return 0.5 * distance;
  return curvature * 25 * Math.sqrt(-distance);
}

function controlWithCurvature({
  pos, x1, y1, x2, y2, curvature,
}: {
  pos: Position;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  curvature: number;
}): Point {
  switch (pos) {
    case Position.Left:
      return { x: x1 - calculateControlOffset(x1 - x2, curvature), y: y1 };
    case Position.Right:
      return { x: x1 + calculateControlOffset(x2 - x1, curvature), y: y1 };
    case Position.Top:
      return { x: x1, y: y1 - calculateControlOffset(y1 - y2, curvature) };
    case Position.Bottom:
      return { x: x1, y: y1 + calculateControlOffset(y2 - y1, curvature) };
    default:
      return { x: x1, y: y1 };
  }
}

function cubicPoint(p0: Point, p1: Point, p2: Point, p3: Point, t: number): Point {
  const mt = 1 - t;
  const mt2 = mt * mt;
  const t2 = t * t;
  return {
    x: mt2 * mt * p0.x + 3 * mt2 * t * p1.x + 3 * mt * t2 * p2.x + t2 * t * p3.x,
    y: mt2 * mt * p0.y + 3 * mt2 * t * p1.y + 3 * mt * t2 * p2.y + t2 * t * p3.y,
  };
}

function lerpPoint(a: Point, b: Point, t: number): Point {
  return { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t };
}

function splitCubicLeft(p0: Point, p1: Point, p2: Point, p3: Point, t: number): [Point, Point, Point, Point] {
  const p01 = lerpPoint(p0, p1, t);
  const p12 = lerpPoint(p1, p2, t);
  const p23 = lerpPoint(p2, p3, t);
  const p012 = lerpPoint(p01, p12, t);
  const p123 = lerpPoint(p12, p23, t);
  const p0123 = lerpPoint(p012, p123, t);
  return [p0, p01, p012, p0123];
}

function cubicLength(p0: Point, p1: Point, p2: Point, p3: Point, fromT = 0, toT = 1): number {
  let length = 0;
  let prev = cubicPoint(p0, p1, p2, p3, fromT);
  for (let i = 1; i <= CUBIC_LENGTH_STEPS; i++) {
    const t = fromT + ((toT - fromT) * i) / CUBIC_LENGTH_STEPS;
    const cur = cubicPoint(p0, p1, p2, p3, t);
    length += Math.hypot(cur.x - prev.x, cur.y - prev.y);
    prev = cur;
  }
  return length;
}

function findCubicTrimT(p0: Point, p1: Point, p2: Point, p3: Point, markerLength: number): number | null {
  const totalLength = cubicLength(p0, p1, p2, p3);
  if (totalLength <= markerLength + MIN_PATH_BEFORE_ARROW) return null;

  let lo = 0;
  let hi = 1;
  for (let i = 0; i < 24; i++) {
    const mid = (lo + hi) / 2;
    const remaining = cubicLength(p0, p1, p2, p3, mid, 1);
    if (remaining > markerLength) lo = mid;
    else hi = mid;
  }
  return hi;
}

function cubicLabelPoint(p0: Point, p1: Point, p2: Point, p3: Point): Point {
  return cubicPoint(p0, p1, p2, p3, 0.5);
}

function arrowFromBaseAndTip(base: Point, tip: Point, markerLength: number): EdgeArrowGeometry | undefined {
  const dx = tip.x - base.x;
  const dy = tip.y - base.y;
  if (Math.hypot(dx, dy) < 1) return undefined;
  return {
    tipX: tip.x,
    tipY: tip.y,
    baseX: base.x,
    baseY: base.y,
    angle: Math.atan2(dy, dx),
    width: markerLength,
  };
}

function pointBehindTarget(target: Point, targetPosition: Position, markerLength: number): Point {
  switch (targetPosition) {
    case Position.Left:
      return { x: target.x - markerLength, y: target.y };
    case Position.Right:
      return { x: target.x + markerLength, y: target.y };
    case Position.Top:
      return { x: target.x, y: target.y - markerLength };
    case Position.Bottom:
      return { x: target.x, y: target.y + markerLength };
    default:
      return target;
  }
}

function cubicSegment(
  p0: Point,
  p1: Point,
  p2: Point,
  p3: Point,
  markerEndLength?: number,
): EdgeSegment {
  const label = cubicLabelPoint(p0, p1, p2, p3);
  if (!markerEndLength) {
    return [`M ${p0.x},${p0.y} C ${p1.x},${p1.y} ${p2.x},${p2.y} ${p3.x},${p3.y}`, label.x, label.y];
  }

  const trimT = findCubicTrimT(p0, p1, p2, p3, markerEndLength);
  if (trimT == null) {
    return [`M ${p0.x},${p0.y} C ${p1.x},${p1.y} ${p2.x},${p2.y} ${p3.x},${p3.y}`, label.x, label.y];
  }

  const [s0, s1, s2, base] = splitCubicLeft(p0, p1, p2, p3, trimT);
  const arrow = arrowFromBaseAndTip(base, p3, markerEndLength);
  return [`M ${s0.x},${s0.y} C ${s1.x},${s1.y} ${s2.x},${s2.y} ${base.x},${base.y}`, label.x, label.y, arrow];
}

function smoothStepSegment({
  sourceX,
  sourceY,
  sourcePosition,
  targetX,
  targetY,
  targetPosition,
  borderRadius,
  offset,
  markerEndLength,
}: {
  sourceX: number;
  sourceY: number;
  sourcePosition: Position;
  targetX: number;
  targetY: number;
  targetPosition: Position;
  borderRadius: number;
  offset?: number;
  markerEndLength?: number;
}): EdgeSegment {
  const target = { x: targetX, y: targetY };
  const base = markerEndLength ? pointBehindTarget(target, targetPosition, markerEndLength) : target;
  const useArrow = markerEndLength && Math.hypot(base.x - sourceX, base.y - sourceY) > MIN_PATH_BEFORE_ARROW;
  const renderedTarget = useArrow ? base : target;
  const [path, labelX, labelY] = getSmoothStepPath({
    sourceX,
    sourceY,
    sourcePosition,
    targetX: renderedTarget.x,
    targetY: renderedTarget.y,
    targetPosition,
    borderRadius,
    offset,
  });
  const arrow = useArrow ? arrowFromBaseAndTip(renderedTarget, target, markerEndLength) : undefined;
  return [path, labelX, labelY, arrow];
}

/**
 * Stroke width applied on top of the arrowhead fill (as a fraction of the arrow length).
 * LabeledEdge lays a same-color stroke with round joins over the dart — that is what rounds
 * the tip and barb corners into the soft, playful reference look. Kept here next to the
 * geometry because {@link edgeArrowPath} insets the core shape by half this stroke so the
 * STROKED silhouette still ends exactly at the tip (the node port) at the intended size.
 */
export const EDGE_ARROW_STROKE_FRAC = 0.25;

export function edgeArrowPath(arrow: EdgeArrowGeometry): string {
  // Playful reference arrowhead: a solid dart — straight sides, swept-back barbs and a
  // concave back notch (paper-plane silhouette). Corners are rounded by the same-color
  // stroke LabeledEdge applies (EDGE_ARROW_STROKE_FRAC, round joins). Everything is
  // proportional to the arrow's length, so the shape stays identical as the head scales
  // with the edge width.
  const { tipX, tipY, baseX, baseY, angle, width } = arrow;
  const ax = Math.cos(angle);            // unit axis, base → tip
  const ay = Math.sin(angle);
  const px = -ay;                        // unit perpendicular
  const py = ax;
  const length = Math.hypot(tipX - baseX, tipY - baseY) || width;

  // Inset the core geometry by half the stroke on every side, so the stroked outline
  // matches the intended length/width and the rounded tip lands exactly on the port.
  const inset = (length * EDGE_ARROW_STROKE_FRAC) / 2;
  const tX = tipX - ax * inset;
  const tY = tipY - ay * inset;
  const bX = baseX + ax * inset;
  const bY = baseY + ay * inset;
  const coreLength = Math.max(1, length - inset * 2);
  const halfWidth = Math.max(1, (width - inset * 2) / 2);

  const leftX = bX + px * halfWidth;
  const leftY = bY + py * halfWidth;
  const rightX = bX - px * halfWidth;
  const rightY = bY - py * halfWidth;
  // Concave back: the base centre pulled ~25% toward the tip → gently swept barbs (solid,
  // not deeply forked — matches the reference).
  const notchX = bX + ax * coreLength * 0.25;
  const notchY = bY + ay * coreLength * 0.25;

  return `M ${tX},${tY} L ${leftX},${leftY} L ${notchX},${notchY} L ${rightX},${rightY} Z`;
}

/**
 * Default control points when an edge has no user-defined shape, projected outward
 * from each port direction so Top/Bottom-port flows get plausible defaults too.
 * For Right→Left edges this is identical to the trivial 0.25*dx offset.
 */
export function defaultControlPoints({
  sourceX, sourceY, sourcePosition,
  targetX, targetY, targetPosition,
}: {
  sourceX: number; sourceY: number; sourcePosition: Position;
  targetX: number; targetY: number; targetPosition: Position;
}): ControlPoints {
  const distance = 0.25 * Math.hypot(targetX - sourceX, targetY - sourceY);
  const project = (x: number, y: number, pos: Position) => {
    switch (pos) {
      case Position.Right:  return { x: x + distance, y };
      case Position.Left:   return { x: x - distance, y };
      case Position.Bottom: return { x, y: y + distance };
      case Position.Top:    return { x, y: y - distance };
      default:              return { x, y };
    }
  };
  const cp1 = project(sourceX, sourceY, sourcePosition);
  const cp2 = project(targetX, targetY, targetPosition);
  return { cp1x: cp1.x, cp1y: cp1.y, cp2x: cp2.x, cp2y: cp2.y };
}

export function getSmartEdgePath({
  sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, routing = 'smart',
  controlPoints, markerEndLength,
}: SmartEdgePathParams): EdgeSegment[] {
  // User-defined cubic Bezier overrides all auto-routing. Comes BEFORE the backward-edge
  // U-loop branch — once the user has shaped an edge by hand, we never silently re-route it.
  if (controlPoints) {
    const { cp1x, cp1y, cp2x, cp2y } = controlPoints;
    return [cubicSegment(
      { x: sourceX, y: sourceY },
      { x: cp1x, y: cp1y },
      { x: cp2x, y: cp2y },
      { x: targetX, y: targetY },
      markerEndLength,
    )];
  }

  const dx = targetX - sourceX;
  const dy = targetY - sourceY;

  if (Math.hypot(dx, dy) < 10) {
    return [[`M ${sourceX},${sourceY} L ${targetX},${targetY}`, (sourceX + targetX) / 2, (sourceY + targetY) / 2]];
  }

  // Backward edge: route below both nodes in a U-shape using two smoothstep segments.
  // Matches n8n's approach (getEdgeRenderData.ts). Always routes below regardless of
  // which node is higher — avoids overlapping node content from the side.
  if (sourcePosition === Position.Right && targetPosition === Position.Left && dx < -BACKWARD_THRESHOLD) {
    const midX = (sourceX + targetX) / 2;
    const loopY = Math.max(sourceY, targetY) + EDGE_PADDING_BOTTOM;

    const [seg1] = getSmoothStepPath({
      sourceX,
      sourceY,
      sourcePosition,
      targetX: midX,
      targetY: loopY,
      targetPosition: Position.Right,
      borderRadius: EDGE_BORDER_RADIUS,
      offset: EDGE_OFFSET,
    });

    const [seg2, , , arrow] = smoothStepSegment({
      sourceX: midX,
      sourceY: loopY,
      sourcePosition: Position.Left,
      targetX,
      targetY,
      targetPosition,
      borderRadius: EDGE_BORDER_RADIUS,
      offset: EDGE_OFFSET,
      markerEndLength,
    });

    return [
      [seg1, midX, loopY],
      [seg2, midX, loopY, arrow],
    ];
  }

  if (routing === 'straight') {
    return [smoothStepSegment({
      sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition,
      borderRadius: 8,
      markerEndLength,
    })];
  }

  if (routing === 'curved') {
    // Always a pronounced arc regardless of node layout.
    const p1 = controlWithCurvature({
      pos: sourcePosition, x1: sourceX, y1: sourceY, x2: targetX, y2: targetY, curvature: 0.5,
    });
    const p2 = controlWithCurvature({
      pos: targetPosition, x1: targetX, y1: targetY, x2: sourceX, y2: sourceY, curvature: 0.5,
    });
    return [cubicSegment({ x: sourceX, y: sourceY }, p1, p2, { x: targetX, y: targetY }, markerEndLength)];
  }

  // smart (Auto): fixed curvature 0.25 — identical to n8n's getBezierPath default.
  const p1 = controlWithCurvature({
    pos: sourcePosition, x1: sourceX, y1: sourceY, x2: targetX, y2: targetY, curvature: 0.25,
  });
  const p2 = controlWithCurvature({
    pos: targetPosition, x1: targetX, y1: targetY, x2: sourceX, y2: sourceY, curvature: 0.25,
  });
  return [cubicSegment({ x: sourceX, y: sourceY }, p1, p2, { x: targetX, y: targetY }, markerEndLength)];
}
