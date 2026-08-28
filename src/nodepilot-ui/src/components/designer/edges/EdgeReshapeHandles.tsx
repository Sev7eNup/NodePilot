import { useContext, useEffect, useRef, useState } from 'react';
import { EdgeLabelRenderer, Position, useReactFlow } from '@xyflow/react';
import { defaultControlPoints, type ControlPoints } from './smartEdgePath';
import { useDesignStore } from '../../../stores/designStore';
import { EdgeEditingContext } from './edgeEditingContext';

interface Props {
  edgeId: string;
  sourceX: number;
  sourceY: number;
  sourcePosition: Position;
  targetX: number;
  targetY: number;
  targetPosition: Position;
  controlPoints: ControlPoints | undefined;
  visible: boolean;
}

type HandleId = 'cp1' | 'cp2';

interface DragState {
  handle: HandleId;
  startScreen: { x: number; y: number };
  startCp: ControlPoints;
}

const HANDLE_RADIUS = 8;

/**
 * Pen-tool style cubic Bezier control handles for an edge: two SVG circles (control points)
 * plus two dashed guide lines from each port to its handle, visible on hover/select via the
 * parent edge component. pointerdown commits history once via beginEdgeReshape, then
 * pointermove streams live updates via updateEdgeShape with no history/dirty per tick. All
 * coordinates are flow space; skipped for backward edges and read-only views (see LabeledEdge).
 */
