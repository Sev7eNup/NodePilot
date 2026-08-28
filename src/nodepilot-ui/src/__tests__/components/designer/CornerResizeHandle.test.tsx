import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { CornerResizeHandle } from '../../../components/designer/library/NodeLibrary';

/**
 * CornerResizeHandle is the bottom-right grip that resizes the shared-folders box on the
 * workflows page in both directions. It holds no resize math of its own; it forwards mouse
 * and double-click events to two useResizable instances (width and height). These tests pin
 * that forwarding contract and the corner cursor affordance.
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
    // The grip glyph is drawn as several nested diagonal strokes.
    expect(svg!.querySelectorAll('line').length).toBeGreaterThanOrEqual(2);
    // The glyph is decorative, so pointer events must pass through to the drag wrapper.
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
