import { useDesignStore, EDGE_WIDTHS } from '../../../stores/designStore';

// Arrowhead fills ride the semantic status tokens (dark-aware, skin-stable); `custom`
// uses the dedicated custom-orange token so stroke, arrowhead and label pill share one colour.
const MARKERS = [
  { id: 'np-arrow-default', fill: 'var(--color-outline-variant)' },
  { id: 'np-arrow-success', fill: 'var(--color-success)' },
  { id: 'np-arrow-failed',  fill: 'var(--color-error)' },
  { id: 'np-arrow-custom',  fill: 'var(--color-custom)' },
] as const;

export function NpEdgeMarkerDefs() {
  const edgeWidth = EDGE_WIDTHS[useDesignStore((s) => s.edgeWidthIndex)];
  // Scale marker size with sqrt(edgeWidth) so the arrowhead grows proportionally
  // to the perceived edge (stroke + glow), not just the raw stroke width.
  // At default 2.5px: ~13px arrowhead. At 7px: ~15px.
  const markerW = +(10 / Math.sqrt(edgeWidth)).toFixed(2);

  return (
    <svg
      style={{ position: 'absolute', width: 0, height: 0, overflow: 'hidden' }}
      aria-hidden
    >
      <defs>
        {MARKERS.map(({ id, fill }) => (
          <marker
            key={id}
            id={id}
            viewBox="0 0 10 10"
            refX="9"
            refY="5"
            markerWidth={markerW}
            markerHeight={markerW}
            orient="auto-start-reverse"
          >
            <path d="M0,1 L10,5 L0,9 Z" fill={fill} />
          </marker>
        ))}
      </defs>
    </svg>
  );
}
