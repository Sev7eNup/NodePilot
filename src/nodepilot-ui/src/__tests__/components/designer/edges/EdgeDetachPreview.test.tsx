import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, fireEvent, act } from '@testing-library/react';
import { createRef } from 'react';

/**
 * EdgeDetachPreview zeichnet die gestrichelte Linie vom Quell-Port zum Cursor, solange ein
 * Edge-Ziel per Kontextmenü gelöst ist.
 *
 * Mocks:
 *   - useReactFlow().screenToFlowPosition: Identität (der Test übergibt direkt Flow-Koordinaten)
 *   - useInternalNode: liefert wahlweise einen vermessenen Node oder undefined
 *   - ViewportPortal: schlichter Wrapper, damit der Inhalt im normalen DOM-Baum landet
 *
 * Gepinntes Verhalten:
 *   - vor der ersten Mausbewegung wird NICHTS gerendert (es gibt kein loses Ende zu zeigen)
 *   - nach einem pointermove auf der Canvas erscheint die Vorschau
 *   - der unvermessene Quell-Node unterdrückt die Vorschau (kein Anker auf (0,0))
 *   - über einem dockbaren Node endet die Linie auf dem HANDLE-Mittelpunkt, nicht am Cursor
 *   - ein nicht dockbarer Node lässt sie am Cursor
 *   - `onDockTargetChange` feuert nur beim WECHSEL des Nodes, nicht pro Mausbewegung
 *     (sonst würde die ganze Editor-Seite bei jedem Pixel neu rendern)
 *   - der pointermove-Listener wird beim Unmount wieder abgehängt
 */

const internalNode = vi.hoisted(() => ({
  current: undefined as undefined | { measured: { width?: number; height?: number }; internals: { positionAbsolute: { x: number; y: number } } },
}));

vi.mock('@xyflow/react', async () => {
  const actual = await vi.importActual<typeof import('@xyflow/react')>('@xyflow/react');
  return {
    ...actual,
    useReactFlow: () => ({ screenToFlowPosition: (p: { x: number; y: number }) => ({ x: p.x, y: p.y }) }),
    useInternalNode: () => internalNode.current,
    ViewportPortal: ({ children }: { children: React.ReactNode }) => <div data-testid="viewport-portal">{children}</div>,
  };
});

import { EdgeDetachPreview } from '../../../../components/designer/edges/EdgeDetachPreview';

/** requestAnimationFrame synchron ausführen — die Komponente drosselt den Move darüber. */
beforeEach(() => {
  internalNode.current = { measured: { width: 200, height: 80 }, internals: { positionAbsolute: { x: 100, y: 50 } } };
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => { cb(0); return 1; });
  vi.stubGlobal('cancelAnimationFrame', () => {});
});

/**
 * Ein `.react-flow__node` mit gestubbten Handle-Rects, das als Dock-Ziel unter dem Cursor
 * liegt. jsdom kennt kein Layout — `getBoundingClientRect` muss pro Handle gesetzt werden.
 *
 * Der Node hängt IN der Canvas: die Komponente lauscht auf der Canvas, und `event.target`
 * ist das Element, auf dem gefeuert wurde. Nur über echtes Bubbling aus dem Node heraus
 * entsteht dieselbe Ereignisform wie im Browser (`fireEvent`s `target`-Option überschreibt
 * `event.target` nicht).
 */
function buildDockNode(parent: HTMLElement, nodeId: string, handles: Array<{ port: string; cx: number; cy: number }>) {
  const node = document.createElement('div');
  node.className = 'react-flow__node';
  node.setAttribute('data-id', nodeId);
  for (const h of handles) {
    const el = document.createElement('div');
    el.className = 'react-flow__handle';
    el.setAttribute('data-handleid', h.port);
    el.setAttribute('data-nodeid', nodeId);
    vi.spyOn(el, 'getBoundingClientRect').mockReturnValue({
      x: h.cx - 5, y: h.cy - 5, width: 10, height: 10,
      left: h.cx - 5, top: h.cy - 5, right: h.cx + 5, bottom: h.cy + 5,
      toJSON: () => ({}),
    } as DOMRect);
    node.appendChild(el);
  }
  parent.appendChild(node);
  return node;
}

const DOCK_HANDLES = [
  { port: 'top', cx: 600, cy: 300 },
  { port: 'right', cx: 660, cy: 340 },
  { port: 'bottom', cx: 600, cy: 380 },
  { port: 'left', cx: 540, cy: 340 },
];

function renderPreview(over: { canDockTo?: (id: string) => boolean; onDockTargetChange?: (id: string | null) => void } = {}) {
  const canvas = document.createElement('div');
  document.body.appendChild(canvas);
  const ref = createRef<HTMLElement>() as { current: HTMLElement | null };
  ref.current = canvas;
  const onDockTargetChange = over.onDockTargetChange ?? vi.fn();
  const utils = render(
    <EdgeDetachPreview
      sourceNodeId="a"
      sourcePort="right"
      targetPort="left"
      canvasRef={ref}
      canDockTo={over.canDockTo ?? (() => true)}
      onDockTargetChange={onDockTargetChange}
    />,
  );
  return { ...utils, canvas, onDockTargetChange };
}

