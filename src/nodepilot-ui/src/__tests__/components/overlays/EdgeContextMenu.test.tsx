import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { EdgeContextMenu } from '../../../components/designer/overlays/EdgeContextMenu';
import { useDesignStore } from '../../../stores/designStore';

beforeEach(() => useDesignStore.setState({ designerMode: 'expert' }));

/**
 * EdgeContextMenu mirrors NodeContextMenu's interaction model. We pin:
 *   - All three actions render (Disable-or-Enable / Swap / Delete)
 *   - isDisabled flips the toggle label from "Disable" to "Enable"
 *   - Each action call invokes its handler AND closes the menu
 *   - Outside-click + Escape close the menu
 *   - Mounting position respects x/y props (so it docks at the right-click coordinate)
 *
 * "Edit condition" is intentionally absent: right-clicking an edge already auto-selects it,
 * which surfaces the EdgePropertiesPanel where conditions are edited. A duplicate menu item
 * would do nothing extra.
 */

function defaultProps(over: Partial<Parameters<typeof EdgeContextMenu>[0]> = {}) {
  return {
    x: 100,
    y: 200,
    isDisabled: false,
    hasCustomShape: false,
    onToggleDisabled: vi.fn(),
    onSwapSourceTarget: vi.fn(),
    onResetShape: vi.fn(),
    onDelete: vi.fn(),
    onClose: vi.fn(),
    ...over,
  };
}

describe('EdgeContextMenu', () => {
  it('rendersAllThreeActions', () => {
    render(<EdgeContextMenu {...defaultProps()} />);

    expect(screen.getByText('Disable edge')).toBeInTheDocument();
    expect(screen.getByText('Swap source ↔ target')).toBeInTheDocument();
    expect(screen.getByText('Delete')).toBeInTheDocument();
  });

  it('doesNotRenderEditConditionItem', () => {
    // Pinning the omission: right-click already auto-selects + opens EdgePropertiesPanel,
    // so a duplicate menu entry would be redundant. If someone re-adds it, this fails.
    render(<EdgeContextMenu {...defaultProps()} />);
    expect(screen.queryByText(/edit condition/i)).not.toBeInTheDocument();
  });

  it('isDisabledTrue_swapsLabelToEnableEdge', () => {
    render(<EdgeContextMenu {...defaultProps({ isDisabled: true })} />);
    expect(screen.getByText('Enable edge')).toBeInTheDocument();
    expect(screen.queryByText('Disable edge')).not.toBeInTheDocument();
  });

  it('clickToggleDisabled_invokesAndCloses', () => {
    const props = defaultProps();
    render(<EdgeContextMenu {...props} />);

    fireEvent.click(screen.getByText('Disable edge'));

    expect(props.onToggleDisabled).toHaveBeenCalledOnce();
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('clickSwap_invokesAndCloses', () => {
    const props = defaultProps();
    render(<EdgeContextMenu {...props} />);

    fireEvent.click(screen.getByText('Swap source ↔ target'));

    expect(props.onSwapSourceTarget).toHaveBeenCalledOnce();
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('clickDelete_invokesAndCloses', () => {
    const props = defaultProps();
    render(<EdgeContextMenu {...props} />);

    fireEvent.click(screen.getByText('Delete'));

    expect(props.onDelete).toHaveBeenCalledOnce();
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('escapeKey_closesMenu', () => {
    const props = defaultProps();
    render(<EdgeContextMenu {...props} />);

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('clickOutsideMenu_closesMenu', () => {
    const props = defaultProps();
    render(
      <div>
        <div data-testid="outside" style={{ width: 200, height: 200 }} />
        <EdgeContextMenu {...props} />
      </div>
    );

    fireEvent.mouseDown(screen.getByTestId('outside'));
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('clickInsideMenu_doesNotClose', () => {
    const props = defaultProps();
    render(<EdgeContextMenu {...props} />);

    fireEvent.mouseDown(screen.getByText('Disable edge'));

    expect(props.onClose).not.toHaveBeenCalled();
  });

  it('resetShapeItem_hiddenWhenNoCustomShape', () => {
    render(<EdgeContextMenu {...defaultProps({ hasCustomShape: false })} />);
    expect(screen.queryByText('Reset edge shape')).not.toBeInTheDocument();
  });

  it('resetShapeItem_visibleWhenCustomShape', () => {
    render(<EdgeContextMenu {...defaultProps({ hasCustomShape: true })} />);
    expect(screen.getByText('Reset edge shape')).toBeInTheDocument();
  });

  it('clickResetShape_invokesAndCloses', () => {
    const props = defaultProps({ hasCustomShape: true });
    render(<EdgeContextMenu {...props} />);

    fireEvent.click(screen.getByText('Reset edge shape'));

    expect(props.onResetShape).toHaveBeenCalledOnce();
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('positionsItselfAtGivenCoordinates', () => {
    const { container } = render(<EdgeContextMenu {...defaultProps({ x: 250, y: 350 })} />);
    const menu = container.querySelector('div.absolute') as HTMLElement;
    expect(menu.style.left).toBe('250px');
    expect(menu.style.top).toBe('350px');
  });
});
