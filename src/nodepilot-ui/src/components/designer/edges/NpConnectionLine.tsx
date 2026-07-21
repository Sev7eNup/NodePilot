import { getBezierPath, type ConnectionLineComponentProps } from '@xyflow/react';

/**
 * Custom connection line drawn while the user drags a new edge. Replaces the
 * stock grey bezier with a primary-colored animated dash + a docking dot at the
 * cursor, so the preview matches the committed LabeledEdge look. Colors ride
 * `--color-primary`, which the `.np-designer .react-flow` shield keeps stable
 * across skins. The dash animation is defined in index.css (reduced-motion-gated).
 */
export function NpConnectionLine({ fromX, fromY, toX, toY, fromPosition, toPosition }: ConnectionLineComponentProps) {
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
