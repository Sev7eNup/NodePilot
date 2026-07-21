import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, fireEvent } from '@testing-library/react';
import { Position } from '@xyflow/react';

/**
 * EdgeReshapeHandles renders partly into the SVG (dashed connector lines) and partly into
 * the EdgeLabelRenderer layer (cp1/cp2 as HTML divs with z-index 10, so they sit above the
 * "+" insert button that lives in the same layer — otherwise the button would cover cp2 on
 * Right→Bottom edges).
 *
 * Mocks:
 *   - useReactFlow().screenToFlowPosition: identity (test passes screen coords directly)
 *   - EdgeLabelRenderer: a plain <div> wrapper, so the portal content lands in the normal
 *     DOM tree and stays reachable via getByTestId(...).
 *
 * Pinned behavior:
 *   - 2 handle-divs + 2 dashed hint-lines render when visible AND canWrite=true
 *   - Nothing renders when visible=false OR canWrite=false
 *   - pointerdown on a handle calls beginEdgeReshape EXACTLY ONCE before any updateEdgeShape
 *   - subsequent pointermove events call updateEdgeShape with deltas applied to the right CP
 *   - port-aware default control points (sanity: the rendered div transform follows port direction)
 *   - the cp-divs sit above the "+" button via position:absolute + zIndex:10 + pointerEvents:all
 */

vi.mock('@xyflow/react', async () => {
  const actual = await vi.importActual<typeof import('@xyflow/react')>('@xyflow/react');
  return {
    ...actual,
    useReactFlow: () => ({
      screenToFlowPosition: (p: { x: number; y: number }) => ({ x: p.x, y: p.y }),
    }),
    EdgeLabelRenderer: ({ children }: { children: React.ReactNode }) => (
      <div data-testid="edge-label-renderer">{children}</div>
    ),
  };
});

import { EdgeReshapeHandles } from '../../../../components/designer/edges/EdgeReshapeHandles';
import { EdgeEditingContext } from '../../../../components/designer/edges/edgeEditingContext';
import { useDesignStore } from '../../../../stores/designStore';

function renderInSvg(ui: React.ReactElement, ctxOverride: Partial<React.ContextType<typeof EdgeEditingContext>> = {}) {
  const ctx: React.ContextType<typeof EdgeEditingContext> = {
    onInsertRequest: vi.fn(),
    canWrite: true,
    beginEdgeReshape: vi.fn(),
    updateEdgeShape: vi.fn(),
    resetEdgeShape: vi.fn(),
    ...ctxOverride,
  };
  const result = render(
    <EdgeEditingContext.Provider value={ctx}>
      <svg width={500} height={500}>{ui}</svg>
    </EdgeEditingContext.Provider>
  );
  return { ...result, ctx };
}

const baseProps = {
  edgeId: 'e1',
  sourceX: 0, sourceY: 0, sourcePosition: Position.Right,
  targetX: 200, targetY: 0, targetPosition: Position.Left,
  controlPoints: undefined,
  visible: true,
};

/** Parses the second translate(…) component out of the inline-style transform,
 *  e.g. "translate(-50%, -50%) translate(50px, 0px)" → { x: 50, y: 0 }. */
function parseTransformXY(el: HTMLElement): { x: number; y: number } {
  const m = el.style.transform.match(/translate\(([-\d.]+)px,\s*([-\d.]+)px\)$/);
  if (!m) throw new Error(`Cannot parse transform: ${el.style.transform}`);
  return { x: Number(m[1]), y: Number(m[2]) };
}

