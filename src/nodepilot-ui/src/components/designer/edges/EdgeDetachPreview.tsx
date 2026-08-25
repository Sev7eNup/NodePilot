import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import { ViewportPortal, useInternalNode, useReactFlow, type XYPosition } from '@xyflow/react';
import { ConnectionPreviewPath } from './NpConnectionLine';
import { detachedSourcePoint, resolveDockTarget } from '../../../lib/edgeDetach';
import { portToPosition, type EdgePortSide } from '../../../lib/edgePorts';

interface Props {
  sourceNodeId: string;
  sourcePort: EdgePortSide;
  /** Port-Seite der noch bestehenden Edge — Fallback, solange der Cursor über keinem Node steht. */
  targetPort: EdgePortSide;
  canvasRef: RefObject<HTMLElement | null>;
  /** Darf an diesen Node angedockt werden? Filtert Quell-Node, Duplikate, Gruppen/Sticky-Notes. */
  canDockTo: (nodeId: string) => boolean;
  /** Meldet den Node, an dem die Linie gerade andockt — für den Hover-Ring auf der Canvas. */
  onDockTargetChange: (nodeId: string | null) => void;
}

/**
 * Vorschau-Linie für ein per Kontextmenü gelöstes Edge-Ziel: läuft vom Quell-Port zum Cursor
 * und **dockt am nächstgelegenen Port an**, sobald der Cursor über einem gültigen Ziel-Node
 * steht. Was hier zu sehen ist, erzeugt der Klick anschließend genau so — beide Seiten fragen
 * dasselbe `resolveDockTarget`.
 *
 * Gerendert in `<ViewportPortal>`, also in FLOW-Koordinaten — nur so bleibt die Linie beim
 * Pannen und Zoomen an ihrem Anker kleben. Ein Overlay in Screen-Koordinaten würde ohne
 * Mausbewegung veralten.
 *
 * Die Komponente hängt ihren eigenen `pointermove` an die Canvas (rAF-gedrosselt, Muster wie
 * EdgeReshapeHandles) statt den geteilten `pointerFlowPositionStore` mitzubenutzen: der ist
 * bewusst an `autoHidePorts` gekoppelt und liefert sonst gar nichts. Sie mountet nur während
 * eines aktiven Detach, kostet also im Normalbetrieb nichts.
 *
 * Zwei Taktungen, bewusst getrennt: die Cursor-Position bleibt als lokaler State hier (ändert
 * sich bei jeder Bewegung), der angedockte Node geht über `onDockTargetChange` nach oben —
 * aber NUR beim Wechsel. Sonst würde die ganze Editor-Seite pro Mausbewegung neu rendern.
 */
export function EdgeDetachPreview({
  sourceNodeId, sourcePort, targetPort, canvasRef, canDockTo, onDockTargetChange,
}: Readonly<Props>) {
  const { screenToFlowPosition } = useReactFlow();
  const sourceNode = useInternalNode(sourceNodeId);
  const [cursor, setCursor] = useState<XYPosition | null>(null);
  const [dock, setDock] = useState<{ port: EdgePortSide; point: XYPosition } | null>(null);
  const rafRef = useRef<number | null>(null);
  const lastDockNodeRef = useRef<string | null>(null);

  // Meldet einen Node-Wechsel nach oben und schluckt jede Wiederholung. Das Ref wird
  // ausschließlich innerhalb dieses Callbacks gelesen und geschrieben, nie im Render.
  const publishDockNode = useCallback((nodeId: string | null, notify: (id: string | null) => void) => {
    if (lastDockNodeRef.current === nodeId) return;
    lastDockNodeRef.current = nodeId;
    notify(nodeId);
  }, []);

  useEffect(() => {
    const el = canvasRef.current;
    if (!el) return;
    const onMove = (e: PointerEvent) => {
      const { clientX, clientY, target } = e;
      if (rafRef.current != null) return;
      rafRef.current = requestAnimationFrame(() => {
        rafRef.current = null;
        setCursor(screenToFlowPosition({ x: clientX, y: clientY }));
        const hit = resolveDockTarget(target, clientX, clientY, canDockTo);
        setDock(hit ? { port: hit.port, point: screenToFlowPosition(hit.screenPoint) } : null);
        publishDockNode(hit?.nodeId ?? null, onDockTargetChange);
      });
    };
    el.addEventListener('pointermove', onMove);
    return () => {
      el.removeEventListener('pointermove', onMove);
      if (rafRef.current != null) cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
    };
  }, [canvasRef, screenToFlowPosition, publishDockNode, canDockTo, onDockTargetChange]);

  // Ring beim Ende des Detach löschen — bewusst als EIGENER Effekt, der NUR beim Unmount
  // aufräumt (Deps sind stabil). Im Cleanup des Listener-Effekts oben wäre es falsch: der
  // hängt unter anderem an `canDockTo`, dessen Identität sich bei jeder Graph-Änderung
  // ändert, und würde den Dock-Node dann bei jedem Re-Attach kurz auf null zurücksetzen.
  // Der jeweils aktuelle Callback kommt über ein Ref, das ausschließlich in Effekten
  // beschrieben und gelesen wird — im Render wäre beides ein React-Compiler-Verstoß.
  const notifyRef = useRef(onDockTargetChange);
  useEffect(() => { notifyRef.current = onDockTargetChange; }, [onDockTargetChange]);
  useEffect(() => () => publishDockNode(null, (id) => notifyRef.current(id)), [publishDockNode]);

  const from = detachedSourcePoint(sourceNode, sourcePort);
  // Bis der Cursor sich das erste Mal bewegt hat, gibt es kein loses Ende zu zeichnen.
  if (!from || !cursor) return null;

  const to = dock?.point ?? cursor;
  const toSide = dock?.port ?? targetPort;

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
          toX={to.x}
          toY={to.y}
          fromPosition={portToPosition(sourcePort)}
          toPosition={portToPosition(toSide)}
        />
      </svg>
    </ViewportPortal>
  );
}
