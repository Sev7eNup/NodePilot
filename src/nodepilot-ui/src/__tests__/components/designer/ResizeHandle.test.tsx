import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ResizeHandle } from '../../../components/designer/library/NodeLibrary';

/**
 * ResizeHandle is the splitter between a panel (node properties, sidebar, folder box,
 * execution step list) and its neighbour. It used to be invisible at rest and only lit up on
 * hover, which made it near-impossible to find and aim at. These tests pin the two properties
 * that fixed that — a grip that is painted **without** a hover prefix, and a pointer target
 * wider than the 4 px lane — plus the event-forwarding contract.
 */
describe('ResizeHandle', () => {
  const grip = (handle: HTMLElement) =>
    [...handle.querySelectorAll('span')].find((s) => s.className.includes('rounded-full'));

  it('exposesSeparatorRoleWithOrientation', () => {
    const { rerender } = render(<ResizeHandle direction="horizontal" />);
    expect(screen.getByRole('separator')).toHaveAttribute('aria-orientation', 'vertical');
    rerender(<ResizeHandle direction="vertical" />);
    expect(screen.getByRole('separator')).toHaveAttribute('aria-orientation', 'horizontal');
  });

  it('paintsTheGripAtRestNotOnlyOnHover', () => {
    render(<ResizeHandle direction="horizontal" />);
    const pill = grip(screen.getByRole('separator'));
    expect(pill).toBeDefined();
    // The base colour must be unprefixed — a `group-hover:`-only fill is exactly the
    // regression this guards (handle invisible until the cursor happens to find it).
    expect(pill!.className).toMatch(/(?:^|\s)bg-outline-variant\/70(?:\s|$)/);
    expect(pill!.className).toContain('group-hover:bg-primary');
  });

  it('widensThePointerTargetBeyondTheDrawnLane', () => {
    render(<ResizeHandle direction="horizontal" />);
    const handle = screen.getByRole('separator');
    // Layout width stays 4 px so the canvas doesn't shift…
    expect(handle.className).toContain('w-1');
    // …while a transparent extender overhangs the lane on both sides.
    const extender = [...handle.querySelectorAll('span')].find((s) => s.className.includes('-inset-x-1'));
    expect(extender).toBeDefined();
    expect(extender!.className).not.toContain('pointer-events-none');
  });

  it('keepsTheDecorationOutOfThePointerPath', () => {
    render(<ResizeHandle direction="horizontal" />);
    const handle = screen.getByRole('separator');
    for (const cls of ['rounded-full', 'group-hover:bg-primary/25']) {
      const decoration = [...handle.querySelectorAll('span')].find((s) => s.className.includes(cls));
      expect(decoration!.className).toContain('pointer-events-none');
    }
  });

  it('forwardsMouseDownAndDoubleClickAndExtraProps', () => {
    const onMouseDown = vi.fn();
    const onDoubleClick = vi.fn();
    render(
      <ResizeHandle
        direction="horizontal"
        title="Resize panel"
        onMouseDown={onMouseDown}
        onDoubleClick={onDoubleClick}
      />,
    );
    const handle = screen.getByRole('separator');
    expect(handle).toHaveAttribute('title', 'Resize panel');
    fireEvent.mouseDown(handle);
    fireEvent.doubleClick(handle);
    expect(onMouseDown).toHaveBeenCalledTimes(1);
    expect(onDoubleClick).toHaveBeenCalledTimes(1);
  });

  it('forwardsMouseDownFromTheOverhangingHitArea', () => {
    const onMouseDown = vi.fn();
    render(<ResizeHandle direction="horizontal" onMouseDown={onMouseDown} />);
    const handle = screen.getByRole('separator');
    const extender = [...handle.querySelectorAll('span')].find((s) => s.className.includes('-inset-x-1'));
    // The extender carries no handlers of its own — it relies on bubbling to the wrapper.
    fireEvent.mouseDown(extender!);
    expect(onMouseDown).toHaveBeenCalledTimes(1);
  });
});
