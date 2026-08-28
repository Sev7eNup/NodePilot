import { useCallback, useMemo, useState, type RefObject } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../../api/client';
import type { UpstreamVariable } from '../../../lib/upstreamVariables';

type GlobalVariableRow = {
  id: string;
  name: string;
  value: string | null;
  isSecret: boolean;
  description: string | null;
};

export interface VariableSuggestion {
  expression: string;
  label: string;
}

export interface VariableAutocompleteApi {
  open: boolean;
  filtered: VariableSuggestion[];
  selectedIdx: number;
  refresh: () => void;
  pick: (expression: string) => void;
  close: () => void;
  handleKeyDown: (e: React.KeyboardEvent<HTMLInputElement | HTMLTextAreaElement>) => void;
}

/**
 * Drives a `{{`-triggered suggestion dropdown for upstream and global variables. Shared by
 * VariableInsertField in the properties panel and ExpressionTester in the debug overlay.
 *
 * Behavior:
 *   - Watches the cursor: opens when the most recent unclosed `{{` precedes it and the text
 *     in between is identifier-like ([\w.-]). Closes otherwise.
 *   - Picking replaces `{{partial`, from openIdx through the cursor, with the chosen
 *     expression.
 *   - Globals load via React Query with a long staleTime so multiple inputs share one fetch.
 *
 * `enabled = false` keeps the hook installed but suppresses opening. The toggle button in
 * VariableInsertField uses this.
 */
export function useVariableAutocomplete({
  inputRef,
  value,
  onChange,
  upstreamVars,
  enabled = true,
}: {
  inputRef: RefObject<HTMLInputElement | HTMLTextAreaElement | null>;
  value: string;
  onChange: (val: string) => void;
  upstreamVars: UpstreamVariable[];
  enabled?: boolean;
}): VariableAutocompleteApi {
  const { data: globals = [] } = useQuery({
    queryKey: ['global-variables'],
    queryFn: () => api.get<GlobalVariableRow[]>('/global-variables'),
    staleTime: 60_000,
  });

  const allSuggestions = useMemo<VariableSuggestion[]>(() => {
    const out: VariableSuggestion[] = [];
    for (const v of upstreamVars) out.push({ expression: v.expression, label: v.label });
    for (const g of globals) out.push({ expression: `{{globals.${g.name}}}`, label: `Global · ${g.name}` });
    return out;
  }, [upstreamVars, globals]);

  const [open, setOpen] = useState(false);
  const [filter, setFilter] = useState('');
  const [matchStart, setMatchStart] = useState(-1);
  const [selectedIdx, setSelectedIdx] = useState(0);

  const filtered = useMemo(() => {
    if (!open) return [];
    const q = filter.trim().toLowerCase();
    // The dropdown scrolls (`max-h-[70vh] overflow-y-auto`), so a long list is not truncated
    // visually. The cap of 200 keeps very large workflows from rendering thousands of nodes.
    return allSuggestions
      .filter((s) => !q || s.expression.toLowerCase().includes(q) || s.label.toLowerCase().includes(q))
      .slice(0, 200);
  }, [allSuggestions, filter, open]);

  const close = useCallback(() => setOpen(false), []);

  const refresh = useCallback(() => {
    if (!enabled) return;
    const el = inputRef.current;
    if (!el) return;
    const cursor = el.selectionStart ?? value.length;
    const before = value.slice(0, cursor);
    const openIdx = before.lastIndexOf('{{');
    const closeIdx = before.lastIndexOf('}}');
    if (openIdx < 0 || openIdx < closeIdx) { setOpen(false); return; }
    const partial = before.slice(openIdx + 2);
    if (!/^[\w.-]*$/.test(partial)) { setOpen(false); return; }
    setMatchStart(openIdx);
    setFilter(partial);
    setOpen(true);
    setSelectedIdx(0);
  }, [enabled, inputRef, value]);

  const pick = useCallback((expression: string) => {
    const el = inputRef.current;
    if (!el || matchStart < 0) return;
    const cursor = el.selectionStart ?? value.length;
    const newValue = value.slice(0, matchStart) + expression + value.slice(cursor);
    onChange(newValue);
    setOpen(false);
    requestAnimationFrame(() => {
      el.focus();
      const newPos = matchStart + expression.length;
      el.setSelectionRange(newPos, newPos);
    });
  }, [inputRef, matchStart, value, onChange]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    if (!open || filtered.length === 0) return;
    if (e.key === 'ArrowDown') { e.preventDefault(); setSelectedIdx((i) => Math.min(filtered.length - 1, i + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setSelectedIdx((i) => Math.max(0, i - 1)); }
    else if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); pick(filtered[selectedIdx].expression); }
    else if (e.key === 'Escape') { e.preventDefault(); setOpen(false); }
  }, [open, filtered, selectedIdx, pick]);

  return { open, filtered, selectedIdx, refresh, pick, close, handleKeyDown };
}
