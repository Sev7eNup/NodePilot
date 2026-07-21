import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import type { Edge, Node } from '@xyflow/react';
import { EdgePropertiesPanel } from '../../../components/designer/EdgePropertiesPanel';
import { useDesignStore } from '../../../stores/designStore';

// Store-driven confirm replaces the native confirm(); default-resolve true (user confirms).
vi.mock('../../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});
import { confirmDialog } from '../../../stores/confirmStore';

/**
 * EdgePropertiesPanel edits one edge's label, condition (simple or expression mode), and
 * disabled state. We pin:
 *   - All onUpdate / onDelete / onClose call shapes
 *   - Mode-toggle (simple ↔ expression) clears the inactive field
 *   - Quick-buttons On Success/Failure/Always set the right `condition`
 *   - Delete uses confirmDialog() and bails out on cancel
 *   - Custom-label-overrides-warning + Use-Auto button
 *
 * ConditionBuilder is mocked — it has its own tests in components/ConditionBuilder.test.tsx.
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
  useDesignStore.setState({ flexiblePortsEnabled: false, designerMode: 'expert' });
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

  it('hidesPortControlsInClassicModeForDefaultEdges', () => {
    const props = defaultProps();
    render(<EdgePropertiesPanel {...props} />);
    expect(screen.queryByText('Connection ports')).not.toBeInTheDocument();
  });

  it('showsPortControlsWhenFlexiblePortsAreEnabled_andUpdatesHandles', () => {
    useDesignStore.setState({ flexiblePortsEnabled: true });
    const props = defaultProps();
    render(<EdgePropertiesPanel {...props} />);

    expect(screen.getByText('Connection ports')).toBeInTheDocument();
    fireEvent.click(screen.getAllByLabelText('Bottom')[0]);

    expect(props.onUpdate).toHaveBeenCalledWith('e1', { sourceHandle: 'bottom' });
  });

  it('showsPortControlsForExistingCustomPortsEvenWhenToggleIsOff', () => {
    const props = defaultProps({
      edge: { ...makeEdge('e1', 'step-1', 'step-2', { label: '', condition: '' }), sourceHandle: 'bottom', targetHandle: 'top' },
    });
    render(<EdgePropertiesPanel {...props} />);
    expect(screen.getByText('Connection ports')).toBeInTheDocument();
    expect(screen.getByText(/Flexible ports are off/)).toBeInTheDocument();
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
    // Canonical empty label → derived to 'On Success'
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
    expect((patch.data as Record<string, unknown>).label).toBe('Always');
  });

  it('switchToExpression_clearsSimpleConditionAndShowsBuilder', () => {
    const props = defaultProps({
      edge: makeEdge('e1', 'step-1', 'step-2', { condition: 'step-1.success' }),
    });
    render(<EdgePropertiesPanel {...props} />);

    fireEvent.click(screen.getByText('Expression'));

    expect(screen.getByTestId('condition-builder')).toBeInTheDocument();
    // Switching to expression clears the simple condition
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

    // Builder is initially shown because edge has a conditionExpression
    expect(screen.getByTestId('condition-builder')).toBeInTheDocument();

    fireEvent.click(screen.getByText('Simple'));

    // After switching, builder is gone and the simple input is rendered
    expect(screen.queryByTestId('condition-builder')).not.toBeInTheDocument();
    const [, patch] = (props.onUpdate as ReturnType<typeof vi.fn>).mock.calls[0];
    // conditionExpression cleared (set to undefined)
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
    // The mock builder emits the AST shape from our vi.mock block above.
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
    // animated should flip to false when disabling
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

    // X close button in header
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
    // The auto-suggestion label "On Success" appears in the warning box AND on the
    // quick-button — checking that at least one match exists is enough.
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
