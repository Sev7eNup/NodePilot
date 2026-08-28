import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import type { Edge, Node } from '@xyflow/react';
import { EdgePropertiesPanel } from '../../../components/designer/EdgePropertiesPanel';
import { useDesignStore } from '../../../stores/designStore';

// The store-driven confirm dialog replaces the native confirm() and resolves true by default.
vi.mock('../../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});
import { confirmDialog } from '../../../stores/confirmStore';

/**
 * EdgePropertiesPanel edits one edge's label, condition (simple or expression mode) and
 * disabled state. These tests pin the onUpdate, onDelete and onClose call shapes, that a mode
 * switch clears the inactive field, that the quick buttons set the right `condition`, that
 * delete goes through confirmDialog(), and the custom-label override warning.
 * ConditionBuilder is mocked; it is covered by components/ConditionBuilder.test.tsx.
 */

vi.mock('../../../components/designer/ConditionBuilder', () => ({
  ConditionBuilder: ({ value, onChange }: { value: unknown; onChange: (v: unknown) => void }) => (
    <div data-testid="condition-builder">
      <span>cb-current:{JSON.stringify(value)}</span>
      <button type="button" onClick={() => onChange({ type: 'comparison', op: '==', left: { kind: 'literal', value: 'a' }, right: { kind: 'literal', value: 'b' } })}>
        cb-set-expr
      </button>
    </div>
  ),
}));

function activityNode(id: string, label: string): Node {
  return { id, type: 'activity', position: { x: 0, y: 0 }, data: { label, activityType: 'runScript' } };
}

function makeEdge(id: string, source: string, target: string, data: Record<string, unknown> = {}): Edge {
  return { id, source, target, type: 'labeled', data };
}

beforeEach(() => {
  vi.mocked(confirmDialog).mockClear();
  useDesignStore.setState({ designerMode: 'expert' });
});

describe('EdgePropertiesPanel', () => {
  function defaultProps(overrides: Partial<Parameters<typeof EdgePropertiesPanel>[0]> = {}) {
    const source = activityNode('step-1', 'Source Step');
    const target = activityNode('step-2', 'Target Step');
    return {
      edge: makeEdge('e1', 'step-1', 'step-2', { label: '', condition: '' }),
      allNodes: [source, target],
      allEdges: [],
      onUpdate: vi.fn(),
      onDelete: vi.fn(),
      onClose: vi.fn(),
      ...overrides,
    };
  }

  it('rendersSourceAndTargetLabels', () => {
    const props = defaultProps();
    render(<EdgePropertiesPanel {...props} />);
    expect(screen.getByText('Source Step')).toBeInTheDocument();
    expect(screen.getByText('Target Step')).toBeInTheDocument();
  });

  it('showsPortControlsInExpertMode_andUpdatesHandles', () => {
    // All four ports are always available; the selector is gated only by expert mode.
    const props = defaultProps();
    render(<EdgePropertiesPanel {...props} />);

    expect(screen.getByText('Connection ports')).toBeInTheDocument();
    fireEvent.click(screen.getAllByLabelText('Bottom')[0]);

    expect(props.onUpdate).toHaveBeenCalledWith('e1', { sourceHandle: 'bottom' });
  });

  it('hidesPortControlsInStandardMode', () => {
    useDesignStore.setState({ designerMode: 'standard' });
    render(<EdgePropertiesPanel {...defaultProps()} />);
    expect(screen.queryByText('Connection ports')).not.toBeInTheDocument();
  });

  it('labelInput_changeFiresOnUpdateWithNewLabel', () => {
    const props = defaultProps();
    render(<EdgePropertiesPanel {...props} />);

    const input = screen.getByPlaceholderText(/On Success/i) as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'Custom Label' } });

    expect(props.onUpdate).toHaveBeenCalledOnce();
    const [edgeId, patch] = (props.onUpdate as ReturnType<typeof vi.fn>).mock.calls[0];
    expect(edgeId).toBe('e1');
    expect((patch.data as Record<string, unknown>).label).toBe('Custom Label');
  });

  it('quickButtonOnSuccess_setsConditionToSourceSuccess', () => {
    const props = defaultProps();
    render(<EdgePropertiesPanel {...props} />);

    fireEvent.click(screen.getByText('On Success'));

    const [, patch] = (props.onUpdate as ReturnType<typeof vi.fn>).mock.calls[0];
    expect((patch.data as Record<string, unknown>).condition).toBe('step-1.success');
    // An empty label is derived from the condition, which yields 'On Success'.
    expect((patch.data as Record<string, unknown>).label).toBe('On Success');
  });

  it('quickButtonOnFailure_setsConditionToSourceFailed', () => {
    const props = defaultProps();
    render(<EdgePropertiesPanel {...props} />);

    fireEvent.click(screen.getByText('On Failure'));

    const [, patch] = (props.onUpdate as ReturnType<typeof vi.fn>).mock.calls[0];
    expect((patch.data as Record<string, unknown>).condition).toBe('step-1.failed');
    expect((patch.data as Record<string, unknown>).label).toBe('On Failure');
  });

  it('quickButtonAlways_clearsCondition', () => {
    const props = defaultProps({
      edge: makeEdge('e1', 'step-1', 'step-2', { label: 'On Success', condition: 'step-1.success' }),
    });
    render(<EdgePropertiesPanel {...props} />);

    fireEvent.click(screen.getByText('Always'));

    const [, patch] = (props.onUpdate as ReturnType<typeof vi.fn>).mock.calls[0];
    expect((patch.data as Record<string, unknown>).condition).toBe('');
    // The label is cleared rather than set to "Always". An edge without a condition always
    // runs, so the panel keeps no label for that state.
    expect((patch.data as Record<string, unknown>).label).toBe('');
  });

  it('switchToExpression_clearsSimpleConditionAndShowsBuilder', () => {
    const props = defaultProps({
      edge: makeEdge('e1', 'step-1', 'step-2', { condition: 'step-1.success' }),
    });
    render(<EdgePropertiesPanel {...props} />);

    fireEvent.click(screen.getByText('Expression'));

    expect(screen.getByTestId('condition-builder')).toBeInTheDocument();
    // Switching to expression mode clears the simple condition.
    const [, patch] = (props.onUpdate as ReturnType<typeof vi.fn>).mock.calls[0];
    expect((patch.data as Record<string, unknown>).condition).toBe('');
  });

  it('switchToSimple_clearsConditionExpression_andHidesBuilder', () => {
    const props = defaultProps({
      edge: makeEdge('e1', 'step-1', 'step-2', {
        conditionExpression: { type: 'comparison', op: '==', left: { kind: 'literal', value: 'a' }, right: { kind: 'literal', value: 'b' } },
      }),
    });
    render(<EdgePropertiesPanel {...props} />);

    // The builder is shown initially because the edge carries a conditionExpression.
    expect(screen.getByTestId('condition-builder')).toBeInTheDocument();

    fireEvent.click(screen.getByText('Simple'));

    // After the switch the builder is gone and the simple input is rendered.
    expect(screen.queryByTestId('condition-builder')).not.toBeInTheDocument();
    const [, patch] = (props.onUpdate as ReturnType<typeof vi.fn>).mock.calls[0];
    // conditionExpression is cleared by setting it to undefined.
    expect((patch.data as Record<string, unknown>).conditionExpression).toBeUndefined();
  });

  it('expressionMode_builderOnChange_propagatesViaOnUpdate', () => {
    const props = defaultProps({
      edge: makeEdge('e1', 'step-1', 'step-2', {
        conditionExpression: { type: 'comparison', op: '==', left: { kind: 'literal', value: 'x' }, right: { kind: 'literal', value: 'y' } },
      }),
    });
    render(<EdgePropertiesPanel {...props} />);

    fireEvent.click(screen.getByText('cb-set-expr'));

    const [, patch] = (props.onUpdate as ReturnType<typeof vi.fn>).mock.calls[0];
    // The mocked builder emits the AST shape defined in the vi.mock block above.
    expect((patch.data as Record<string, unknown>).conditionExpression).toEqual({
      type: 'comparison', op: '==',
      left: { kind: 'literal', value: 'a' },
      right: { kind: 'literal', value: 'b' },
    });
  });

  it('disabledToggle_emitsDisabledTrueWhenCurrentlyEnabled', () => {
    const props = defaultProps();
    render(<EdgePropertiesPanel {...props} />);

    fireEvent.click(screen.getByText(/Connection is active/));

    const [, patch] = (props.onUpdate as ReturnType<typeof vi.fn>).mock.calls[0];
    expect((patch.data as Record<string, unknown>).disabled).toBe(true);
    // Disabling the edge also turns off the animation.
    expect(patch.animated).toBe(false);
  });

  it('disabledToggle_emitsDisabledFalseWhenCurrentlyDisabled', () => {
    const props = defaultProps({
      edge: makeEdge('e1', 'step-1', 'step-2', { disabled: true }),
    });
    render(<EdgePropertiesPanel {...props} />);

    fireEvent.click(screen.getByText(/Connection is disabled/));

    const [, patch] = (props.onUpdate as ReturnType<typeof vi.fn>).mock.calls[0];
    expect((patch.data as Record<string, unknown>).disabled).toBe(false);
    expect(patch.animated).toBe(true);
  });

  it('deleteButton_callsOnDeleteWhenConfirmed', async () => {
    const props = defaultProps();
    render(<EdgePropertiesPanel {...props} />);

    fireEvent.click(screen.getByText(/Delete Connection/));
    await waitFor(() => expect(props.onDelete).toHaveBeenCalledWith('e1'));
  });

  it('deleteButton_doesNotCallOnDeleteWhenConfirmCancelled', async () => {
    vi.mocked(confirmDialog).mockResolvedValueOnce(false);
    const props = defaultProps();
    render(<EdgePropertiesPanel {...props} />);

    fireEvent.click(screen.getByText(/Delete Connection/));
    await waitFor(() => expect(confirmDialog).toHaveBeenCalled());
    expect(props.onDelete).not.toHaveBeenCalled();
  });

  it('closeButton_callsOnClose', () => {
    const props = defaultProps();
    const { container } = render(<EdgePropertiesPanel {...props} />);

    // The close button in the panel header.
    const closeBtn = container.querySelector('button.text-on-surface-variant') as HTMLButtonElement;
    fireEvent.click(closeBtn);
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('customLabelDifferentFromAuto_showsOverrideWarning', () => {
    const props = defaultProps({
      edge: makeEdge('e1', 'step-1', 'step-2', { label: 'My weird label', condition: 'step-1.success' }),
    });
    render(<EdgePropertiesPanel {...props} />);

    expect(screen.getByText(/Custom label overrides the condition/)).toBeInTheDocument();
    // The suggested label "On Success" appears both in the warning box and on the quick
    // button, so checking for at least one match is enough.
    expect(screen.getAllByText(/On Success/).length).toBeGreaterThanOrEqual(1);
  });

  it('useAutoButton_clearsCustomLabel', () => {
    const props = defaultProps({
      edge: makeEdge('e1', 'step-1', 'step-2', { label: 'Override', condition: 'step-1.success' }),
    });
    render(<EdgePropertiesPanel {...props} />);

    fireEvent.click(screen.getByTitle(/Clear custom label/));

    const [, patch] = (props.onUpdate as ReturnType<typeof vi.fn>).mock.calls[0];
    expect((patch.data as Record<string, unknown>).label).toBe('');
  });

  it('canonicalLabelMatchingAuto_doesNotShowOverrideWarning', () => {
    const props = defaultProps({
      edge: makeEdge('e1', 'step-1', 'step-2', { label: 'On Success', condition: 'step-1.success' }),
    });
    render(<EdgePropertiesPanel {...props} />);
    expect(screen.queryByText(/Custom label overrides/)).not.toBeInTheDocument();
  });
});
