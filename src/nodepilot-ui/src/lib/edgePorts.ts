import { Position, type Edge } from '@xyflow/react';
import i18n from '../i18n';

export type EdgePortSide = 'top' | 'right' | 'bottom' | 'left';

export const EDGE_PORT_SIDES: EdgePortSide[] = ['top', 'right', 'bottom', 'left'];
export const DEFAULT_SOURCE_PORT: EdgePortSide = 'right';
export const DEFAULT_TARGET_PORT: EdgePortSide = 'left';

/** Localized port labels. Resolved at access time via a getter so a language switch
 *  takes effect without a reload (mirrors the triggerBadgeMeta pattern). */
export const EDGE_PORT_LABELS: Record<EdgePortSide, string> = {
  get top() { return i18n.t('designer:edgePort.top'); },
  get right() { return i18n.t('designer:edgePort.right'); },
  get bottom() { return i18n.t('designer:edgePort.bottom'); },
  get left() { return i18n.t('designer:edgePort.left'); },
};

export function isEdgePortSide(value: unknown): value is EdgePortSide {
  return value === 'top' || value === 'right' || value === 'bottom' || value === 'left';
}

export function normalizePort(value: unknown, fallback: EdgePortSide): EdgePortSide {
  return isEdgePortSide(value) ? value : fallback;
}

export function edgeSourcePort(edge: Pick<Edge, 'sourceHandle'>): EdgePortSide {
  return normalizePort(edge.sourceHandle, DEFAULT_SOURCE_PORT);
}

export function edgeTargetPort(edge: Pick<Edge, 'targetHandle'>): EdgePortSide {
  return normalizePort(edge.targetHandle, DEFAULT_TARGET_PORT);
}

export function withDefaultEdgePorts(edge: Edge): Edge {
  const sourceHandle = edgeSourcePort(edge);
  const targetHandle = edgeTargetPort(edge);
  return edge.sourceHandle === sourceHandle && edge.targetHandle === targetHandle
    ? edge
    : { ...edge, sourceHandle, targetHandle };
}

export function oppositePort(side: EdgePortSide): EdgePortSide {
  switch (side) {
    case 'top': return 'bottom';
    case 'right': return 'left';
    case 'bottom': return 'top';
    case 'left': return 'right';
  }
}

export function portToPosition(side: EdgePortSide): Position {
  switch (side) {
    case 'top': return Position.Top;
    case 'right': return Position.Right;
    case 'bottom': return Position.Bottom;
    case 'left': return Position.Left;
  }
}

export function getPortPoint(
  bounds: { x: number; y: number; w: number; h: number },
  side: EdgePortSide,
): { x: number; y: number } {
  switch (side) {
    case 'top': return { x: bounds.x + bounds.w / 2, y: bounds.y };
    case 'right': return { x: bounds.x + bounds.w, y: bounds.y + bounds.h / 2 };
    case 'bottom': return { x: bounds.x + bounds.w / 2, y: bounds.y + bounds.h };
    case 'left': return { x: bounds.x, y: bounds.y + bounds.h / 2 };
  }
}
