import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, fireEvent, act } from '@testing-library/react';
import { createRef } from 'react';

/**
 * EdgeDetachPreview draws the dashed line from the source port to the cursor while an edge
 * target is detached through the context menu.
 *
 * Mocks:
 *   - useReactFlow().screenToFlowPosition: identity, so the test passes flow coordinates
 *   - useInternalNode: returns either a measured node or undefined
 *   - ViewportPortal: a plain wrapper, so its content lands in the normal DOM tree
 *
 * Pinned behavior:
 *   - nothing renders before the first mouse move, since there is no loose end to show
 *   - the preview appears after a pointermove on the canvas
 *   - an unmeasured source node suppresses the preview instead of anchoring it at (0,0)
 *   - over a dockable node the line ends on the handle centre, not at the cursor
 *   - over a node that cannot be docked to, the line stays at the cursor
 *   - `onDockTargetChange` fires only when the node under the cursor changes, because it
 *     drives editor-page state
 *   - the pointermove listener is removed on unmount
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

/** Run requestAnimationFrame synchronously; the component throttles pointer moves with it. */
beforeEach(() => {
  internalNode.current = { measured: { width: 200, height: 80 }, internals: { positionAbsolute: { x: 100, y: 50 } } };
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => { cb(0); return 1; });
  vi.stubGlobal('cancelAnimationFrame', () => {});
});

/**
 * A `.react-flow__node` with stubbed handle rects that sits under the cursor as a dock target.
 * jsdom has no layout, so `getBoundingClientRect` must be stubbed per handle.
 *
 * The node is appended inside the canvas because the component listens on the canvas and reads
 * `event.target`. Only real bubbling out of the node produces the same event shape as in the
 * browser; the `target` option of `fireEvent` does not override `event.target`.
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
    // The dock point sits at the cursor; the path starts at the right port (x=300, y=90).
    const dot = container.querySelector('circle') as SVGCircleElement;
    expect(dot.getAttribute('cx')).toBe('400');
    expect(dot.getAttribute('cy')).toBe('300');
    expect(container.querySelector('path')?.getAttribute('d')).toContain('M300,90');
  });

  it('overDockableNode_endsOnTheHandleCentreNotTheCursor', () => {
    // The cursor sits just below the bottom handle (600, 380). The line must end on the handle,
    // which is where a click docks too, because both paths ask `resolveDockTarget`.
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
    // The callback drives editor-page state. Firing on every mouse move would re-render the
    // whole designer, so it may only fire when the node under the cursor changes.
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
    // Otherwise the highlight ring would stay visible after the detach ends.
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
