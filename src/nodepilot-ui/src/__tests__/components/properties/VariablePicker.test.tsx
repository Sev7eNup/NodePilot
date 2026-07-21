import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  GlobalVariablePicker,
  OptionsPicker,
  VariablePicker,
} from '../../../components/designer/properties/shared';
import type { UpstreamVariable } from '../../../lib/upstreamVariables';

const variables: UpstreamVariable[] = Array.from({ length: 57 }, (_, index) => ({
  stepId: 'junction-1',
  label: `Junction: waitAll (4 lanes) → value ${index + 1}`,
  variable: `read_${index + 1}`,
  expression: `{{read_${index + 1}.output}}`,
  type: 'string',
}));

describe('VariablePicker', () => {
  it('portalsThePopoverOutsideOverflowAncestors', () => {
    render(
      <div data-testid="clipping-section" className="overflow-hidden">
        <VariablePicker upstreamVars={variables} onPick={vi.fn()} />
      </div>,
    );

    fireEvent.click(screen.getByRole('button', { name: /Vars57/i }));

    const popover = screen.getByTestId('anchored-picker-popover');
    expect(popover.parentElement).toBe(document.body);
    expect(screen.getByPlaceholderText(/Search variable/i)).toBeInTheDocument();
  });

  it('keepsPortalInteractionsInsideThePicker', () => {
    const onPick = vi.fn();
    render(<VariablePicker upstreamVars={variables} onPick={onPick} />);
    fireEvent.click(screen.getByRole('button', { name: /Vars57/i }));

    fireEvent.mouseDown(screen.getByText('read_1'));
    fireEvent.click(screen.getByText('read_1'));

    expect(onPick).toHaveBeenCalledWith('{{read_1.output}}');
  });

  it('portalsGlobalVariablesOutsideOverflowAncestors', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(['global-variables'], []);
    render(
      <QueryClientProvider client={queryClient}>
        <div className="overflow-hidden">
          <GlobalVariablePicker onPick={vi.fn()} />
        </div>
      </QueryClientProvider>,
    );

    fireEvent.click(screen.getByRole('button', { name: /Globals0/i }));
    expect(screen.getByTestId('anchored-picker-popover').parentElement).toBe(document.body);
  });

  it('portalsMachineAndCredentialOptionListsOutsideOverflowAncestors', () => {
    const onPick = vi.fn();
    render(
      <div className="overflow-hidden">
        <OptionsPicker
          options={[{ id: 'machine-1', label: 'Server 01' }]}
          onPick={onPick}
          label="Select machine"
        />
      </div>,
    );

    fireEvent.click(screen.getByRole('button', { name: /List1/i }));
    expect(screen.getByTestId('anchored-picker-popover').parentElement).toBe(document.body);
    fireEvent.click(screen.getByText('Server 01'));
    expect(onPick).toHaveBeenCalledWith('machine-1');
  });
});
