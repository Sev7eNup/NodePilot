import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import type { Node } from '@xyflow/react';
import { BulkEditPanel } from '../../../components/designer/BulkEditPanel';
import type { ManagedMachine } from '../../../types/api';

/**
 * BulkEditPanel is shown when ≥2 activity nodes are selected. Each field has its own
 * Apply button (vs. an all-or-nothing form), so we pin:
 *   - Header counts activity vs. non-activity correctly
 *   - Apply on each field calls onApply with the right (patch, configPatch?) shape
 *   - Apply buttons stay disabled until a value is entered
 *   - Close button calls onClose
 */

function activityNode(id: string): Node {
  return { id, type: 'activity', position: { x: 0, y: 0 }, data: { label: id } };
}

function junctionNode(id: string): Node {
  return { id, type: 'junction', position: { x: 0, y: 0 }, data: { label: id } };
}

function machine(id: string, name: string, hostname: string): ManagedMachine {
  return {
    id, name, hostname, winRmPort: 5985, useSsl: false,
    defaultCredentialId: null, tags: null,
    lastConnectivityCheck: null, isReachable: true,
    usedByWorkflowCount: 0, recentStepCount: 0,
    recentFailedStepCount: 0, activeRunCount: 0,
  };
}

describe('BulkEditPanel', () => {
  it('rendersHeaderWithActivityCountOnly_whenAllSelectedAreActivities', () => {
    render(
      <BulkEditPanel
        selectedNodes={[activityNode('a'), activityNode('b')]}
        machines={[]}
        onApply={vi.fn()}
        onClose={vi.fn()}
        width={320}
      />
    );
    expect(screen.getByText(/2 activities selected/)).toBeInTheDocument();
    expect(screen.queryByText(/other/)).not.toBeInTheDocument();
  });

  it('rendersHeaderWithSingularActivityLabel_whenExactlyOneActivity', () => {
    render(
      <BulkEditPanel
        selectedNodes={[activityNode('a'), junctionNode('j')]}
        machines={[]}
        onApply={vi.fn()}
        onClose={vi.fn()}
        width={320}
      />
    );
    // 1 activity, +1 other (the junction)
    expect(screen.getByText(/1 activity \(\+ 1 other\) selected/)).toBeInTheDocument();
  });

  it('rendersHeaderWithMixedCounts', () => {
    render(
      <BulkEditPanel
        selectedNodes={[activityNode('a'), activityNode('b'), junctionNode('j1'), junctionNode('j2')]}
        machines={[]}
        onApply={vi.fn()}
        onClose={vi.fn()}
        width={320}
      />
    );
    expect(screen.getByText(/2 activities \(\+ 2 other\) selected/)).toBeInTheDocument();
  });

  it('targetMachineDropdown_listsMachinesWithHostnameSuffix', () => {
    render(
      <BulkEditPanel
        selectedNodes={[activityNode('a'), activityNode('b')]}
        machines={[machine('m1', 'Web-01', 'web01.lab')]}
        onApply={vi.fn()}
        onClose={vi.fn()}
        width={320}
      />
    );
    expect(screen.getByText('Web-01 (web01.lab)')).toBeInTheDocument();
  });

  it('selectMachine_andClickApply_callsOnApplyWithTargetMachineId', () => {
    const onApply = vi.fn();
    render(
      <BulkEditPanel
        selectedNodes={[activityNode('a'), activityNode('b')]}
        machines={[machine('m1', 'Web-01', 'web01.lab'), machine('m2', 'DB-01', 'db01.lab')]}
        onApply={onApply}
        onClose={vi.fn()}
        width={320}
      />
    );

    // The "Target Machine" select is the first <select>
    const machineSelect = screen.getAllByRole('combobox')[0] as HTMLSelectElement;
    fireEvent.change(machineSelect, { target: { value: 'm2' } });
    fireEvent.click(screen.getByText('Apply machine'));

    expect(onApply).toHaveBeenCalledWith(['a', 'b'], { targetMachineId: 'm2' }, undefined);
  });

  it('applyMachineButton_disabledUntilMachineSelected', () => {
    render(
      <BulkEditPanel
        selectedNodes={[activityNode('a'), activityNode('b')]}
        machines={[machine('m1', 'Web-01', 'web01.lab')]}
        onApply={vi.fn()}
        onClose={vi.fn()}
        width={320}
      />
    );
    expect(screen.getByText('Apply machine')).toBeDisabled();
  });

  it('disableAllOption_emitsDisabledTrue', () => {
    const onApply = vi.fn();
    render(
      <BulkEditPanel
        selectedNodes={[activityNode('a'), activityNode('b')]}
        machines={[]}
        onApply={onApply}
        onClose={vi.fn()}
        width={320}
      />
    );

    // Second <select> = Enable/Disable
    const stateSelect = screen.getAllByRole('combobox')[1] as HTMLSelectElement;
    fireEvent.change(stateSelect, { target: { value: 'true' } });
    fireEvent.click(screen.getByText('Apply state'));

    expect(onApply).toHaveBeenCalledWith(['a', 'b'], { disabled: true }, undefined);
  });

  it('enableAllOption_emitsDisabledFalse', () => {
    const onApply = vi.fn();
    render(
      <BulkEditPanel
        selectedNodes={[activityNode('a'), activityNode('b')]}
        machines={[]}
        onApply={onApply}
        onClose={vi.fn()}
        width={320}
      />
    );

    const stateSelect = screen.getAllByRole('combobox')[1] as HTMLSelectElement;
    fireEvent.change(stateSelect, { target: { value: 'false' } });
    fireEvent.click(screen.getByText('Apply state'));

    expect(onApply).toHaveBeenCalledWith(['a', 'b'], { disabled: false }, undefined);
  });

  it('timeoutInput_emitsConfigPatchWithNumericTimeout', () => {
    const onApply = vi.fn();
    render(
      <BulkEditPanel
        selectedNodes={[activityNode('a'), activityNode('b')]}
        machines={[]}
        onApply={onApply}
        onClose={vi.fn()}
        width={320}
      />
    );

    const timeoutInput = screen.getByPlaceholderText('e.g. 60');
    fireEvent.change(timeoutInput, { target: { value: '120' } });
    fireEvent.click(screen.getByText('Apply timeout'));

    // patch is empty {}, configPatch contains the timeout as a NUMBER (not string).
    expect(onApply).toHaveBeenCalledWith(['a', 'b'], {}, { timeoutSeconds: 120 });
    const arg = onApply.mock.calls[0][2] as { timeoutSeconds: unknown };
    expect(typeof arg.timeoutSeconds).toBe('number');
  });

  it('applyTimeoutButton_disabledUntilNumberEntered', () => {
    render(
      <BulkEditPanel
        selectedNodes={[activityNode('a')]}
        machines={[]}
        onApply={vi.fn()}
        onClose={vi.fn()}
        width={320}
      />
    );
    expect(screen.getByText('Apply timeout')).toBeDisabled();
  });

  it('retryFields_emitFullPolicyWithDefaultDelays', () => {
    const onApply = vi.fn();
    render(
      <BulkEditPanel
        selectedNodes={[activityNode('a'), activityNode('b')]}
        machines={[]}
        onApply={onApply}
        onClose={vi.fn()}
        width={320}
      />
    );

    fireEvent.change(screen.getByPlaceholderText('attempts'), { target: { value: '3' } });
    // The third <select> overall is the retry-backoff (after target-machine + state).
    const backoffSelect = screen.getAllByRole('combobox')[2] as HTMLSelectElement;
    fireEvent.change(backoffSelect, { target: { value: 'exponential' } });
    fireEvent.click(screen.getByText('Apply retry'));

    expect(onApply).toHaveBeenCalledWith(['a', 'b'], {}, {
      retry: {
        maxAttempts: 3,
        backoff: 'exponential',
        initialDelayMs: 1000,
        maxDelayMs: 30000,
      },
    });
  });

  it('applyRetryButton_disabledIfBackoffMissing', () => {
    render(
      <BulkEditPanel
        selectedNodes={[activityNode('a')]}
        machines={[]}
        onApply={vi.fn()}
        onClose={vi.fn()}
        width={320}
      />
    );

    fireEvent.change(screen.getByPlaceholderText('attempts'), { target: { value: '3' } });
    expect(screen.getByText('Apply retry')).toBeDisabled();
  });

  it('closeButton_callsOnClose', () => {
    const onClose = vi.fn();
    const { container } = render(
      <BulkEditPanel
        selectedNodes={[activityNode('a'), activityNode('b')]}
        machines={[]}
        onApply={vi.fn()}
        onClose={onClose}
        width={320}
      />
    );

    // The X close button is the only button in the header (no name); query by role.
    const closeButton = container.querySelector('button.text-on-surface-variant') as HTMLButtonElement;
    fireEvent.click(closeButton);
    expect(onClose).toHaveBeenCalledOnce();
  });
});
