import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QuickConnectPicker } from '../../../components/designer/overlays/QuickConnectPicker';
import { useCustomActivityCatalogStore } from '../../../lib/customActivities';

const disk = {
  id: 'def-1', key: 'disk_check', type: 'custom:disk_check', name: 'Disk Check', icon: 'extension',
  runsRemote: false, timeout: 'always', inputs: [], outputs: [], isEnabled: true, version: 1,
};

function panelOf(button: HTMLElement): HTMLElement {
  return button.closest('.fixed') as HTMLElement;
}

describe('QuickConnectPicker', () => {
  afterEach(() => useCustomActivityCatalogStore.getState().setCatalog([]));

  it('position_nearBottomRightEdge_isClampedIntoTheViewport', () => {
    useCustomActivityCatalogStore.getState().setCatalog([disk]);
    render(<QuickConnectPicker x={1500} y={900} onPick={vi.fn()} onClose={vi.fn()} />);

    const { top, left } = panelOf(screen.getByRole('button', { name: 'Disk Check' })).style;
    // The picker is max 60vh tall and 320px wide, so these bounds keep all of it on screen.
    expect(top).toMatch(/^max\(8px, min\(900px, .*40vh.*\)\)$/);
    expect(left).toMatch(/^max\(8px, min\(1500px, .*100vw.*\)\)$/);
  });

  it('pick_customActivity_reportsItsType', () => {
    useCustomActivityCatalogStore.getState().setCatalog([disk]);
    const onPick = vi.fn();
    render(<QuickConnectPicker x={100} y={100} onPick={onPick} onClose={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: 'Disk Check' }));
    expect(onPick).toHaveBeenCalledWith('custom:disk_check', 'Disk Check');
  });
});
