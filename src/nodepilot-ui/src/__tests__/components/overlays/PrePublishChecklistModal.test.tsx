import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import type { Node } from '@xyflow/react';
import { PrePublishChecklistModal } from '../../../components/designer/overlays/PrePublishChecklistModal';
import type { LintIssue, LintResult } from '../../../lib/workflowLint';

function err(code: string, message: string, nodeId?: string): LintIssue {
  return { severity: 'error', code, message, nodeId };
}
function warn(code: string, message: string, nodeId?: string): LintIssue {
  return { severity: 'warning', code, message, nodeId };
}

function nodeWithLabel(id: string, label: string): Node {
  return { id, type: 'activity', position: { x: 0, y: 0 }, data: { label } } as unknown as Node;
}

const cleanResult: LintResult = { errors: [], warnings: [] };

function renderModal(overrides: Partial<{
  result: LintResult;
  nodes: Node[];
  isPending: boolean;
  onConfirm: () => void;
  onCancel: () => void;
  onJumpToNode: (id: string) => void;
}> = {}) {
  const onConfirm = overrides.onConfirm ?? vi.fn();
  const onCancel = overrides.onCancel ?? vi.fn();
  const onJumpToNode = overrides.onJumpToNode ?? vi.fn();
  render(
    <PrePublishChecklistModal
      result={overrides.result ?? cleanResult}
      nodes={overrides.nodes ?? []}
      workflowName="Demo Workflow"
      isPending={overrides.isPending ?? false}
      onConfirm={onConfirm}
      onCancel={onCancel}
      onJumpToNode={onJumpToNode}
    />,
  );
  return { onConfirm, onCancel, onJumpToNode };
}

describe('PrePublishChecklistModal — clean state', () => {
  it('shows the empty-state copy when no issues exist', () => {
    renderModal();
    expect(screen.getByText(/All clear/i)).toBeInTheDocument();
  });

  it('confirm button is enabled and not labelled "anyway"', () => {
    renderModal();
    const confirm = screen.getByTestId('pre-publish-confirm') as HTMLButtonElement;
    expect(confirm).not.toBeDisabled();
    expect(confirm.textContent).toMatch(/Publish/);
    expect(confirm.textContent).not.toMatch(/anyway/);
  });
});

describe('PrePublishChecklistModal — warnings only', () => {
  it('lists each warning row', () => {
    renderModal({
      result: {
        errors: [],
        warnings: [warn('no-description', 'Workflow hat keine Beschreibung'), warn('orphan-root', 'Step ohne Trigger')],
      },
    });
    expect(screen.getByText(/keine Beschreibung/)).toBeInTheDocument();
    expect(screen.getByText(/Step ohne Trigger/)).toBeInTheDocument();
  });

  it('confirm button is enabled and labelled "Publish anyway"', () => {
    renderModal({
      result: { errors: [], warnings: [warn('no-description', 'fehlt')] },
    });
    const confirm = screen.getByTestId('pre-publish-confirm') as HTMLButtonElement;
    expect(confirm).not.toBeDisabled();
    expect(confirm.textContent).toMatch(/Publish anyway/);
  });

  it('confirm callback fires when clicked with warnings only', () => {
    const { onConfirm } = renderModal({
      result: { errors: [], warnings: [warn('w', 'soft')] },
    });
    fireEvent.click(screen.getByTestId('pre-publish-confirm'));
    expect(onConfirm).toHaveBeenCalledOnce();
  });
});

describe('PrePublishChecklistModal — errors present', () => {
  it('disables the confirm button', () => {
    renderModal({
      result: { errors: [err('missing-machine', 'Step ohne Maschine')], warnings: [] },
    });
    const confirm = screen.getByTestId('pre-publish-confirm') as HTMLButtonElement;
    expect(confirm).toBeDisabled();
  });

  it('shows the "Errors must be resolved" footer copy', () => {
    renderModal({
      result: { errors: [err('e1', 'broken')], warnings: [] },
    });
    expect(screen.getByText(/Errors must be resolved/)).toBeInTheDocument();
  });

  it('confirm callback does NOT fire even when clicked', () => {
    const { onConfirm } = renderModal({
      result: { errors: [err('e1', 'broken')], warnings: [] },
    });
    fireEvent.click(screen.getByTestId('pre-publish-confirm'));
    expect(onConfirm).not.toHaveBeenCalled();
  });
});

describe('PrePublishChecklistModal — interactivity', () => {
  it('cancel button fires onCancel', () => {
    const { onCancel } = renderModal();
    fireEvent.click(screen.getByText('Cancel'));
    expect(onCancel).toHaveBeenCalledOnce();
  });

  it('header close button fires onCancel', () => {
    const { onCancel } = renderModal();
    fireEvent.click(screen.getByLabelText('Close'));
    expect(onCancel).toHaveBeenCalledOnce();
  });

  it('clicking on a node-bound row fires onJumpToNode with the right id', () => {
    const onJumpToNode = vi.fn();
    renderModal({
      result: { errors: [err('e1', 'kaputt', 'step-42')], warnings: [] },
      nodes: [nodeWithLabel('step-42', 'Check Disk')],
      onJumpToNode,
    });
    fireEvent.click(screen.getByText('kaputt'));
    expect(onJumpToNode).toHaveBeenCalledWith('step-42');
  });

  it('rows without a nodeId render but stay non-clickable', () => {
    const onJumpToNode = vi.fn();
    renderModal({
      result: { errors: [err('global', 'no trigger anywhere')], warnings: [] },
      onJumpToNode,
    });
    const row = screen.getByText('no trigger anywhere').closest('button')!;
    expect(row).toBeDisabled();
  });

  it('shows pending state copy on confirm button while publish is in flight', () => {
    renderModal({
      result: { errors: [], warnings: [warn('w', 'soft')] },
      isPending: true,
    });
    const confirm = screen.getByTestId('pre-publish-confirm') as HTMLButtonElement;
    expect(confirm).toBeDisabled();
    expect(confirm.textContent).toMatch(/Publishing…/);
  });

  it('shows the workflow name in the header', () => {
    renderModal();
    expect(screen.getByText(/Demo Workflow/)).toBeInTheDocument();
  });
});

describe('PrePublishChecklistModal — counter strip', () => {
  it('shows the right error + warning counts', () => {
    renderModal({
      result: {
        errors: [err('e1', 'a'), err('e2', 'b')],
        warnings: [warn('w1', 'c'), warn('w2', 'd'), warn('w3', 'e')],
      },
    });
    // Counter blocks render the count as the only large number — easier to assert by role+name.
    const errorsBlock = screen.getByText('Errors').parentElement!;
    const warningsBlock = screen.getByText('Warnings').parentElement!;
    expect(errorsBlock.textContent).toMatch(/2/);
    expect(warningsBlock.textContent).toMatch(/3/);
  });
});
