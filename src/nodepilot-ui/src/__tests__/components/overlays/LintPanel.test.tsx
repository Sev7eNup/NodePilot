import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import type { Node } from '@xyflow/react';
import { LintPanel } from '../../../components/designer/overlays/LintPanel';
import type { LintIssue } from '../../../lib/workflowLint';

/**
 * LintPanel renders a list of lint issues with click-to-jump on each row. We pin:
 *   - error/warning counter in the header
 *   - clicking an issue calls onJump(nodeId) for node issues
 *   - clicking an issue calls onJumpEdge(edgeId) for edge issues
 *   - issues without a nodeId or edgeId render but are non-clickable (button disabled)
 *   - close button calls onClose
 */

function err(code: string, message: string, nodeId?: string, edgeId?: string): LintIssue {
  return { severity: 'error', code, message, nodeId, edgeId };
}
function warn(code: string, message: string, nodeId?: string, edgeId?: string): LintIssue {
  return { severity: 'warning', code, message, nodeId, edgeId };
}

function nodeWithLabel(id: string, label: string): Node {
  return { id, type: 'activity', position: { x: 0, y: 0 }, data: { label } };
}

describe('LintPanel', () => {
  it('rendersErrorAndWarningCount', () => {
    const result = {
      errors: [err('E1', 'broken')],
      warnings: [warn('W1', 'soft'), warn('W2', 'soft 2')],
    };
    render(<LintPanel result={result} nodes={[]} edges={[]} onJump={vi.fn()} onClose={vi.fn()} />);

    expect(screen.getByText(/1 errors · 2 warnings/)).toBeInTheDocument();
  });

  it('rendersAllIssuesInOrderErrorsThenWarnings', () => {
    const result = {
      errors: [err('E_FIRST', 'first error')],
      warnings: [warn('W_SECOND', 'second warning')],
    };
    render(<LintPanel result={result} nodes={[]} edges={[]} onJump={vi.fn()} onClose={vi.fn()} />);

    const codes = screen.getAllByText(/^E_FIRST|^W_SECOND/);
    expect(codes[0].textContent).toBe('E_FIRST');
    expect(codes[1].textContent).toBe('W_SECOND');
  });

  it('clickOnIssue_callsOnJumpWithNodeId', () => {
    const onJump = vi.fn();
    const result = { errors: [err('E1', 'broken', 'node-1')], warnings: [] };
    render(<LintPanel result={result} nodes={[]} edges={[]} onJump={onJump} onClose={vi.fn()} />);

    fireEvent.click(screen.getByText('broken'));
    expect(onJump).toHaveBeenCalledWith('node-1');
  });

  it('issueWithoutNodeIdOrEdgeId_buttonIsDisabled', () => {
    const onJump = vi.fn();
    const result = { errors: [err('E1', 'global error')], warnings: [] };
    render(<LintPanel result={result} nodes={[]} edges={[]} onJump={onJump} onClose={vi.fn()} />);

    const button = screen.getByText('global error').closest('button')!;
    expect(button).toBeDisabled();
  });

  it('clickOnEdgeIssue_callsOnJumpEdge', () => {
    const onJumpEdge = vi.fn();
    const result = { errors: [err('duplicate-edge', 'Doppelte Edge', undefined, 'e-1')], warnings: [] };
    render(<LintPanel result={result} nodes={[]} edges={[]} onJump={vi.fn()} onJumpEdge={onJumpEdge} onClose={vi.fn()} />);

    const button = screen.getByText('Doppelte Edge').closest('button')!;
    expect(button).not.toBeDisabled();
    fireEvent.click(button);
    expect(onJumpEdge).toHaveBeenCalledWith('e-1');
  });

  it('rendersNodeLabel_whenNodeIdResolvesToANode', () => {
    const node = nodeWithLabel('node-1', 'Check Disk');
    const result = { errors: [err('E1', 'broken', 'node-1')], warnings: [] };
    render(<LintPanel result={result} nodes={[node]} edges={[]} onJump={vi.fn()} onClose={vi.fn()} />);

    expect(screen.getByText(/Check Disk/)).toBeInTheDocument();
  });

  it('fallsBackToNodeId_whenNodeHasNoLabel', () => {
    const node: Node = { id: 'node-x', type: 'activity', position: { x: 0, y: 0 }, data: {} };
    const result = { errors: [err('E1', 'broken', 'node-x')], warnings: [] };
    render(<LintPanel result={result} nodes={[node]} edges={[]} onJump={vi.fn()} onClose={vi.fn()} />);

    expect(screen.getByText(/node-x/)).toBeInTheDocument();
  });

  it('closeButton_callsOnClose', () => {
    const onClose = vi.fn();
    const result = { errors: [], warnings: [] };
    render(<LintPanel result={result} nodes={[]} edges={[]} onJump={vi.fn()} onClose={onClose} />);

    fireEvent.click(screen.getByLabelText('Close'));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('emptyResult_rendersZeroCounts', () => {
    const result = { errors: [], warnings: [] };
    render(<LintPanel result={result} nodes={[]} edges={[]} onJump={vi.fn()} onClose={vi.fn()} />);

    expect(screen.getByText(/0 errors · 0 warnings/)).toBeInTheDocument();
  });

  it('errorIconUsesErrorColorWhenErrorsPresent', () => {
    const result = { errors: [err('E1', 'broken')], warnings: [] };
    const { container } = render(
      <LintPanel result={result} nodes={[]} edges={[]} onJump={vi.fn()} onClose={vi.fn()} />
    );
    // The header AlertTriangle icon switches to text-error class when errors > 0.
    expect(container.querySelector('.text-error')).not.toBeNull();
  });
});
