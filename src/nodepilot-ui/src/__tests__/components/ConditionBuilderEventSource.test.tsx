import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ConditionBuilder, type ExprNode } from '../../components/designer/ConditionBuilder';

const EVENT_FIELDS = [
  { name: 'workflowName', label: 'Workflow name' },
  { name: 'severity', label: 'Severity' },
];

function renderBuilder(value: ExprNode | null, onChange: (n: ExprNode | null) => void) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ConditionBuilder value={value} upstreamVars={[]} eventFields={EVENT_FIELDS} onChange={onChange} />
    </QueryClientProvider>,
  );
}

describe('ConditionBuilder (event source)', () => {
  it('addCondition_defaultsLeftToFirstEventField', () => {
    const onChange = vi.fn();
    renderBuilder(null, onChange);

    fireEvent.click(screen.getByRole('button', { name: /Condition/i }));

    expect(onChange).toHaveBeenCalled();
    const emitted = onChange.mock.calls[0][0] as ExprNode;
    expect(emitted.type).toBe('comparison');
    if (emitted.type === 'comparison') {
      expect(emitted.left).toMatchObject({ kind: 'variable', source: 'event', name: 'workflowName' });
    }
  });

  it('rendersEventFieldOptionsInDropdown', () => {
    const value: ExprNode = {
      type: 'comparison',
      left: { kind: 'variable', source: 'event', name: 'severity' },
      op: '==',
      right: { kind: 'literal', value: 'Critical' },
    };
    renderBuilder(value, vi.fn());

    // Both event fields are offered as operand options (not step/global ones).
    expect(screen.getAllByRole('option').some((o) => o.textContent === 'Severity')).toBe(true);
    expect(screen.getAllByRole('option').some((o) => o.textContent === 'Workflow name')).toBe(true);
  });
});
