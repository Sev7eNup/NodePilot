import { useEffect, useRef, useState, type RefObject } from 'react';
import { ViewportPortal, useInternalNode, useReactFlow, type XYPosition } from '@xyflow/react';
import { ConnectionPreviewPath } from './NpConnectionLine';
import { detachedSourcePoint } from '../../../lib/edgeDetach';
import { portToPosition, type EdgePortSide } from '../../../lib/edgePorts';

interface Props {
  sourceNodeId: string;
  sourcePort: EdgePortSide;
  targetPort: EdgePortSide;
  canvasRef: RefObject<HTMLElement | null>;
}

/**
 * Vorschau-Linie für ein per Kontextmenü gelöstes Edge-Ziel: läuft vom Quell-Port zum
 * Cursor, bis der Nutzer einen Node anklickt.
 *
 * Gerendert in `<ViewportPortal>`, also in FLOW-Koordinaten — nur so bleibt die Linie beim
 * Pannen und Zoomen an ihrem Anker kleben. Ein Overlay in Screen-Koordinaten würde ohne
 * Mausbewegung veralten.
 *
 * Die Komponente hängt ihren eigenen `pointermove` an die Canvas (rAF-gedrosselt, Muster wie
 * EdgeReshapeHandles) statt den geteilten `pointerFlowPositionStore` mitzubenutzen: der ist
 * bewusst an `autoHidePorts` gekoppelt und liefert sonst gar nichts. Sie mountet nur während
 * eines aktiven Detach, kostet also im Normalbetrieb nichts.
 */
export function EdgeDetachPreview({ sourceNodeId, sourcePort, targetPort, canvasRef }: Readonly<Props>) {
  const { screenToFlowPosition } = useReactFlow();
  const sourceNode = useInternalNode(sourceNodeId);
  const [cursor, setCursor] = useState<XYPosition | null>(null);
  const rafRef = useRef<number | null>(null);

  useEffect(() => {
    const el = canvasRef.current;
    if (!el) return;
    const onMove = (e: PointerEvent) => {
      const { clientX, clientY } = e;
      if (rafRef.current != null) return;
      rafRef.current = requestAnimationFrame(() => {
        rafRef.current = null;
        setCursor(screenToFlowPosition({ x: clientX, y: clientY }));
      });
    };
    el.addEventListener('pointermove', onMove);
    return () => {
      el.removeEventListener('pointermove', onMove);
      if (rafRef.current != null) cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
    };
  }, [canvasRef, screenToFlowPosition]);

  const from = detachedSourcePoint(sourceNode, sourcePort);
  // Bis der Cursor sich das erste Mal bewegt hat, gibt es kein loses Ende zu zeichnen.
  if (!from || !cursor) return null;

  return (
    <ViewportPortal>
      <svg
        data-testid="edge-detach-preview"
        style={{
          position: 'absolute',
          left: 0,
          top: 0,
          width: 1,
          height: 1,
          overflow: 'visible',
          pointerEvents: 'none',
          zIndex: 1001,
        }}
      >
        <ConnectionPreviewPath
          fromX={from.x}
          fromY={from.y}
          toX={cursor.x}
          toY={cursor.y}
          fromPosition={portToPosition(sourcePort)}
          toPosition={portToPosition(targetPort)}
        />
      </svg>
    </ViewportPortal>
  );
}
