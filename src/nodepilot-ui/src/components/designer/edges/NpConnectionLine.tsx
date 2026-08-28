import { getBezierPath, type Position, type ConnectionLineComponentProps } from '@xyflow/react';

interface PreviewPathProps {
  fromX: number;
  fromY: number;
  toX: number;
  toY: number;
  fromPosition: Position;
  toPosition: Position;
}

/**
 * Shared look for a "not yet attached" connection: an animated primary-colored stroke
 * plus a docking dot at the loose end, matching the committed LabeledEdge. Colors ride
 * `--color-primary`; the dash animation lives in index.css (reduced-motion gated).
 *
 * Used both when dragging a new edge (NpConnectionLine below) and when detaching an
 * edge end via the context menu (EdgeDetachPreview) - both must look identical since
 * they represent the same action to the user.
 */
export function ConnectionPreviewPath({ fromX, fromY, toX, toY, fromPosition, toPosition }: Readonly<PreviewPathProps>) {
  const [path] = getBezierPath({
    sourceX: fromX,
    sourceY: fromY,
    sourcePosition: fromPosition,
    targetX: toX,
    targetY: toY,
    targetPosition: toPosition,
    curvature: 0.25,
  });
  return (
    <g className="np-connection-line" pointerEvents="none">
      <path
        d={path}
        fill="none"
        stroke="var(--color-primary)"
        strokeWidth={2}
        strokeLinecap="round"
        strokeDasharray="6 4"
        className="np-connection-dash"
      />
      <circle
        cx={toX}
        cy={toY}
        r={4}
        fill="var(--color-surface-lowest)"
        stroke="var(--color-primary)"
        strokeWidth={2}
      />
    </g>
  );
}

/** Custom connection line drawn while the user drags a new edge. */
export function NpConnectionLine({ fromX, fromY, toX, toY, fromPosition, toPosition }: ConnectionLineComponentProps) {
  return (
    <ConnectionPreviewPath
      fromX={fromX}
      fromY={fromY}
      toX={toX}
      toY={toY}
      fromPosition={fromPosition}
      toPosition={toPosition}
    />
  );
}
