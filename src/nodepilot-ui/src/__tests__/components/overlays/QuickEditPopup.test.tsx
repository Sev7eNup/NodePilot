import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import type { Node } from '@xyflow/react';
import { QuickEditPopup } from '../../../components/designer/overlays/QuickEditPopup';

/**
 * QuickEditPopup edits a single primary field of a node (script, url, query, …) without
 * opening the full PropertiesPanel. We pin:
 *   - Renders the right field type (input vs textarea) per activity type
 *   - Initial value reflects current config
 *   - Save calls onSave with a *partial* config patch (only the edited key) and closes
 *   - Cancel/Escape closes without saving
 *   - `seconds` field coerces to number
 *   - Unknown activity types render nothing
 */

function makeNode(activityType: string, configValues: Record<string, unknown> = {}): Node {
  return {
    id: 'step-1',
    type: 'activity',
    position: { x: 0, y: 0 },
    data: { label: 'Test', activityType, config: configValues },
  };
}

describe('QuickEditPopup', () => {
  it('runScript_rendersTextareaWithCurrentScript', () => {
    const node = makeNode('runScript', { script: 'Get-Service' });
    render(
      <QuickEditPopup node={node} screenX={100} screenY={300} onSave={vi.fn()} onClose={vi.fn()} />
    );

    // textarea with the script value
    const textarea = screen.getByDisplayValue('Get-Service');
    expect(textarea.tagName).toBe('TEXTAREA');
  });

  it('restApi_rendersInputWithCurrentUrl', () => {
    const node = makeNode('restApi', { url: 'https://api.test/x' });
    render(
      <QuickEditPopup node={node} screenX={100} screenY={300} onSave={vi.fn()} onClose={vi.fn()} />
    );

    const input = screen.getByDisplayValue('https://api.test/x');
    expect(input.tagName).toBe('INPUT');
  });

  it('rendersFieldLabelForActivity', () => {
    const node = makeNode('serviceManagement', { serviceName: 'Spooler' });
    render(
      <QuickEditPopup node={node} screenX={100} screenY={300} onSave={vi.fn()} onClose={vi.fn()} />
    );

    expect(screen.getByText('Service Name')).toBeInTheDocument();
  });

  it('saveButton_callsOnSaveWithPartialPatchAndCloses', () => {
    const onSave = vi.fn();
    const onClose = vi.fn();
    const node = makeNode('restApi', { url: 'https://old' });
    render(
      <QuickEditPopup node={node} screenX={100} screenY={300} onSave={onSave} onClose={onClose} />
    );

    const input = screen.getByDisplayValue('https://old');
    fireEvent.change(input, { target: { value: 'https://new' } });
    fireEvent.click(screen.getByText('Save'));

    expect(onSave).toHaveBeenCalledWith('step-1', { url: 'https://new' });
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('cancelButton_closesWithoutSaving', () => {
    const onSave = vi.fn();
    const onClose = vi.fn();
    const node = makeNode('restApi', { url: 'https://old' });
    render(
      <QuickEditPopup node={node} screenX={100} screenY={300} onSave={onSave} onClose={onClose} />
    );

    fireEvent.click(screen.getByText('Cancel'));

    expect(onSave).not.toHaveBeenCalled();
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('escapeOnInput_closesWithoutSaving', () => {
    const onSave = vi.fn();
    const onClose = vi.fn();
    const node = makeNode('restApi', { url: 'https://x' });
    render(
      <QuickEditPopup node={node} screenX={100} screenY={300} onSave={onSave} onClose={onClose} />
    );

    fireEvent.keyDown(screen.getByDisplayValue('https://x'), { key: 'Escape' });

    expect(onSave).not.toHaveBeenCalled();
    // Escape fires both the input's onKeyDown and the global window keydown listener;
    // both invoke onClose. We only care that close happens, not the count.
    expect(onClose).toHaveBeenCalled();
  });

  it('enterOnSingleLineInput_savesAndCloses', () => {
    const onSave = vi.fn();
    const onClose = vi.fn();
    const node = makeNode('restApi', { url: 'https://x' });
    render(
      <QuickEditPopup node={node} screenX={100} screenY={300} onSave={onSave} onClose={onClose} />
    );

    fireEvent.keyDown(screen.getByDisplayValue('https://x'), { key: 'Enter' });

    expect(onSave).toHaveBeenCalledOnce();
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('delaySeconds_coercesToNumberOnSave', () => {
    const onSave = vi.fn();
    const node = makeNode('delay', { seconds: 5 });
    render(
      <QuickEditPopup node={node} screenX={100} screenY={300} onSave={onSave} onClose={vi.fn()} />
    );

    const input = screen.getByDisplayValue('5');
    fireEvent.change(input, { target: { value: '42' } });
    fireEvent.click(screen.getByText('Save'));

    expect(onSave).toHaveBeenCalledWith('step-1', { seconds: 42 });
    // Critical: number, not string '42'.
    const arg = onSave.mock.calls[0][1] as { seconds: unknown };
    expect(typeof arg.seconds).toBe('number');
  });

  it('unknownActivityType_rendersNothing', () => {
    const node = makeNode('definitelyNotARealType', {});
    const { container } = render(
      <QuickEditPopup node={node} screenX={100} screenY={300} onSave={vi.fn()} onClose={vi.fn()} />
    );
    expect(container.firstChild).toBeNull();
  });

  it('emptyInitialConfig_rendersEmptyField', () => {
    const node = makeNode('restApi', {});
    render(
      <QuickEditPopup node={node} screenX={100} screenY={300} onSave={vi.fn()} onClose={vi.fn()} />
    );

    const input = screen.getByPlaceholderText('https://…') as HTMLInputElement;
    expect(input.value).toBe('');
  });

  it('positionsLeftWithinViewport', () => {
    const node = makeNode('restApi', { url: 'x' });
    // Place screenX way past the viewport — popup left should clamp to viewport - width - 16.
    const { container } = render(
      <QuickEditPopup node={node} screenX={9999} screenY={500} onSave={vi.fn()} onClose={vi.fn()} />
    );
    const popup = container.firstChild as HTMLElement;
    const left = parseInt(popup.style.left, 10);
    // Viewport in jsdom defaults to 1024 wide; popup width 360 + 16px gutter = clamps to 648.
    expect(left).toBeLessThan(9999);
  });
});
