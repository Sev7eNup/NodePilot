import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { NodeContextMenu } from '../../../components/designer/overlays/NodeContextMenu';
import { useDesignStore } from '../../../stores/designStore';

beforeEach(() => useDesignStore.setState({ designerMode: 'expert' }));

/**
 * NodeContextMenu is the right-click pop-up on a canvas node. We pin:
 *   - All four actions render with correct labels (mode-dependent for disable/breakpoint)
 *   - Each action call invokes its handler AND closes the menu
 *   - Outside-click closes the menu
 *   - Escape closes the menu
 */

function defaultProps(over: Partial<Parameters<typeof NodeContextMenu>[0]> = {}) {
  return {
    x: 100,
    y: 200,
    isDisabled: false,
    hasBreakpoint: false,
    onDuplicate: vi.fn(),
    onToggleDisabled: vi.fn(),
    onToggleBreakpoint: vi.fn(),
    onDelete: vi.fn(),
    onClose: vi.fn(),
    ...over,
  };
}

describe('NodeContextMenu', () => {
  it('standardMode_hidesBreakpointAction', () => {
    useDesignStore.setState({ designerMode: 'standard' });
    render(<NodeContextMenu {...defaultProps()} />);
    expect(screen.queryByText('Add breakpoint')).not.toBeInTheDocument();
    expect(screen.getByText('Duplicate')).toBeInTheDocument();
  });

  it('rendersAllFourActions', () => {
    render(<NodeContextMenu {...defaultProps()} />);

    expect(screen.getByText('Duplicate')).toBeInTheDocument();
    expect(screen.getByText('Disable step')).toBeInTheDocument();
    expect(screen.getByText('Add breakpoint')).toBeInTheDocument();
    expect(screen.getByText('Delete')).toBeInTheDocument();
  });

  it('isDisabledTrue_swapsLabelToEnableStep', () => {
    render(<NodeContextMenu {...defaultProps({ isDisabled: true })} />);
    expect(screen.getByText('Enable step')).toBeInTheDocument();
    expect(screen.queryByText('Disable step')).not.toBeInTheDocument();
  });

  it('hasBreakpointTrue_swapsLabelToRemoveBreakpoint', () => {
    render(<NodeContextMenu {...defaultProps({ hasBreakpoint: true })} />);
    expect(screen.getByText('Remove breakpoint')).toBeInTheDocument();
    expect(screen.queryByText('Add breakpoint')).not.toBeInTheDocument();
  });

  it('clickDuplicate_invokesAndCloses', () => {
    const props = defaultProps();
    render(<NodeContextMenu {...props} />);

    fireEvent.click(screen.getByText('Duplicate'));

    expect(props.onDuplicate).toHaveBeenCalledOnce();
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('clickDelete_invokesAndCloses', () => {
    const props = defaultProps();
    render(<NodeContextMenu {...props} />);

    fireEvent.click(screen.getByText('Delete'));

    expect(props.onDelete).toHaveBeenCalledOnce();
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('clickToggleDisabled_invokesAndCloses', () => {
    const props = defaultProps();
    render(<NodeContextMenu {...props} />);

    fireEvent.click(screen.getByText('Disable step'));

    expect(props.onToggleDisabled).toHaveBeenCalledOnce();
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('clickToggleBreakpoint_invokesAndCloses', () => {
    const props = defaultProps();
    render(<NodeContextMenu {...props} />);

    fireEvent.click(screen.getByText('Add breakpoint'));

    expect(props.onToggleBreakpoint).toHaveBeenCalledOnce();
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('escapeKey_closesMenu', () => {
    const props = defaultProps();
    render(<NodeContextMenu {...props} />);

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('clickOutsideMenu_closesMenu', () => {
    const props = defaultProps();
    render(
      <div>
        <div data-testid="outside" style={{ width: 200, height: 200 }} />
        <NodeContextMenu {...props} />
      </div>
    );

    fireEvent.mouseDown(screen.getByTestId('outside'));
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('clickInsideMenu_doesNotClose', () => {
    const props = defaultProps();
    render(<NodeContextMenu {...props} />);

    // Mousedown on the menu itself (not on a button) should not close.
    fireEvent.mouseDown(screen.getByText('Duplicate'));

    expect(props.onClose).not.toHaveBeenCalled();
  });

  it('positionsItselfAtGivenCoordinates', () => {
    const { container } = render(<NodeContextMenu {...defaultProps({ x: 250, y: 350 })} />);
    const menu = container.querySelector('div.absolute') as HTMLElement;
    expect(menu.style.left).toBe('250px');
    expect(menu.style.top).toBe('350px');
  });
});