describe('EdgeDetachPreview', () => {
  it('beforeFirstPointerMove_rendersNothing', () => {
    const { queryByTestId } = renderPreview();
    expect(queryByTestId('edge-detach-preview')).not.toBeInTheDocument();
  });

  it('afterPointerMove_rendersPreviewFromSourcePort', () => {
    const { canvas, queryByTestId, container } = renderPreview();

    act(() => { fireEvent.pointerMove(canvas, { clientX: 400, clientY: 300 }); });

    expect(queryByTestId('edge-detach-preview')).toBeInTheDocument();
    // Docking-Punkt sitzt am Cursor; der Pfad startet am rechten Port (x=300, y=90).
    const dot = container.querySelector('circle') as SVGCircleElement;
    expect(dot.getAttribute('cx')).toBe('400');
    expect(dot.getAttribute('cy')).toBe('300');
    expect(container.querySelector('path')?.getAttribute('d')).toContain('M300,90');
  });

  it('overDockableNode_endsOnTheHandleCentreNotTheCursor', () => {
    // Cursor knapp unter dem Bottom-Handle (600, 380). Die Linie muss auf dem HANDLE landen —
    // genau das erzeugt der Klick anschließend auch, weil beide `resolveDockTarget` fragen.
    const { canvas, container, onDockTargetChange } = renderPreview();
    const node = buildDockNode(canvas, 'target-1', DOCK_HANDLES);

    act(() => { fireEvent.pointerMove(node, { clientX: 604, clientY: 386 }); });

    const dot = container.querySelector('circle') as SVGCircleElement;
    expect(dot.getAttribute('cx')).toBe('600');
    expect(dot.getAttribute('cy')).toBe('380');
    expect(onDockTargetChange).toHaveBeenCalledWith('target-1');
  });

  it('overNonDockableNode_staysOnTheCursor', () => {
    const { canvas, container, onDockTargetChange } = renderPreview({ canDockTo: () => false });
    const node = buildDockNode(canvas, 'target-1', DOCK_HANDLES);

    act(() => { fireEvent.pointerMove(node, { clientX: 604, clientY: 386 }); });

    const dot = container.querySelector('circle') as SVGCircleElement;
    expect(dot.getAttribute('cx')).toBe('604');
    expect(dot.getAttribute('cy')).toBe('386');
    expect(onDockTargetChange).not.toHaveBeenCalled();
  });

  it('movingWithinTheSameNode_doesNotRepublishTheDockTarget', () => {
    // Der Callback hängt an State der Editor-Seite. Pro Mausbewegung zu feuern würde den
    // ganzen Designer neu rendern — er darf nur beim Node-WECHSEL feuern.
    const { canvas, onDockTargetChange } = renderPreview();
    const node = buildDockNode(canvas, 'target-1', DOCK_HANDLES);

    act(() => { fireEvent.pointerMove(node, { clientX: 604, clientY: 386 }); });
    act(() => { fireEvent.pointerMove(node, { clientX: 602, clientY: 384 }); });
    act(() => { fireEvent.pointerMove(node, { clientX: 655, clientY: 342 }); });

    expect(onDockTargetChange).toHaveBeenCalledTimes(1);
    expect(onDockTargetChange).toHaveBeenCalledWith('target-1');
  });

  it('leavingTheNode_publishesNull', () => {
    const { canvas, onDockTargetChange } = renderPreview();
    const node = buildDockNode(canvas, 'target-1', DOCK_HANDLES);

    act(() => { fireEvent.pointerMove(node, { clientX: 604, clientY: 386 }); });
    act(() => { fireEvent.pointerMove(canvas, { clientX: 50, clientY: 50 }); });

    expect(onDockTargetChange).toHaveBeenNthCalledWith(1, 'target-1');
    expect(onDockTargetChange).toHaveBeenNthCalledWith(2, null);
  });

  it('unmount_clearsTheDockTarget', () => {
    // Sonst bliebe der Hervorhebungs-Ring nach dem Ende des Detach stehen.
    const { canvas, unmount, onDockTargetChange } = renderPreview();
    const node = buildDockNode(canvas, 'target-1', DOCK_HANDLES);

    act(() => { fireEvent.pointerMove(node, { clientX: 604, clientY: 386 }); });
    unmount();

    expect(onDockTargetChange).toHaveBeenLastCalledWith(null);
  });

  it('unmeasuredSourceNode_rendersNothing', () => {
    internalNode.current = { measured: {}, internals: { positionAbsolute: { x: 100, y: 50 } } };
    const { canvas, queryByTestId } = renderPreview();

    act(() => { fireEvent.pointerMove(canvas, { clientX: 400, clientY: 300 }); });

    expect(queryByTestId('edge-detach-preview')).not.toBeInTheDocument();
  });

  it('missingSourceNode_rendersNothing', () => {
    internalNode.current = undefined;
    const { canvas, queryByTestId } = renderPreview();

    act(() => { fireEvent.pointerMove(canvas, { clientX: 400, clientY: 300 }); });

    expect(queryByTestId('edge-detach-preview')).not.toBeInTheDocument();
  });

  it('unmount_removesPointerMoveListener', () => {
    const { canvas, unmount } = renderPreview();
    const remove = vi.spyOn(canvas, 'removeEventListener');

    unmount();

    expect(remove).toHaveBeenCalledWith('pointermove', expect.any(Function));
  });
});
