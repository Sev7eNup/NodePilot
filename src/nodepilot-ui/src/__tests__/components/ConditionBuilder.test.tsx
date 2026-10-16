import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ConditionBuilder, type ExprNode } from '../../components/designer/ConditionBuilder';
import type { UpstreamVariable } from '../../lib/upstreamVariables';

const upstreamVars: UpstreamVariable[] = [
  { stepId: 'stepA', label: 'Step A', variable: 'stepA', expression: '{{stepA.output}}', type: 'string' },
  { stepId: 'stepA', label: 'Step A → status', variable: 'stepA.param.status', expression: '{{stepA.param.status}}', type: 'string' },
  { stepId: 'stepB', label: 'Step B', variable: 'stepB', expression: '{{stepB.output}}', type: 'string' },
];

function renderBuilder(ui: React.ReactElement) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

describe('ConditionBuilder', () => {
  it('shows empty hint when no condition set', () => {
    const onChange = vi.fn();
    renderBuilder(<ConditionBuilder value={null} upstreamVars={upstreamVars} onChange={onChange} />);
    expect(screen.getByText(/No condition set/i)).toBeInTheDocument();
  });

  it('adds a comparison row via + Condition button', () => {
    const onChange = vi.fn();
    renderBuilder(<ConditionBuilder value={null} upstreamVars={upstreamVars} onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: /Condition/i }));
    expect(onChange).toHaveBeenCalledTimes(1);
    const arg = onChange.mock.calls[0][0] as ExprNode;
    expect(arg.type).toBe('comparison');
    if (arg.type === 'comparison') {
      expect(arg.op).toBe('==');
      expect(arg.left.kind).toBe('variable');
    }
  });

  it('emits a group when multiple comparisons are added', () => {
    const existing: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 'stepA', field: 'output' },
      op: '==',
      right: { kind: 'literal', value: 'OK' },
    };
    const onChange = vi.fn();
    renderBuilder(<ConditionBuilder value={existing} upstreamVars={upstreamVars} onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: /Condition/i }));
    const arg = onChange.mock.calls[0][0] as ExprNode;
    expect(arg.type).toBe('group');
    if (arg.type === 'group') expect(arg.children.length).toBe(2);
  });

  it('hides right-side operand for unary ops', () => {
    const existing: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 'stepA', field: 'output' },
      op: 'isEmpty',
    };
    renderBuilder(<ConditionBuilder value={existing} upstreamVars={upstreamVars} onChange={() => {}} />);
    // Only one "Variable" toggle button should be visible (for LHS)
    expect(screen.getAllByRole('button', { name: /^Variable$/i }).length).toBe(1);
  });

  it('renders Variable and Literal mode toggles on each side for binary ops', () => {
    const existing: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 'stepA', field: 'output' },
      op: 'contains',
      right: { kind: 'literal', value: 'foo' },
    };
    renderBuilder(<ConditionBuilder value={existing} upstreamVars={upstreamVars} onChange={() => {}} />);
    expect(screen.getAllByRole('button', { name: /^Variable$/i }).length).toBe(2);
    expect(screen.getAllByRole('button', { name: /^Literal$/i }).length).toBe(2);
    // Literal mode input visible
    expect(screen.getByPlaceholderText(/fixed value/i)).toBeInTheDocument();
  });

  it('switches RHS from Literal to Variable and emits variable operand', () => {
    const existing: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 'stepA', field: 'output' },
      op: '==',
      right: { kind: 'literal', value: 'foo' },
    };
    const onChange = vi.fn();
    renderBuilder(<ConditionBuilder value={existing} upstreamVars={upstreamVars} onChange={onChange} />);
    // Second "Variable" button is the RHS toggle
    const variableButtons = screen.getAllByRole('button', { name: /^Variable$/i });
    fireEvent.click(variableButtons[1]);
    const arg = onChange.mock.calls[0][0] as ExprNode;
    expect(arg.type).toBe('comparison');
    if (arg.type === 'comparison') {
      expect(arg.right?.kind).toBe('variable');
      // Narrow to the step-source variant: the union also covers global, manual and event
      // operands, which have no stepId or field.
      if (arg.right?.kind === 'variable' && arg.right.source !== 'global' && arg.right.source !== 'manual' && arg.right.source !== 'event') {
        expect(arg.right.stepId).toBe('stepA');
        expect(arg.right.field).toBe('output');
      }
    }
  });

  it('exposes param fields via Field dropdown for the selected step', () => {
    const existing: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 'stepA', field: 'output' },
      op: '==',
      right: { kind: 'literal', value: '' },
    };
    renderBuilder(<ConditionBuilder value={existing} upstreamVars={upstreamVars} onChange={() => {}} />);
    // The LHS Field dropdown should list output/error/success + status (param)
    expect(screen.getByText(/status \(param\)/i)).toBeInTheDocument();
  });

  it('renders not(group) and keeps its shape when an inner value changes', () => {
    const existing: ExprNode = {
      type: 'not',
      child: {
        type: 'group', op: 'OR', children: [
          { type: 'comparison', left: { kind: 'literal', value: 'a' }, op: '==', right: { kind: 'literal', value: 'first' } },
          { type: 'comparison', left: { kind: 'literal', value: 'b' }, op: '==', right: { kind: 'literal', value: 'second' } },
        ],
      },
    };
    const onChange = vi.fn();
    renderBuilder(<ConditionBuilder value={existing} upstreamVars={upstreamVars} onChange={onChange} />);

    expect(screen.getByDisplayValue('first')).toBeInTheDocument();
    fireEvent.change(screen.getByDisplayValue('second'), { target: { value: 'changed' } });

    expect(onChange).toHaveBeenLastCalledWith({
      type: 'not',
      child: {
        type: 'group', op: 'OR', children: [
          { type: 'comparison', left: { kind: 'literal', value: 'a' }, op: '==', right: { kind: 'literal', value: 'first' } },
          { type: 'comparison', left: { kind: 'literal', value: 'b' }, op: '==', right: { kind: 'literal', value: 'changed' } },
        ],
      },
    });
  });

  it('shows a manual operand as itself and keeps it on unrelated edits', () => {
    const existing: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', source: 'manual', name: 'region' },
      op: '==',
      right: { kind: 'literal', value: 'eu' },
    };
    const onChange = vi.fn();
    renderBuilder(<ConditionBuilder value={existing} upstreamVars={upstreamVars} onChange={onChange} />);

    const selected = screen.getByRole('option', { name: 'manual.region' }) as HTMLOptionElement;
    expect(selected.selected).toBe(true);
    fireEvent.change(screen.getByDisplayValue('eu'), { target: { value: 'us' } });

    expect(onChange).toHaveBeenLastCalledWith({ ...existing, right: { kind: 'literal', value: 'us' } });
  });

  it('marks a reference to a step that is no longer upstream instead of dropping it', () => {
    const existing: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 'removedStep', field: 'param', paramName: 'code' },
      op: '==',
      right: { kind: 'literal', value: '0' },
    };
    const onChange = vi.fn();
    renderBuilder(<ConditionBuilder value={existing} upstreamVars={upstreamVars} onChange={onChange} />);

    const stale = screen.getByRole('option', { name: 'removedStep.param.code (not available)' }) as HTMLOptionElement;
    expect(stale.selected).toBe(true);
    expect(stale.closest('select')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByText(/not available here/i)).toBeInTheDocument();

    fireEvent.change(screen.getByDisplayValue('0'), { target: { value: '1' } });
    expect(onChange).toHaveBeenLastCalledWith({ ...existing, right: { kind: 'literal', value: '1' } });
  });

  it('resolves a reference by output variable to its step', () => {
    const aliased: UpstreamVariable[] = [
      { stepId: 'step-1', label: 'Check Disk', variable: 'diskCheck', expression: '{{diskCheck.output}}', type: 'string' },
    ];
    const existing: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', stepId: 'diskCheck', field: 'success' },
      op: 'isTrue',
    };
    renderBuilder(<ConditionBuilder value={existing} upstreamVars={aliased} onChange={() => {}} />);

    expect((screen.getByRole('option', { name: 'Check Disk → success (bool)' }) as HTMLOptionElement).selected).toBe(true);
    expect(screen.queryByText(/not available/i)).toBeNull();
  });

  it('keeps the operand when the already active Variable mode is clicked', () => {
    const existing: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', source: 'manual', name: 'region' },
      op: 'isNotEmpty',
    };
    const onChange = vi.fn();
    renderBuilder(<ConditionBuilder value={existing} upstreamVars={upstreamVars} onChange={onChange} />);

    fireEvent.click(screen.getByRole('button', { name: /^Variable$/i }));
    expect(onChange).not.toHaveBeenCalled();
  });
});
