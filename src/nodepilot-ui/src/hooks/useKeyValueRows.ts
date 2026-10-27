import { useState } from 'react';

export interface KeyValueRow {
  id: number;
  key: string;
  value: string;
  /** Another row above already uses this non-empty name; this row is not committed. */
  duplicate: boolean;
}

type DraftRow = Omit<KeyValueRow, 'duplicate'>;

let nextRowId = 0;

const toRows = (map: Record<string, string>): DraftRow[] =>
  Object.entries(map).map(([key, value]) => ({ id: nextRowId++, key, value }));

// The first row per name wins; later rows with the same name stay local drafts.
function commit(rows: DraftRow[]): Record<string, string> {
  const next: Record<string, string> = {};
  for (const row of rows) if (!Object.hasOwn(next, row.key)) next[row.key] = row.value;
  return next;
}

function sameEntries(a: Record<string, string>, b: Record<string, string>): boolean {
  const aKeys = Object.keys(a);
  const bKeys = Object.keys(b);
  return aKeys.length === bKeys.length && aKeys.every((k, i) => k === bKeys[i] && a[k] === b[k]);
}

/**
 * Rows of a name/value map with a stable identity per row, so typing a name that briefly equals
 * another row's name does not overwrite that row. Rows reset from `map` when it changes from
 * outside, for example on undo or when another node is selected.
 */
export function useKeyValueRows(map: Record<string, string>, onChange: (next: Record<string, string>) => void) {
  const [draft, setDraft] = useState(() => toRows(map));
  let rows = draft;
  if (!sameEntries(commit(draft), map)) {
    rows = toRows(map);
    setDraft(rows);
  }

  const update = (next: DraftRow[]) => {
    setDraft(next);
    const committed = commit(next);
    if (!sameEntries(committed, map)) onChange(committed);
  };

  const seen = new Set<string>();
  const view: KeyValueRow[] = rows.map((row) => {
    const duplicate = row.key !== '' && seen.has(row.key);
    seen.add(row.key);
    return { ...row, duplicate };
  });

  return {
    rows: view,
    add: () => update([...rows, { id: nextRowId++, key: '', value: '' }]),
    rename: (id: number, key: string) => update(rows.map((r) => (r.id === id ? { ...r, key } : r))),
    setValue: (id: number, value: string) => update(rows.map((r) => (r.id === id ? { ...r, value } : r))),
    remove: (id: number) => update(rows.filter((r) => r.id !== id)),
  };
}
