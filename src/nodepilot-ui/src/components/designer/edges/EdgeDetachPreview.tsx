import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import { ViewportPortal, useInternalNode, useReactFlow, type XYPosition } from '@xyflow/react';
import { ConnectionPreviewPath } from './NpConnectionLine';
import { detachedSourcePoint, resolveDockTarget } from '../../../lib/edgeDetach';
import { portToPosition, type EdgePortSide } from '../../../lib/edgePorts';

interface Props {
  sourceNodeId: string;
  sourcePort: EdgePortSide;
  /** Port side of the existing edge — fallback while the cursor is over no node. */
  targetPort: EdgePortSide;
  canvasRef: RefObject<HTMLElement | null>;
  /** Whether this node can be docked to (excludes source node, duplicates, groups/notes). */
  canDockTo: (nodeId: string) => boolean;
  /** Reports the node the line is currently docking to, for the hover ring on the canvas. */
  onDockTargetChange: (nodeId: string | null) => void;
}

/**
 * Preview line for an edge target detached via the context menu. Runs from the source port
 * to the cursor and docks at the nearest port over a valid target node, using the same
 * resolveDockTarget the click later uses to create the real edge. Rendered inside
 * ViewportPortal (flow coordinates) so it stays anchored while panning and zooming, with its
 * own throttled pointermove listener since it only mounts during an active detach.
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

  // Reports a node change upward and swallows repeats. The ref is read and written
  // only inside this callback, never during render.
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

  // Clears the dock ring at the end of a detach, as its own effect that only runs on
  // unmount (deps are stable) — doing it in the listener effect's cleanup above would
  // reset the dock node on every re-attach, since that effect depends on canDockTo, whose
  // identity changes on every graph edit. The current callback is read via a ref that is
  // only written and read inside effects, per the React Compiler rules.
  const notifyRef = useRef(onDockTargetChange);
  useEffect(() => { notifyRef.current = onDockTargetChange; }, [onDockTargetChange]);
  useEffect(() => () => publishDockNode(null, (id) => notifyRef.current(id)), [publishDockNode]);

  const from = detachedSourcePoint(sourceNode, sourcePort);
  // Nothing to draw until the cursor has moved at least once.
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
