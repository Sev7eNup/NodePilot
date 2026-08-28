import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ConcurrencyLimitDialog } from '../../components/workflows/ConcurrencyLimitDialog';
import type { Workflow } from '../../types/api';

const workflow = (maxConcurrentExecutions: number | null): Workflow => ({
  id: 'wf-1',
  name: 'Nightly Sync',
  description: null,
  definitionJson: '{}',
  version: 1,
  isEnabled: true,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  createdBy: null,
  updatedBy: null,
  maxConcurrentExecutions,
});

describe('ConcurrencyLimitDialog', () => {
  it('starts unlimited when the workflow has no limit', () => {
    render(
      <ConcurrencyLimitDialog
        workflow={workflow(null)}
        onClose={() => {}}
        onSave={() => {}}
        isSaving={false}
      />,
    );

    expect(screen.getByRole('checkbox')).toBeChecked();
    expect(screen.getByRole('spinbutton')).toBeDisabled();
  });

  it('pre-fills the existing limit', () => {
    render(
      <ConcurrencyLimitDialog
        workflow={workflow(5)}
        onClose={() => {}}
        onSave={() => {}}
        isSaving={false}
      />,
    );

    expect(screen.getByRole('checkbox')).not.toBeChecked();
    expect(screen.getByRole('spinbutton')).toHaveValue(5);
  });

  it('saves the entered limit', () => {
    const onSave = vi.fn();
    render(
      <ConcurrencyLimitDialog
        workflow={workflow(5)}
        onClose={() => {}}
        onSave={onSave}
        isSaving={false}
      />,
    );

    fireEvent.change(screen.getByRole('spinbutton'), { target: { value: '8' } });
    fireEvent.click(screen.getByRole('button', { name: /save|speichern/i }));

    expect(onSave).toHaveBeenCalledWith(8);
  });

  it('saves null when unlimited is ticked', () => {
    const onSave = vi.fn();
    render(
      <ConcurrencyLimitDialog
        workflow={workflow(5)}
        onClose={() => {}}
        onSave={onSave}
        isSaving={false}
      />,
    );

    fireEvent.click(screen.getByRole('checkbox'));
    fireEvent.click(screen.getByRole('button', { name: /save|speichern/i }));

    expect(onSave).toHaveBeenCalledWith(null);
  });

  it('blocks values outside 1..1000', () => {
    const onSave = vi.fn();
    render(
      <ConcurrencyLimitDialog
        workflow={workflow(5)}
        onClose={() => {}}
        onSave={onSave}
        isSaving={false}
      />,
    );
    const input = screen.getByRole('spinbutton');
    const save = screen.getByRole('button', { name: /save|speichern/i });

    fireEvent.change(input, { target: { value: '0' } });
    expect(save).toBeDisabled();

    fireEvent.change(input, { target: { value: '1001' } });
    expect(save).toBeDisabled();

    fireEvent.change(input, { target: { value: '1000' } });
    expect(save).not.toBeDisabled();
    fireEvent.click(save);
    expect(onSave).toHaveBeenCalledWith(1000);
  });
});
