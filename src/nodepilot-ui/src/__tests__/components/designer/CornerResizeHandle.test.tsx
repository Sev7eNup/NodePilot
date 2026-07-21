import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { CornerResizeHandle } from '../../../components/designer/library/NodeLibrary';

/**
 * CornerResizeHandle is the bottom-right grip used to 2D-resize the "Freigegebene
 * Ordner" box on the workflows page. It carries no resize math itself — it forwards
 * mouse/double-click events to two useResizable instances (width + height). These
 * tests pin that forwarding contract and the corner cursor affordance.
 */
describe('CornerResizeHandle', () => {
  it('rendersWithCornerCursorAffordance', () => {
    render(<CornerResizeHandle title="Resize" />);
    const handle = screen.getByTestId('folder-panel-corner-resize');
    expect(handle).toBeInTheDocument();
    expect(handle.className).toContain('cursor-se-resize');
    expect(handle).toHaveAttribute('title', 'Resize');
  });

  it('rendersTheClassicDiagonalGripGlyph', () => {
    render(<CornerResizeHandle title="Resize" />);
    const handle = screen.getByTestId('folder-panel-corner-resize');
    const svg = handle.querySelector('svg');
    expect(svg).not.toBeNull();
    // Classic resize grip = several nested diagonal strokes (not the old single corner bracket).
    expect(svg!.querySelectorAll('line').length).toBeGreaterThanOrEqual(2);
    // Decorative only — pointer events must pass through to the drag wrapper.
    expect(svg!.getAttribute('class')).toContain('pointer-events-none');
  });

  it('forwardsMouseDown', () => {
    const onMouseDown = vi.fn();
    render(<CornerResizeHandle onMouseDown={onMouseDown} />);
    fireEvent.mouseDown(screen.getByTestId('folder-panel-corner-resize'));
    expect(onMouseDown).toHaveBeenCalledTimes(1);
  });

  it('forwardsDoubleClick', () => {
    const onDoubleClick = vi.fn();
    render(<CornerResizeHandle onDoubleClick={onDoubleClick} />);
    fireEvent.doubleClick(screen.getByTestId('folder-panel-corner-resize'));
    expect(onDoubleClick).toHaveBeenCalledTimes(1);
  });
});
