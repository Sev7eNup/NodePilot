import { describe, it, expect } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useBulkSelection } from '../../hooks/useBulkSelection';

type Row = { id: string };
const rows = (...ids: string[]): Row[] => ids.map((id) => ({ id }));
const key = (r: Row) => r.id;

function setup(initial: Row[] = rows('a', 'b', 'c', 'd')) {
  return renderHook(({ items }: { items: Row[] }) => useBulkSelection(items, key), {
    initialProps: { items: initial },
  });
}

describe('useBulkSelection', () => {
  it('starts empty', () => {
    const { result } = setup();
    expect(result.current.selectedCount).toBe(0);
    expect(result.current.allSelected).toBe(false);
    expect(result.current.someSelected).toBe(false);
  });

  it('toggle selects and deselects a single row', () => {
    const { result } = setup();
    act(() => result.current.toggle('b'));
    expect(result.current.isSelected('b')).toBe(true);
    expect(result.current.selectedCount).toBe(1);
    expect(result.current.someSelected).toBe(true);

    act(() => result.current.toggle('b'));
    expect(result.current.isSelected('b')).toBe(false);
    expect(result.current.selectedCount).toBe(0);
  });

  it('selectedItems follows the item order, not the click order', () => {
    const { result } = setup();
    act(() => result.current.toggle('c'));
    act(() => result.current.toggle('a'));
    expect(result.current.selectedItems.map(key)).toEqual(['a', 'c']);
  });

  it('shift-toggle selects the range from the previous anchor', () => {
    const { result } = setup();
    act(() => result.current.toggle('a'));
    act(() => result.current.toggle('d', true));
    expect(result.current.selectedItems.map(key)).toEqual(['a', 'b', 'c', 'd']);
    expect(result.current.allSelected).toBe(true);
  });

  it('shift-toggle works backwards and deselects a range', () => {
    const { result } = setup();
    act(() => result.current.toggleAll());
    // Anchor on 'd', then shift-toggle back to 'b'. 'd' is selected, so the range clears.
    act(() => result.current.toggle('d'));
    act(() => result.current.toggle('b', true));
    expect(result.current.selectedItems.map(key)).toEqual(['a']);
  });

  it('shift without a prior anchor behaves like a plain toggle', () => {
    const { result } = setup();
    act(() => result.current.toggle('c', true));
    expect(result.current.selectedItems.map(key)).toEqual(['c']);
  });

  it('toggleAll selects everything, then clears when all are already selected', () => {
    const { result } = setup();
    act(() => result.current.toggleAll());
    expect(result.current.selectedCount).toBe(4);
    expect(result.current.allSelected).toBe(true);
    expect(result.current.someSelected).toBe(false);

    act(() => result.current.toggleAll());
    expect(result.current.selectedCount).toBe(0);
  });

  it('clear empties the selection', () => {
    const { result } = setup();
    act(() => result.current.toggleAll());
    act(() => result.current.clear());
    expect(result.current.selectedCount).toBe(0);
  });

  it('retain narrows the selection to the given ids', () => {
    const { result } = setup();
    act(() => result.current.toggleAll());
    act(() => result.current.retain(['b', 'd']));
    expect(result.current.selectedItems.map(key)).toEqual(['b', 'd']);
  });

  it('retain ignores ids that were never selected', () => {
    const { result } = setup();
    act(() => result.current.toggle('a'));
    act(() => result.current.retain(['a', 'zzz']));
    expect(result.current.selectedItems.map(key)).toEqual(['a']);
  });

  // Rows that leave the list must drop out of the selection, or the count and the next bulk
  // action would still include items that no longer exist.
  it('prunes ids that disappear from the item list', () => {
    const { result, rerender } = setup();
    act(() => result.current.toggleAll());
    expect(result.current.selectedCount).toBe(4);

    rerender({ items: rows('a', 'c') });
    expect(result.current.selectedItems.map(key)).toEqual(['a', 'c']);
    expect(result.current.allSelected).toBe(true);
  });

  it('drops everything when the list becomes empty', () => {
    const { result, rerender } = setup();
    act(() => result.current.toggleAll());
    rerender({ items: [] });
    expect(result.current.selectedCount).toBe(0);
    expect(result.current.allSelected).toBe(false);
  });

  it('keeps the selection when the same items are re-supplied in a new array', () => {
    const { result, rerender } = setup();
    act(() => result.current.toggle('b'));
    rerender({ items: rows('a', 'b', 'c', 'd') });
    expect(result.current.isSelected('b')).toBe(true);
  });

  // `selectedCount` must match `selectedItems`, which is what every bulk action receives. If it
  // reported the raw id set instead, a row that left the list would keep the bar open on a
  // selection the action cannot see.
  it('counts only rows that are still on screen', () => {
    const { result, rerender } = setup();
    act(() => result.current.toggle('a'));
    act(() => result.current.toggle('d'));
    expect(result.current.selectedCount).toBe(2);

    // 'd' leaves the rendered list, for example through a collapsed branch, a filter or a refetch.
    rerender({ items: rows('a', 'b', 'c') });

    expect(result.current.selectedCount).toBe(1);
    expect(result.current.selectedItems.map(key)).toEqual(['a']);
  });

  it('selectedCount never exceeds what a bulk action would receive', () => {
    const { result, rerender } = setup();
    act(() => result.current.toggleAll());
    rerender({ items: rows('b') });

    expect(result.current.selectedCount).toBe(result.current.selectedItems.length);
  });

  it('someSelected follows the on-screen count, not the raw set', () => {
    const { result, rerender } = setup();
    act(() => result.current.toggle('d'));
    rerender({ items: rows('a', 'b', 'c') });

    expect(result.current.someSelected).toBe(false);
  });

  it('allSelected is false for an empty list', () => {
    const { result } = setup([]);
    expect(result.current.allSelected).toBe(false);
  });
});
