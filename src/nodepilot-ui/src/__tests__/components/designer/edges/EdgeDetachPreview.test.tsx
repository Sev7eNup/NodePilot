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

function renderPreview() {
  const canvas = document.createElement('div');
  document.body.appendChild(canvas);
  const ref = createRef<HTMLElement>() as { current: HTMLElement | null };
  ref.current = canvas;
  const utils = render(
    <EdgeDetachPreview sourceNodeId="a" sourcePort="right" targetPort="left" canvasRef={ref} />,
  );
  return { ...utils, canvas };
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
