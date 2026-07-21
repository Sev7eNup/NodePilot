import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ContractMappingTable } from '../../../components/designer/properties/ContractMappingTable';
import type { WorkflowContractResponse } from '../../../types/api';

/**
 * Pin (V1):
 * - Required + missing-and-no-default → red border + error message
 * - Required + has-default → no error
 * - Empty-out a value → key REMOVED from values dict (engine-correct, lets child default kick in)
 * - Stale key (in values, not in contract) → render with warning + remove button
 * - hasManualTrigger=false → render info banner, no inputs table
 * - hasMultipleReturnDataNodes → output section gets warning badge
 * - hasReturnData=false → noReturnDataHint shown, no user outputs (only system)
 * - Conflict input → renders conflict badge
 * - Output hint uses parentStepHandle to render `{{handle.param.x}}`
 */

beforeEach(() => {
  // VariableInsertField transitively imports GlobalVariablePicker which fans out to
  // /api/global-variables — stub fetch.
  vi.spyOn(globalThis, 'fetch').mockResolvedValue(
    new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }),
  );
});

function wrap(ui: React.ReactElement) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

const baseContract: WorkflowContractResponse = {
  workflowId: 'abc',
  workflowName: 'PatchServer',
  hasManualTrigger: true,
  hasReturnData: true,
  hasMultipleReturnDataNodes: false,
  inputs: [],
  outputs: [
    { name: '__executionId', source: 'system' },
    { name: '__status', source: 'system' },
  ],
};

describe('ContractMappingTable', () => {
  it('renders no-input-contract banner when hasManualTrigger=false', () => {
    wrap(
      <ContractMappingTable
        contract={{ ...baseContract, hasManualTrigger: false }}
        values={{}}
        onChange={vi.fn()}
        upstreamVars={[]}
        parentStepHandle="patchChild"
      />,
    );
    expect(screen.getByText(/No declared input contract/i)).toBeInTheDocument();
    // No inputs header rendered
    expect(screen.queryByText(/Inputs expected/i)).not.toBeInTheDocument();
  });

  it('shows required-missing error for required input with no default and empty value', () => {
    wrap(
      <ContractMappingTable
        contract={{
          ...baseContract,
          inputs: [{ name: 'serverName', type: 'string', required: true, default: null, description: null, hasConflict: false }],
        }}
        values={{}}
        onChange={vi.fn()}
        upstreamVars={[]}
        parentStepHandle="patchChild"
      />,
    );
    expect(screen.getByText(/required — no default declared/i)).toBeInTheDocument();
  });

  it('does NOT show required-missing error when required has a default', () => {
    wrap(
      <ContractMappingTable
        contract={{
          ...baseContract,
          inputs: [{ name: 'reboot', type: 'bool', required: true, default: 'false', description: null, hasConflict: false }],
        }}
        values={{}}
        onChange={vi.fn()}
        upstreamVars={[]}
        parentStepHandle="patchChild"
      />,
    );
    expect(screen.queryByText(/required — no default declared/i)).not.toBeInTheDocument();
    expect(screen.getByText(/Default: false/i)).toBeInTheDocument();
  });

  it('removes the key from values when an input is cleared', () => {
    const onChange = vi.fn();
    wrap(
      <ContractMappingTable
        contract={{
          ...baseContract,
          inputs: [{ name: 'foo', type: 'string', required: false, default: null, description: null, hasConflict: false }],
        }}
        values={{ foo: 'bar' }}
        onChange={onChange}
        upstreamVars={[]}
        parentStepHandle="patchChild"
      />,
    );

    const input = screen.getByDisplayValue('bar') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '' } });

    expect(onChange).toHaveBeenCalled();
    const last = onChange.mock.calls.at(-1)![0];
    // Key 'foo' must be removed entirely, not set to ""
    expect(last).toEqual({});
    expect('foo' in last).toBe(false);
  });

  it('renders stale keys with warning + remove button', () => {
    const onChange = vi.fn();
    wrap(
      <ContractMappingTable
        contract={{ ...baseContract, inputs: [] }}
        values={{ outdatedParam: 'leftover' }}
        onChange={onChange}
        upstreamVars={[]}
        parentStepHandle="patchChild"
      />,
    );

    // Stale-keys header is rendered
    expect(screen.getByText(/Parameters being sent that are not in the contract/i)).toBeInTheDocument();
    expect(screen.getByText(/outdatedParam = leftover/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /remove/i }));
    expect(onChange).toHaveBeenCalledWith({});
  });

  it('renders multiple-sources warning when hasMultipleReturnDataNodes=true', () => {
    wrap(
      <ContractMappingTable
        contract={{
          ...baseContract,
          hasMultipleReturnDataNodes: true,
          outputs: [
            ...baseContract.outputs,
            { name: 'patched', source: 'multiple' },
          ],
        }}
        values={{}}
        onChange={vi.fn()}
        upstreamVars={[]}
        parentStepHandle="patchChild"
      />,
    );
    expect(screen.getByText(/multiple sources/i)).toBeInTheDocument();
  });

  it('shows noReturnDataHint when hasReturnData=false (only system outputs)', () => {
    wrap(
      <ContractMappingTable
        contract={{ ...baseContract, hasReturnData: false }}
        values={{}}
        onChange={vi.fn()}
        upstreamVars={[]}
        parentStepHandle="patchChild"
      />,
    );
    expect(screen.getByText(/Workflow has no returnData/i)).toBeInTheDocument();
  });

  it('renders conflict badge for inputs with hasConflict=true', () => {
    wrap(
      <ContractMappingTable
        contract={{
          ...baseContract,
          inputs: [{ name: 'x', type: 'string', required: false, default: null, description: null, hasConflict: true }],
        }}
        values={{}}
        onChange={vi.fn()}
        upstreamVars={[]}
        parentStepHandle="patchChild"
      />,
    );
    expect(screen.getByText(/^conflict$/i)).toBeInTheDocument();
  });

  it('renders output downstream-hint with parentStepHandle', () => {
    wrap(
      <ContractMappingTable
        contract={{
          ...baseContract,
          outputs: [{ name: '__executionId', source: 'system' }, { name: 'patched', source: 'single' }],
        }}
        values={{}}
        onChange={vi.fn()}
        upstreamVars={[]}
        parentStepHandle="myStep"
      />,
    );
    expect(screen.getByText(/\{\{myStep\.param\.patched\}\}/)).toBeInTheDocument();
    expect(screen.getByText(/\{\{myStep\.param\.__executionId\}\}/)).toBeInTheDocument();
  });
});