export function EdgeReshapeHandles({
  edgeId,
  sourceX, sourceY, sourcePosition,
  targetX, targetY, targetPosition,
  controlPoints,
  visible,
}: Readonly<Props>) {
  const { canWrite, beginEdgeReshape, updateEdgeShape } = useContext(EdgeEditingContext);
  const { screenToFlowPosition } = useReactFlow();
  const snapToGrid = useDesignStore((s) => s.snapToGrid);
  const snapGridSize = useDesignStore((s) => s.snapGridSize);

  const dragStateRef = useRef<DragState | null>(null);
  const [draggingHandle, setDraggingHandle] = useState<HandleId | null>(null);

  // Effective control points: user-defined override, otherwise port-aware default.
  // Defaults are not persisted — controlPoints only appears in edge.data once the user
  // actually drags a handle. Until then the handles just visualize the auto-curve's shape.
  const effective: ControlPoints = controlPoints ?? defaultControlPoints({
    sourceX, sourceY, sourcePosition,
    targetX, targetY, targetPosition,
  });

  // Stable ref for the in-flight drag callbacks, so pointermove does not depend on
  // `effective` (it would change every render and churn the listener).
  const latestRef = useRef({
    edgeId, screenToFlowPosition, snapToGrid, snapGridSize, updateEdgeShape, effective,
  });
  // The mutation has to happen inside an effect — writing to a ref during render violates
  // the react-hooks/refs rule and could end up inconsistent if React discards the render.
  useEffect(() => {
    latestRef.current = {
      edgeId, screenToFlowPosition, snapToGrid, snapGridSize, updateEdgeShape, effective,
    };
  });

  useEffect(() => {
    if (!draggingHandle) return;
    const handlePointerMove = (e: PointerEvent) => {
      const drag = dragStateRef.current;
      if (!drag) return;
      const { screenToFlowPosition: stf, snapToGrid: snap, snapGridSize: gs, updateEdgeShape: upd, edgeId: id } = latestRef.current;
      const startFlow = stf({ x: drag.startScreen.x, y: drag.startScreen.y });
      const curFlow = stf({ x: e.clientX, y: e.clientY });
      const dxFlow = curFlow.x - startFlow.x;
      const dyFlow = curFlow.y - startFlow.y;
      const round = (v: number) => snap ? Math.round(v / gs) * gs : v;
      const next: ControlPoints = drag.handle === 'cp1'
        ? {
            cp1x: round(drag.startCp.cp1x + dxFlow),
            cp1y: round(drag.startCp.cp1y + dyFlow),
            cp2x: drag.startCp.cp2x,
            cp2y: drag.startCp.cp2y,
          }
        : {
            cp1x: drag.startCp.cp1x,
            cp1y: drag.startCp.cp1y,
            cp2x: round(drag.startCp.cp2x + dxFlow),
            cp2y: round(drag.startCp.cp2y + dyFlow),
          };
      upd(id, next);
    };
    const handlePointerUp = () => {
      dragStateRef.current = null;
      setDraggingHandle(null);
    };
    globalThis.addEventListener('pointermove', handlePointerMove);
    globalThis.addEventListener('pointerup', handlePointerUp);
    globalThis.addEventListener('pointercancel', handlePointerUp);
    return () => {
      globalThis.removeEventListener('pointermove', handlePointerMove);
      globalThis.removeEventListener('pointerup', handlePointerUp);
      globalThis.removeEventListener('pointercancel', handlePointerUp);
    };
  }, [draggingHandle]);

  // Takes the handle as a plain argument instead of being curried. A curried
  // `(handle) => (e) => ...` form has to be called during render to produce the prop, which
  // puts the ref write below on a render-reachable path as far as the React Compiler is
  // concerned (react-hooks/refs).
  const onHandlePointerDown = (handle: HandleId, e: React.PointerEvent<HTMLDivElement>) => {
    if (!canWrite) return;
    e.stopPropagation();
    e.preventDefault();
    // currentTarget instead of target: stays pinned to the handle div even if a child element
    // (icon/tooltip) later gets added inside it. Otherwise the first pointermove could drag
    // the pointer out of the inner element and pointer capture would be lost.
    e.currentTarget.setPointerCapture?.(e.pointerId);
    dragStateRef.current = {
      handle,
      startScreen: { x: e.clientX, y: e.clientY },
      // Snapshot the effective points (including defaults) so the first drag on a
      // never-shaped edge starts from the visible handle position, not from (0,0).
      startCp: { ...effective },
    };
    beginEdgeReshape(edgeId);
    setDraggingHandle(handle);
  };

  if (!visible || !canWrite) return null;

  const stroke = 'var(--color-outline-variant)';
  const cursor = draggingHandle ? 'grabbing' : 'grab';

  return (
    <>
      {/* The dashed connector lines stay in the SVG edge layer (z-index 1) — they're part
          of the edge geometry and don't need any interaction. pointerEvents: none on the
          group so the hover logic of the edge underneath isn't blocked by this hint overlay. */}
      <g
        className="nodrag nopan"
        data-testid={`edge-reshape-lines-${edgeId}`}
        style={{ pointerEvents: 'none' }}
      >
        <line
          x1={sourceX} y1={sourceY} x2={effective.cp1x} y2={effective.cp1y}
          stroke={stroke} strokeWidth={1} strokeDasharray="3,3"
        />
        <line
          x1={targetX} y1={targetY} x2={effective.cp2x} y2={effective.cp2y}
          stroke={stroke} strokeWidth={1} strokeDasharray="3,3"
        />
      </g>
      {/* The handle "circles" are HTML divs rendered into the EdgeLabelRenderer layer
          (z-index 4 via CSS). The inline zIndex 10 beats the sibling +-button div in that
          same layer, so cp2 doesn't disappear behind the button when they overlap spatially
          (e.g. Right→Bottom edges, where cp2 sits projected 25% down from the target —
          landing right on the edge's midpoint, which is also where the +-button renders). */}
      <EdgeLabelRenderer>
        <ReshapeHandleDiv
          testId={`edge-reshape-cp1-${edgeId}`}
          x={effective.cp1x} y={effective.cp1y}
          cursor={cursor}
          onPointerDown={(e) => onHandlePointerDown('cp1', e)}
        />
        <ReshapeHandleDiv
          testId={`edge-reshape-cp2-${edgeId}`}
          x={effective.cp2x} y={effective.cp2y}
          cursor={cursor}
          onPointerDown={(e) => onHandlePointerDown('cp2', e)}
        />
      </EdgeLabelRenderer>
    </>
  );
}

function ReshapeHandleDiv({ testId, x, y, cursor, onPointerDown }: Readonly<{
  testId: string;
  x: number;
  y: number;
  cursor: string;
  onPointerDown: (e: React.PointerEvent<HTMLDivElement>) => void;
}>) {
  return (
    <div
      data-testid={testId}
      className="nodrag nopan"
      onPointerDown={onPointerDown}
      style={{
        // position: absolute set inline (not just via a Tailwind class): zIndex only affects
        // positioned elements, and we don't want the layering to depend on CSS load order.
        position: 'absolute',
        transform: `translate(-50%, -50%) translate(${x}px, ${y}px)`,
        width: HANDLE_RADIUS * 2,
        height: HANDLE_RADIUS * 2,
        borderRadius: '50%',
        // Using cyan-tinted sky-400 instead of the brand primary color: a selected edge
        // already uses primary blue (#004ac6), and both Material primary tones (including
        // the -container variant #2563eb) sit visually too close to it. sky-400 has a
        // clearly turquoise cast and stands out reliably in both light and dark mode.
        background: '#38bdf8',
        border: '2.5px solid white',
        boxShadow: '0 1px 3px rgba(0,0,0,0.45)',
        cursor,
        pointerEvents: 'all',
        zIndex: 10,
      }}
    />
  );
}
