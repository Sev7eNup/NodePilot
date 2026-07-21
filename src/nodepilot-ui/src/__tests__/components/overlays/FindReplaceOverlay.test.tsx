import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';
import { FindReplaceOverlay } from '../../../components/designer/overlays/FindReplaceOverlay';

/**
 * FindReplaceOverlay is a thin UI on top of lib/findReplace. We don't re-test the matching
 * algorithm (covered by findReplace.test.ts) — we pin the wiring:
 *   - empty search → "enter a search term" hint
 *   - non-empty search with results → match list rendered with kind-badges
 *   - "All" button → calls onApply with the full transformed graph and closes
 *   - Esc / close button → onClose
 *   - scope checkboxes → toggling re-evaluates matches
 */

function node(id: string, label: string, config: Record<string, unknown> = {}): Node {
  return {
    id,
    type: 'activity',
    position: { x: 0, y: 0 },
    data: { label, activityType: 'runScript', config },
  };
}

function edge(id: string, source: string, target: string, label?: string): Edge {
  return { id, source, target, type: 'labeled', data: label ? { label } : {} };
}

describe('FindReplaceOverlay', () => {
  it('emptyQuery_showsHelpHint', () => {
    render(
      <FindReplaceOverlay nodes={[node('a', 'Foo')]} edges={[]} onApply={vi.fn()} onClose={vi.fn()} />
    );
    expect(screen.getByText(/Enter a search term/i)).toBeInTheDocument();
  });

  it('queryWithMatches_listsMatchCount', () => {
    render(
      <FindReplaceOverlay
        nodes={[node('a', 'Foo step'), node('b', 'Bar step')]}
        edges={[]}
        onApply={vi.fn()}
        onClose={vi.fn()}
      />
    );

    fireEvent.change(screen.getByPlaceholderText(/Find/i), { target: { value: 'step' } });

    expect(screen.getByText(/2 matches/i)).toBeInTheDocument();
    // Each match renders the label twice (displayName + contextSnippet) — getAllByText.
    expect(screen.getAllByText('Foo step').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Bar step').length).toBeGreaterThanOrEqual(1);
  });

  it('queryWithoutMatches_showsNoMatchesMessage', () => {
    render(
      <FindReplaceOverlay nodes={[node('a', 'Foo')]} edges={[]} onApply={vi.fn()} onClose={vi.fn()} />
    );

    fireEvent.change(screen.getByPlaceholderText(/Find/i), { target: { value: 'zzz' } });

    expect(screen.getByText(/No matches found/i)).toBeInTheDocument();
  });

  it('replaceAllButton_callsOnApplyAndClose', () => {
    const onApply = vi.fn();
    const onClose = vi.fn();
    render(
      <FindReplaceOverlay
        nodes={[node('a', 'Foo')]}
        edges={[]}
        onApply={onApply}
        onClose={onClose}
      />
    );

    fireEvent.change(screen.getByPlaceholderText(/Find/i), { target: { value: 'Foo' } });
    fireEvent.change(screen.getByPlaceholderText(/Replace with/i), { target: { value: 'Bar' } });
    fireEvent.click(screen.getByTitle(/Replace all/i));

    expect(onApply).toHaveBeenCalledOnce();
    const [newNodes] = onApply.mock.calls[0];
    expect((newNodes[0].data as { label: string }).label).toBe('Bar');
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('replaceOneButton_callsOnApplyButDoesNotClose', () => {
    const onApply = vi.fn();
    const onClose = vi.fn();
    render(
      <FindReplaceOverlay
        nodes={[node('a', 'Foo'), node('b', 'Foo')]}
        edges={[]}
        onApply={onApply}
        onClose={onClose}
      />
    );

    fireEvent.change(screen.getByPlaceholderText(/Find/i), { target: { value: 'Foo' } });
    fireEvent.change(screen.getByPlaceholderText(/Replace with/i), { target: { value: 'Bar' } });
    fireEvent.click(screen.getByTitle(/Replace current match/i));

    expect(onApply).toHaveBeenCalledOnce();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('replaceButtonsDisabledWhenNoMatches', () => {
    render(
      <FindReplaceOverlay nodes={[node('a', 'Foo')]} edges={[]} onApply={vi.fn()} onClose={vi.fn()} />
    );

    fireEvent.change(screen.getByPlaceholderText(/Find/i), { target: { value: 'zzz' } });

    expect(screen.getByTitle(/Replace current match/i)).toBeDisabled();
  });

  it('escapeOnSearchField_callsOnClose', () => {
    const onClose = vi.fn();
    render(
      <FindReplaceOverlay nodes={[]} edges={[]} onApply={vi.fn()} onClose={onClose} />
    );

    fireEvent.keyDown(screen.getByPlaceholderText(/Find/i), { key: 'Escape' });
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('closeButton_callsOnClose', () => {
    const onClose = vi.fn();
    render(
      <FindReplaceOverlay nodes={[]} edges={[]} onApply={vi.fn()} onClose={onClose} />
    );

    fireEvent.click(screen.getByLabelText('Close'));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('togglingNodeLabelsScope_excludesNodeMatches', () => {
    render(
      <FindReplaceOverlay
        nodes={[node('a', 'Foo'), node('b', 'Bar', { script: 'Foo' })]}
        edges={[]}
        onApply={vi.fn()}
        onClose={vi.fn()}
      />
    );

    fireEvent.change(screen.getByPlaceholderText(/Find/i), { target: { value: 'Foo' } });

    expect(screen.getByText(/2 matches/i)).toBeInTheDocument();

    // Uncheck node labels
    fireEvent.click(screen.getByLabelText(/Node labels/i));

    expect(screen.getByText(/1 match/i)).toBeInTheDocument();
  });

  it('edgeLabelMatch_rendersWithEdgeBadge', () => {
    render(
      <FindReplaceOverlay
        nodes={[]}
        edges={[edge('e1', 'a', 'b', 'On Foo')]}
        onApply={vi.fn()}
        onClose={vi.fn()}
      />
    );

    fireEvent.change(screen.getByPlaceholderText(/Find/i), { target: { value: 'Foo' } });

    expect(screen.getByText('Edge')).toBeInTheDocument();
    expect(screen.getByText('On Foo')).toBeInTheDocument();
  });

  it('clickOnBackdrop_closes', () => {
    const onClose = vi.fn();
    render(
      <FindReplaceOverlay nodes={[]} edges={[]} onApply={vi.fn()} onClose={onClose} />
    );

    // Backdrop is the outermost div with onClick={onClose}
    fireEvent.click(screen.getByPlaceholderText(/Find/i).closest('.fixed')!);
    expect(onClose).toHaveBeenCalledOnce();
  });
});
