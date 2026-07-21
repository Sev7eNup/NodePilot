import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { ViewMenu } from '../../../components/designer/header/ViewMenu';
import { useDesignStore } from '../../../stores/designStore';

/**
 * The "Display" canvas-settings popover (ViewMenu → CanvasSettingsPanel). It's a settings
 * DIALOG, not a menu: labeled rows with a switch / segmented control / stepper each. The body
 * only mounts while the dialog is open, so every test clicks the trigger first. SPA renders
 * ENGLISH under the test i18n. Store state is the source of truth for control behaviour.
 */

beforeEach(() => {
  useDesignStore.setState({
    edgesAnimated: true,
    edgeWidthIndex: 2,
    edgeRouting: 'smart',
    nodeStyle: 'classic',
    nodeIconStyle: 'shape',
    flexiblePortsEnabled: false,
    autoHidePorts: true,
    nodeScaleIndex: 2,
    labelFontOffsetIndex: 2,
    snapToGrid: false,
    snapGridSize: 20,
    premiumCanvas: true,
  });
});

function open() {
  render(<ViewMenu />);
  fireEvent.click(screen.getByTestId('canvas-settings-trigger'));
}

describe('CanvasSettings — dialog open/close + focus', () => {
  it('trigger_click_opensLabelledDialog', () => {
    open();
    const dialog = screen.getByRole('dialog');
    expect(dialog).toHaveAttribute('aria-labelledby', 'canvas-settings-title');
    // The header the dialog is labelled by renders the title text.
    expect(document.getElementById('canvas-settings-title')).toHaveTextContent('Display');
    // Focus moves into the panel on open.
    expect(dialog).toHaveFocus();
  });

  it('escapeKey_closesDialog_andReturnsFocusToTrigger', async () => {
    open();
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    fireEvent.keyDown(document, { key: 'Escape' });

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.getByTestId('canvas-settings-trigger')).toHaveFocus();
  });

  it('rendersLabeledSectionsAndRows', () => {
    open();
    expect(screen.getByRole('heading', { name: 'Nodes' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Connections' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Canvas' })).toBeInTheDocument();
    expect(screen.getByText('Animation')).toBeInTheDocument();
    expect(screen.getByText('Routing')).toBeInTheDocument();
    // Explanations no longer consume permanent vertical space, but remain available to AT.
    expect(screen.queryByText('Flowing pulse dots along the edges.')).not.toBeInTheDocument();
    expect(screen.getByTestId('canvas-setting-edge-animation'))
      .toHaveAttribute('aria-description', 'Flowing pulse dots along the edges.');
  });
});

describe('CanvasSettings — controls affect design store', () => {
  it('animationSwitch_click_togglesEdgesAnimated', () => {
    open();
    const sw = screen.getByTestId('canvas-setting-edge-animation');
    expect(sw).toHaveAttribute('aria-checked', 'true');

    fireEvent.click(sw);

    expect(useDesignStore.getState().edgesAnimated).toBe(false);
    expect(sw).toHaveAttribute('aria-checked', 'false');
  });

  it('flexiblePortsSwitch_click_togglesStore', () => {
    open();
    const sw = screen.getByTestId('canvas-setting-flexible-ports');
    expect(sw).toHaveAttribute('aria-checked', 'false');

    fireEvent.click(sw);

    expect(useDesignStore.getState().flexiblePortsEnabled).toBe(true);
    expect(sw).toHaveAttribute('aria-checked', 'true');
  });

  it('autoHideAndPremiumSwitches_clickWholeRow_togglesStore', () => {
    open();
    const autoHide = screen.getByTestId('canvas-setting-auto-hide-ports');
    const premium = screen.getByTestId('canvas-setting-premium-canvas');

    fireEvent.click(autoHide);
    fireEvent.click(premium);

    expect(useDesignStore.getState().autoHidePorts).toBe(false);
    expect(useDesignStore.getState().premiumCanvas).toBe(false);
    expect(autoHide).toHaveAttribute('aria-checked', 'false');
    expect(premium).toHaveAttribute('aria-checked', 'false');
  });

  it('routingSegment_click_setsEdgeRouting', () => {
    open();
    fireEvent.click(screen.getByRole('radio', { name: 'Curve' }));
    expect(useDesignStore.getState().edgeRouting).toBe('curved');
  });

  it('nodeStyleSegment_click_setsNodeStyle', () => {
    open();
    expect(useDesignStore.getState().nodeStyle).toBe('classic');
    fireEvent.click(screen.getByRole('radio', { name: 'Card' }));
    expect(useDesignStore.getState().nodeStyle).toBe('card');
  });

  it('iconStyleSegment_click_setsNodeIconStyle', () => {
    open();
    fireEvent.click(screen.getByRole('radio', { name: 'Glyph' }));
    expect(useDesignStore.getState().nodeIconStyle).toBe('glyph');
  });

  it('clickingActiveSegment_isNoOp_notAccidentalToggle', () => {
    open();
    // 'Classic' is already active — clicking it must keep the value, never flip to 'card'.
    fireEvent.click(screen.getByRole('radio', { name: 'Classic' }));
    expect(useDesignStore.getState().nodeStyle).toBe('classic');
  });

  it('thicknessStepper_plus_increasesEdgeWidthIndex', () => {
    open();
    const stepper = screen.getByTestId('canvas-setting-edge-thickness');
    fireEvent.click(within(stepper).getByLabelText('Thicker edges'));
    expect(useDesignStore.getState().edgeWidthIndex).toBe(3);
  });

  it('nodeAndLabelSteppers_plus_increaseTheirIndices', () => {
    open();
    fireEvent.click(within(screen.getByTestId('canvas-setting-node-scale')).getByLabelText('Larger nodes'));
    fireEvent.click(within(screen.getByTestId('canvas-setting-label-font')).getByLabelText('Larger label text'));

    expect(useDesignStore.getState().nodeScaleIndex).toBe(3);
    expect(useDesignStore.getState().labelFontOffsetIndex).toBe(3);
  });

  it('snapChips_pickSize_enablesSnapAndSetsGridSize', () => {
    open();
    const snap = screen.getByTestId('canvas-setting-snap');
    fireEvent.click(within(snap).getByRole('radio', { name: '20' }));
    expect(useDesignStore.getState().snapToGrid).toBe(true);
    expect(useDesignStore.getState().snapGridSize).toBe(20);
  });
});

describe('CanvasSettings — classic-only rows', () => {
  it('cardMode_hidesClassicOnlyRows', () => {
    useDesignStore.setState({ nodeStyle: 'card' });
    open();
    // Node-style row itself stays (it's how you switch back); classic-only rows disappear.
    expect(screen.getByText('Style')).toBeInTheDocument();
    expect(screen.queryByText('Size')).not.toBeInTheDocument();
    expect(screen.queryByText('Premium effects')).not.toBeInTheDocument();
    expect(screen.queryByText('Icon style')).not.toBeInTheDocument();
    expect(screen.queryByText('Label')).not.toBeInTheDocument();
    // Canvas remains as a stable group because grid snapping applies to both node styles.
    expect(screen.getByRole('heading', { name: 'Canvas' })).toBeInTheDocument();
    expect(screen.getByText('Grid')).toBeInTheDocument();
  });
});