describe('EdgeReshapeHandles', () => {
  beforeEach(() => {
    // Reset snapToGrid before each test — Zustand singleton survives between renders.
    useDesignStore.getState().setSnapToGrid(false);
  });

  it('rendersTwoHandlesAndTwoLines_whenVisibleAndCanWrite', () => {
    const { queryByTestId, container } = renderInSvg(<EdgeReshapeHandles {...baseProps} />);
    expect(queryByTestId('edge-reshape-cp1-e1')).not.toBeNull();
    expect(queryByTestId('edge-reshape-cp2-e1')).not.toBeNull();
    expect(queryByTestId('edge-reshape-lines-e1')).not.toBeNull();
    expect(container.querySelectorAll('line')).toHaveLength(2);
  });

  it('rendersNothing_whenVisibleFalse', () => {
    const { queryByTestId, container } = renderInSvg(<EdgeReshapeHandles {...baseProps} visible={false} />);
    expect(queryByTestId('edge-reshape-cp1-e1')).toBeNull();
    expect(queryByTestId('edge-reshape-cp2-e1')).toBeNull();
    expect(queryByTestId('edge-reshape-lines-e1')).toBeNull();
    expect(container.querySelectorAll('line')).toHaveLength(0);
  });

  it('rendersNothing_whenCanWriteFalse', () => {
    const { queryByTestId } = renderInSvg(<EdgeReshapeHandles {...baseProps} />, { canWrite: false });
    expect(queryByTestId('edge-reshape-cp1-e1')).toBeNull();
    expect(queryByTestId('edge-reshape-cp2-e1')).toBeNull();
    expect(queryByTestId('edge-reshape-lines-e1')).toBeNull();
  });

  it('defaultControlPoints_rightLeft_handlesPositionedHorizontally', () => {
    // distance = 0.25 * 200 = 50 → cp1 at (50, 0), cp2 at (150, 0)
    const { getByTestId } = renderInSvg(<EdgeReshapeHandles {...baseProps} />);
    expect(parseTransformXY(getByTestId('edge-reshape-cp1-e1'))).toEqual({ x: 50, y: 0 });
    expect(parseTransformXY(getByTestId('edge-reshape-cp2-e1'))).toEqual({ x: 150, y: 0 });
  });

  it('defaultControlPoints_topBottom_handlesPositionedVertically', () => {
    const { getByTestId } = renderInSvg(
      <EdgeReshapeHandles
        {...baseProps}
        sourceX={100} sourceY={0} sourcePosition={Position.Bottom}
        targetX={100} targetY={200} targetPosition={Position.Top}
      />
    );
    expect(parseTransformXY(getByTestId('edge-reshape-cp1-e1'))).toEqual({ x: 100, y: 50 });
    expect(parseTransformXY(getByTestId('edge-reshape-cp2-e1'))).toEqual({ x: 100, y: 150 });
  });

  it('userDefinedControlPoints_overrideDefaults', () => {
    const { getByTestId } = renderInSvg(
      <EdgeReshapeHandles
        {...baseProps}
        controlPoints={{ cp1x: 30, cp1y: -80, cp2x: 170, cp2y: -80 }}
      />
    );
    expect(parseTransformXY(getByTestId('edge-reshape-cp1-e1'))).toEqual({ x: 30, y: -80 });
    expect(parseTransformXY(getByTestId('edge-reshape-cp2-e1'))).toEqual({ x: 170, y: -80 });
  });

  it('handles_renderedAboveButtonLayer_via_zIndexAndPositioning', () => {
    // Regression guard for exactly the layering bug this component fixes:
    // z-index only takes effect on a positioned element, and pointer-events:all must be
    // set so clicks land on the handle instead of falling through to the button layer.
    const { getByTestId } = renderInSvg(<EdgeReshapeHandles {...baseProps} />);
    for (const id of ['edge-reshape-cp1-e1', 'edge-reshape-cp2-e1']) {
      const handle = getByTestId(id);
      expect(handle.style.position).toBe('absolute');
      expect(handle.style.zIndex).toBe('10');
      expect(handle.style.pointerEvents).toBe('all');
    }
  });

  it('pointerDown_callsBeginEdgeReshapeExactlyOnce', () => {
    const { getByTestId, ctx } = renderInSvg(<EdgeReshapeHandles {...baseProps} />);
    const cp1 = getByTestId('edge-reshape-cp1-e1');

    fireEvent.pointerDown(cp1, { clientX: 50, clientY: 0, pointerId: 1 });

    expect(ctx.beginEdgeReshape).toHaveBeenCalledTimes(1);
    expect(ctx.beginEdgeReshape).toHaveBeenCalledWith('e1');
    expect(ctx.updateEdgeShape).not.toHaveBeenCalled();
  });

  it('pointerMove_callsUpdateEdgeShape_withDeltaAppliedToCp1', () => {
    const { getByTestId, ctx } = renderInSvg(<EdgeReshapeHandles {...baseProps} />);
    const cp1 = getByTestId('edge-reshape-cp1-e1');

    fireEvent.pointerDown(cp1, { clientX: 50, clientY: 0, pointerId: 1 });
    fireEvent.pointerMove(window, { clientX: 80, clientY: -40 });

    // Default cp1 = (50, 0), delta = (+30, -40), so new cp1 = (80, -40), cp2 unchanged.
    expect(ctx.updateEdgeShape).toHaveBeenCalledWith('e1', {
      cp1x: 80, cp1y: -40,
      cp2x: 150, cp2y: 0,
    });
  });

  it('multiplePointerMoves_callBeginOnlyOnce', () => {
    const { getByTestId, ctx } = renderInSvg(<EdgeReshapeHandles {...baseProps} />);
    const cp2 = getByTestId('edge-reshape-cp2-e1');

    fireEvent.pointerDown(cp2, { clientX: 150, clientY: 0, pointerId: 1 });
    fireEvent.pointerMove(window, { clientX: 160, clientY: 10 });
    fireEvent.pointerMove(window, { clientX: 170, clientY: 20 });
    fireEvent.pointerMove(window, { clientX: 180, clientY: 30 });

    expect(ctx.beginEdgeReshape).toHaveBeenCalledTimes(1);
    expect(ctx.updateEdgeShape).toHaveBeenCalledTimes(3);
    // last call: delta = (+30, +30), new cp2 = (180, 30)
    expect(ctx.updateEdgeShape).toHaveBeenLastCalledWith('e1', {
      cp1x: 50, cp1y: 0,
      cp2x: 180, cp2y: 30,
    });
  });

  it('pointerUp_endsDrag_subsequentMovesIgnored', () => {
    const { getByTestId, ctx } = renderInSvg(<EdgeReshapeHandles {...baseProps} />);
    const cp1 = getByTestId('edge-reshape-cp1-e1');

    fireEvent.pointerDown(cp1, { clientX: 50, clientY: 0, pointerId: 1 });
    fireEvent.pointerMove(window, { clientX: 60, clientY: 10 });
    fireEvent.pointerUp(window, { clientX: 60, clientY: 10 });

    const callsAfterUp = (ctx.updateEdgeShape as ReturnType<typeof vi.fn>).mock.calls.length;
    fireEvent.pointerMove(window, { clientX: 200, clientY: 200 });
    expect((ctx.updateEdgeShape as ReturnType<typeof vi.fn>).mock.calls.length).toBe(callsAfterUp);
  });

  it('snapToGrid_roundsControlPointsToGridSize', () => {
    useDesignStore.getState().setSnapToGrid(true);
    useDesignStore.getState().setSnapGridSize(20);

    const { getByTestId, ctx } = renderInSvg(<EdgeReshapeHandles {...baseProps} />);
    const cp1 = getByTestId('edge-reshape-cp1-e1');

    fireEvent.pointerDown(cp1, { clientX: 50, clientY: 0, pointerId: 1 });
    // delta = (+33, -47) → unsnapped new cp1 = (83, -47); rounded to nearest 20 → (80, -40)
    fireEvent.pointerMove(window, { clientX: 83, clientY: -47 });

    expect(ctx.updateEdgeShape).toHaveBeenCalledWith('e1', {
      cp1x: 80, cp1y: -40,
      cp2x: 150, cp2y: 0,
    });
  });

  it('canWriteFalse_pointerDownNoOp', () => {
    const { queryByTestId, ctx } = renderInSvg(<EdgeReshapeHandles {...baseProps} />, { canWrite: false });

    // Component renders nothing when canWrite=false, so no handles to click.
    expect(queryByTestId('edge-reshape-cp1-e1')).toBeNull();
    expect(ctx.beginEdgeReshape).not.toHaveBeenCalled();
  });
});
