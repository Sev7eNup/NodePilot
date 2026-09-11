import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { JsonPathTree } from '../../../../components/designer/properties/JsonPathTree';

/**
 * The tree is one component per row, each with its own expansion state. A step output holding a
 * few hundred records used to mount the whole thing at once, which made a single click on a
 * chevron stall. Children are now mounted in batches — nothing is hidden, and every path stays
 * reachable.
 */
function records(count: number) {
  return Array.from({ length: count }, (_, i) => ({ name: `host-${i}`, ok: true }));
}

describe('JsonPathTree', () => {
  it('mounts only the first batch of children and offers the rest', () => {
    render(<JsonPathTree value={records(200)} onPick={vi.fn()} />);

    // Array indices are the child names at depth 1.
    expect(screen.getByText('49')).toBeInTheDocument();
    expect(screen.queryByText('50')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /show 150 more/i })).toBeInTheDocument();
  });

  it('reveals the next batch on each click and drops the button at the end', () => {
    render(<JsonPathTree value={records(60)} onPick={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: /show 10 more/i }));

    expect(screen.getByText('59')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /show .* more/i })).not.toBeInTheDocument();
  });

  it('shows no batching control for a small object', () => {
    render(<JsonPathTree value={{ a: 1, b: 2 }} onPick={vi.fn()} />);

    expect(screen.getByText('a')).toBeInTheDocument();
    expect(screen.getByText('b')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /show .* more/i })).not.toBeInTheDocument();
  });

  it('still hands the full path of a nested value to onPick', () => {
    const onPick = vi.fn();
    render(<JsonPathTree value={{ hosts: [{ name: 'srv-1' }] }} onPick={onPick} />);

    // Depth 0 and 1 start expanded; the array element at depth 2 is the only collapsed node.
    fireEvent.click(screen.getByRole('button', { name: /expand/i }));
    fireEvent.click(screen.getByText('name'));

    expect(onPick).toHaveBeenCalledWith('$.hosts[0].name');
  });

  it('honours maxDepth by making deeper nodes non-expandable', () => {
    render(<JsonPathTree value={{ a: { b: { c: 1 } } }} onPick={vi.fn()} maxDepth={1} />);

    // Root expands, 'a' does not — so 'b' is never mounted.
    expect(screen.getByText('a')).toBeInTheDocument();
    expect(screen.queryByText('b')).not.toBeInTheDocument();
  });
});
