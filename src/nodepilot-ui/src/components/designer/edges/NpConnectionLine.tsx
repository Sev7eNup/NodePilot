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
 * Das gemeinsame Aussehen jeder "noch nicht festgemachten" Verbindung: primary-farbener
 * animierter Strich + Docking-Punkt am losen Ende, passend zur committeten LabeledEdge.
 * Farben reiten auf `--color-primary`, das der `.np-designer .react-flow`-Schild über alle
 * Skins stabil hält. Die Dash-Animation steht in index.css (reduced-motion-gegated).
 *
 * Zwei Aufrufer: der Drag einer neuen Edge (NpConnectionLine unten) und das per Kontextmenü
 * gelöste Edge-Ende (EdgeDetachPreview). Beide Wege müssen gleich aussehen — es ist für den
 * Nutzer dieselbe Handlung.
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
