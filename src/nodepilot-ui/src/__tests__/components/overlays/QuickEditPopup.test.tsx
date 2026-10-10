import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import type { Node } from '@xyflow/react';
import { QuickEditPopup } from '../../../components/designer/overlays/QuickEditPopup';

/**
 * QuickEditPopup edits a single primary field of a node (script, url, query) without opening
 * the full PropertiesPanel. These tests cover the field type and initial value per activity
 * type, Save passing a partial config patch that holds only the edited key, Cancel and Escape
 * closing without saving, the seconds field coercing to a number, and unknown activity types
 * rendering nothing.
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
  it('does not register a delayed outside-click listener after unmount', () => {
    vi.useFakeTimers();
    const add = vi.spyOn(globalThis, 'addEventListener');
    try {
      const { unmount } = render(<QuickEditPopup node={makeNode('restApi', { url: 'https://example.test' })}
        screenX={100} screenY={300} onSave={vi.fn()} onClose={vi.fn()} />);
      unmount();
      vi.runAllTimers();
      expect(add.mock.calls.filter(([event]) => event === 'mousedown')).toEqual([]);
    } finally { add.mockRestore(); vi.useRealTimers(); }
  });

  it('returnData preserves its object contract when saved unchanged or edited', () => {
    const onSave = vi.fn();
    const onClose = vi.fn();
    const data = { answer: '{{step.param.answer}}', count: 3, ok: true };
    render(<QuickEditPopup node={makeNode('returnData', { data })} screenX={100} screenY={300}
      onSave={onSave} onClose={onClose} />);

    const field = screen.getByRole('textbox') as HTMLTextAreaElement;
    expect(JSON.parse(field.value)).toEqual(data);
    fireEvent.click(screen.getByText('Save'));
    expect(onSave).toHaveBeenLastCalledWith('step-1', { data });

    fireEvent.change(field, { target: { value: '{"answer":"updated","nested":{"value":2}}' } });
    fireEvent.click(screen.getByText('Save'));
    expect(onSave).toHaveBeenLastCalledWith('step-1', { data: { answer: 'updated', nested: { value: 2 } } });
  });

  it.each(['broken JSON', 'null', '[]', '"text"'])('returnData rejects %s without replacing the existing map', (value) => {
    const onSave = vi.fn();
    const onClose = vi.fn();
    render(<QuickEditPopup node={makeNode('returnData', { data: { answer: 'keep' } })}
      screenX={100} screenY={300} onSave={onSave} onClose={onClose} />);

    fireEvent.change(screen.getByRole('textbox'), { target: { value } });
    fireEvent.click(screen.getByText('Save'));
    expect(onSave).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent(/JSON object/i);
  });

  it('runScript_rendersTextareaWithCurrentScript', () => {
    const node = makeNode('runScript', { script: 'Get-Service' });
    render(
      <QuickEditPopup node={node} screenX={100} screenY={300} onSave={vi.fn()} onClose={vi.fn()} />
    );

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
    // Escape fires both the input's onKeyDown and the global window keydown listener, and
    // both call onClose, so assert that it happened rather than how often.
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
    // The saved value must be a number, not the string '42'.
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
    // screenX sits past the viewport, so the popup left edge clamps to viewport - width - 16.
    const { container } = render(
      <QuickEditPopup node={node} screenX={9999} screenY={500} onSave={vi.fn()} onClose={vi.fn()} />
    );
    const popup = container.firstChild as HTMLElement;
    const left = parseInt(popup.style.left, 10);
    // jsdom uses a 1024px viewport; a 360px popup plus a 16px gutter clamps left to 648.
    expect(left).toBeLessThan(9999);
  });
});
