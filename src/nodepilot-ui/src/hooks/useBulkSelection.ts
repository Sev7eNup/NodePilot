import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

export interface BulkSelection<T> {
  /** The raw id set. Use `isSelected` instead of reading it directly in render paths. */
  selectedIds: ReadonlySet<string>;
  /** How many selected rows are on screen, that is `selectedItems.length`. Gate bulk controls
   *  on this, never on `selectedIds.size`: only these rows reach a bulk action. */
  selectedCount: number;
  isSelected: (id: string) => boolean;
  /** The currently selected items, in the order of the `items` array handed to the hook. */
  selectedItems: T[];
  /** Toggles one row. With `shiftKey` the range from the previous click's anchor takes the
   *  toggled row's new state, the standard file-manager gesture. */
  toggle: (id: string, shiftKey?: boolean) => void;
  /** Selects every visible item, or clears the selection when everything is already selected. */
  toggleAll: () => void;
  clear: () => void;
/** Narrows the selection to the given ids, retaining rows where a bulk run failed. */
  retain: (ids: readonly string[]) => void;
  allSelected: boolean;
  someSelected: boolean;
}

/**
 * Row multi-select for list pages: a `Set<string>` of ids plus the select-all and shift-range
 * gestures a table is expected to have.
 *
 * `items` must be the list as rendered (already filtered and sorted). Two behaviours depend on
 * that: shift-range uses the rendered order, and the effect below prunes ids that are no longer
 * in the list. The prune keeps the selection honest across a folder switch, a refetch and a
 * bulk delete, after which the removed ids must not linger in the count or a follow-up action.
 */
export function useBulkSelection<T>(items: T[], getId: (item: T) => string): BulkSelection<T> {
  const [selectedIds, setSelectedIds] = useState<ReadonlySet<string>>(() => new Set<string>());
  // Anchor for shift-range selection: the id of the last row toggled without shift.
  const anchorRef = useRef<string | null>(null);

  const ids = useMemo(() => items.map(getId), [items, getId]);
  // Stable string key so the prune effect fires on membership changes, not on every render
  // (`items` is a fresh array whenever the page re-sorts or refetches).
  const idsKey = ids.join(',');

  useEffect(() => {
    const visible = new Set(idsKey ? idsKey.split(',') : []);
    setSelectedIds((prev) => {
      if (prev.size === 0) return prev;
      const next = new Set([...prev].filter((id) => visible.has(id)));
      // Returning `prev` unchanged avoids a needless re-render (and a render loop).
      return next.size === prev.size ? prev : next;
    });
  }, [idsKey]);

  const toggle = useCallback((id: string, shiftKey = false) => {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      const select = !next.has(id);
      const anchor = anchorRef.current;

      if (shiftKey && anchor !== null && anchor !== id) {
        const from = ids.indexOf(anchor);
        const to = ids.indexOf(id);
        if (from !== -1 && to !== -1) {
          const [lo, hi] = from < to ? [from, to] : [to, from];
          for (let i = lo; i <= hi; i++) {
            if (select) next.add(ids[i]);
            else next.delete(ids[i]);
          }
          return next;
        }
      }

      if (select) next.add(id);
      else next.delete(id);
      anchorRef.current = id;
      return next;
    });
  }, [ids]);

  const toggleAll = useCallback(() => {
    setSelectedIds((prev) => {
      const allIn = ids.length > 0 && ids.every((id) => prev.has(id));
      anchorRef.current = null;
      return allIn ? new Set<string>() : new Set(ids);
    });
  }, [ids]);

  const clear = useCallback(() => {
    anchorRef.current = null;
    setSelectedIds((prev) => (prev.size === 0 ? prev : new Set<string>()));
  }, []);

  const retain = useCallback((keep: readonly string[]) => {
    const keepSet = new Set(keep);
    anchorRef.current = null;
    setSelectedIds((prev) => new Set([...prev].filter((id) => keepSet.has(id))));
  }, []);

  const isSelected = useCallback((id: string) => selectedIds.has(id), [selectedIds]);
  const selectedItems = useMemo(
    () => items.filter((item) => selectedIds.has(getId(item))),
    [items, selectedIds, getId],
  );

  const allSelected = ids.length > 0 && selectedIds.size >= ids.length && ids.every((id) => selectedIds.has(id));

  // Counted from `selectedItems`, not from the raw id set: the two differ while an id is
  // selected but no longer rendered, because the prune effect runs after render. A count taken
  // from the raw set would offer actions on rows the action itself cannot see.
  const selectedCount = selectedItems.length;

  return {
    selectedIds,
    selectedCount,
    isSelected,
    selectedItems,
    toggle,
    toggleAll,
    clear,
    retain,
    allSelected,
    someSelected: selectedCount > 0 && !allSelected,
  };
}
