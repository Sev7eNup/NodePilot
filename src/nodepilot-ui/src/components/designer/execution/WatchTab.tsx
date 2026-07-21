import { Add, Close, Copy } from '@carbon/icons-react';
import { useState, useEffect, useMemo, useRef, useCallback, useLayoutEffect } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation, Trans } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import type { Node } from '@xyflow/react';
import { api } from '../../../api/client';
import type { DatabusEntry } from '../../../hooks/useSignalR';
import { describeNodeOutputs, type UpstreamVariable } from '../../../lib/upstreamVariables';

const WATCH_STORAGE_KEY = 'nodepilot-watch-expressions';

type WatchPickerEntry = {
  /** Raw expression without `{{` `}}` — what gets inserted into the watch textbox
   *  (the box renders its own `{{` `}}` framing). */
  insert: string;
  /** Human-readable label shown next to the expression. */
  label: string;
  group: 'workflow' | 'global' | 'live';
  /** If true, this variable currently has a value in the live databus. */
  hasLiveValue?: boolean;
};

export function WatchTab({ workflowId, databus, nodes }: Readonly<{ workflowId: string; databus: Record<string, DatabusEntry>; nodes: Node[] }>) {
  const { t } = useTranslation('designer');
  const [expressions, setExpressions] = useState<string[]>(() => {
    try {
      const stored = localStorage.getItem(`${WATCH_STORAGE_KEY}:${workflowId}`);
      return stored ? JSON.parse(stored) : [];
    } catch { return []; }
  });
  const [newExpr, setNewExpr] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);
  const [picker, setPicker] = useState<{ x: number; y: number } | null>(null);

  const { data: globals = [] } = useQuery({
    queryKey: ['global-variables'],
    queryFn: () => api.get<Array<{ id: string; name: string; isSecret: boolean }>>('/global-variables'),
    staleTime: 60_000,
    // Globals are commonly needed by many inputs at once — a single fetch per stale window
    // is enough, matching the behavior of VariableInsertField.
  });

  // Picker list = workflow-defined step outputs ∪ globals ∪ live databus keys.
  // Step outputs come directly from the workflow definition (`nodes`), so the picker is still
  // useful without an active execution — the user should be able to set watches *before* the
  // run starts.
  const allEntries = useMemo<WatchPickerEntry[]>(() => {
    const seen = new Set<string>();
    const out: WatchPickerEntry[] = [];

    const upstream: UpstreamVariable[] = nodes.flatMap((n) => describeNodeOutputs(n));
    for (const v of upstream) {
      const insert = v.expression.replace(/^\{\{|\}\}$/g, '');
      if (seen.has(insert)) continue;
      seen.add(insert);
      out.push({
        insert,
        label: v.label,
        group: 'workflow',
        hasLiveValue: Object.prototype.hasOwnProperty.call(databus, insert),
      });
    }

    for (const g of globals) {
      const insert = `globals.${g.name}`;
      if (seen.has(insert)) continue;
      seen.add(insert);
      out.push({
        insert,
        label: g.isSecret ? t('execution.watch.globalSecretLabel', { name: g.name }) : t('execution.watch.globalLabel', { name: g.name }),
        group: 'global',
        hasLiveValue: Object.prototype.hasOwnProperty.call(databus, insert),
      });
    }

    // Live databus keys that didn't already come from workflow/globals — e.g. trigger
    // params, or structured-output params from runScript variables that the parser can't
    // detect statically. This way the picker includes everything the run actually published.
    for (const key of Object.keys(databus)) {
      if (seen.has(key)) continue;
      seen.add(key);
      out.push({ insert: key, label: t('execution.watch.liveLabel'), group: 'live', hasLiveValue: true });
    }

    return out;
  }, [nodes, globals, databus, t]);

  const insertAtCursor = useCallback((text: string) => {
    const el = inputRef.current;
    if (!el) {
      setNewExpr((prev) => prev + text);
      return;
    }
    const start = el.selectionStart ?? newExpr.length;
    const end = el.selectionEnd ?? newExpr.length;
    const next = newExpr.slice(0, start) + text + newExpr.slice(end);
    setNewExpr(next);
    requestAnimationFrame(() => {
      el.focus();
      const pos = start + text.length;
      el.setSelectionRange(pos, pos);
    });
  }, [newExpr]);

  const openPicker = useCallback((e: React.MouseEvent) => {
    // Suppress browser context menu — we replace it with the variables list. Same
    // pattern used by other right-click pickers (e.g. NodeContextMenu in the designer).
    e.preventDefault();
    setPicker({ x: e.clientX, y: e.clientY });
  }, []);

  const saveExpressions = useCallback((exprs: string[]) => {
    setExpressions(exprs);
    localStorage.setItem(`${WATCH_STORAGE_KEY}:${workflowId}`, JSON.stringify(exprs));
  }, [workflowId]);

  const addExpression = () => {
    const trimmed = newExpr.trim();
    if (!trimmed || expressions.includes(trimmed)) return;
    saveExpressions([...expressions, trimmed]);
    setNewExpr('');
  };

  const removeExpression = (expr: string) => {
    saveExpressions(expressions.filter((e) => e !== expr));
  };

  const resolveExpression = (expr: string): { value: string | null; found: boolean } => {
    // Try to match {{expr}} format or raw key
    const key = expr.replace(/^\{\{|\}\}$/g, '').trim();
    if (!key) return { value: null, found: false };

    // Direct databus lookup
    const entry = databus[key];
    if (entry) {
      let val = entry.value;
      if (typeof val === 'object' && val !== null) val = JSON.stringify(val, null, 2);
      return { value: String(val ?? ''), found: true };
    }

    // Try with step output variable name: "stepName.output" → look for matching stepId
    const parts = key.split('.');
    if (parts.length >= 2) {
      const [stepName, ...fieldParts] = parts;
      const field = fieldParts.join('.');
      // Search databus for keys matching stepName.*
      for (const [databusKey, entry] of Object.entries(databus)) {
        if (databusKey.startsWith(`${stepName}.`)) {
          const databusField = databusKey.slice(stepName.length + 1);
          if (databusField === field || field === 'output') {
            let val = entry.value;
            if (typeof val === 'object' && val !== null) val = JSON.stringify(val, null, 2);
            return { value: String(val ?? ''), found: true };
          }
        }
      }
    }

    // Check globals
    for (const [databusKey, entry] of Object.entries(databus)) {
      if (databusKey.startsWith('globals.')) {
        const globalName = databusKey.slice('globals.'.length);
        if (key === `globals.${globalName}` || key === globalName) {
          return { value: String(entry.value ?? ''), found: true };
        }
      }
    }

    return { value: null, found: false };
  };

  return (
    <div className="flex flex-col h-full text-xs">
      {/* Add expression input */}
      <div className="flex items-center gap-1 px-3 py-1.5 border-b border-outline-variant/10 shrink-0">
        <div className="flex-1 flex items-center gap-1 bg-surface-high rounded px-2 py-1" onContextMenu={openPicker}>
          <span className="text-on-surface-variant/50 font-mono">{"{{"}</span>
          <input
            ref={inputRef}
            type="text"
            value={newExpr}
            onChange={(e) => setNewExpr(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && addExpression()}
            onContextMenu={openPicker}
            placeholder={t('execution.watch.placeholder')}
            className="flex-1 bg-transparent text-on-surface font-mono text-[11px] outline-none placeholder:text-on-surface-variant/40"
          />
          <span className="text-on-surface-variant/50 font-mono">{"}}"}</span>
        </div>
        <button
          onClick={addExpression}
          disabled={!newExpr.trim()}
          className="flex items-center justify-center rounded h-6 w-6 bg-primary text-on-primary hover:opacity-90 disabled:opacity-30 transition-opacity"
          title={t('execution.watch.addExpression')}
        >
          <Add size={12} />
        </button>
      </div>
      <WatchVariablePicker
        anchor={picker}
        entries={allEntries}
        onPick={(text) => { insertAtCursor(text); setPicker(null); }}
        onClose={() => setPicker(null)}
      />
      {/* Watch list */}
      <div className="flex-1 overflow-y-auto">
        {expressions.length === 0 && (
          <div className="flex items-center justify-center h-full text-on-surface-variant/50 text-[11px] px-4 text-center">
            <Trans
              i18nKey="execution.watch.emptyHint"
              ns="designer"
              components={[
                <code className="mx-1 px-1 bg-surface-high rounded font-mono" key="0" />,
                <code className="mx-1 px-1 bg-surface-high rounded font-mono" key="1" />,
              ]}
            />
          </div>
        )}
        {expressions.map((expr) => {
          const resolved = resolveExpression(expr);
          const value = resolved.value ?? '';
          return (
            <div key={expr} className="flex items-center gap-2 px-3 py-1.5 border-b border-outline-variant/5 hover:bg-surface-high/50 group">
              <code className="text-[11px] font-mono text-primary min-w-[120px] truncate" title={expr}>
                {expr}
              </code>
              <span className="text-on-surface-variant/30">=</span>
              <div className="flex-1 min-w-0">
                {resolved.found ? (
                  <span className="text-[11px] font-mono text-on-surface break-all whitespace-pre-wrap" title={value}>
                    {value.length > 200 ? value.slice(0, 200) + '…' : value}
                  </span>
                ) : (
                  <span className="text-[11px] text-on-surface-variant/40 italic">
                    {Object.keys(databus).length > 0 ? t('execution.watch.notFound') : t('execution.watch.noActiveExecution')}
                  </span>
                )}
              </div>
              <button
                onClick={() => {
                  if (value) navigator.clipboard.writeText(value);
                }}
                className="opacity-0 group-hover:opacity-100 text-on-surface-variant hover:text-on-surface transition-opacity"
                title={t('execution.watch.copyValue')}
              >
                <Copy size={10} />
              </button>
              <button
                onClick={() => removeExpression(expr)}
                className="opacity-0 group-hover:opacity-100 text-on-surface-variant hover:text-error transition-opacity"
                title={t('execution.watch.remove')}
              >
                <Close size={10} />
              </button>
            </div>
          );
        })}
      </div>
    </div>
  );
}

/**
 * Right-click variable picker for the Watch tab's expression input. Renders as a portal
 * anchored at the mouse position so it escapes the panel's overflow container. Click
 * inserts the bare key (no `{{` `}}` — the Watch input renders its own framing chars).
 *
 * Closed by: outside click, Escape, or picking an entry. Filter input gets initial focus
 * so the user can immediately type to narrow down — for workflows with 30+ step outputs
 * the unfiltered list is impractical to scan visually.
 */
function WatchVariablePicker({ anchor, entries, onPick, onClose }: Readonly<{
  anchor: { x: number; y: number } | null;
  entries: WatchPickerEntry[];
  onPick: (insert: string) => void;
  onClose: () => void;
}>) {
  const { t } = useTranslation('designer');
  const [filter, setFilter] = useState('');
  const [selectedIdx, setSelectedIdx] = useState(0);
  const filterRef = useRef<HTMLInputElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);

  // Reset filter + focus whenever the picker re-opens.
  useEffect(() => {
    if (!anchor) return;
    setFilter('');
    setSelectedIdx(0);
    requestAnimationFrame(() => filterRef.current?.focus());
  }, [anchor]);

  // Clamp menu position so it never escapes the viewport. Width estimate ~340 px,
  // height capped at 60vh; computed after measurement to keep the right/bottom edge inside.
  useLayoutEffect(() => {
    if (!anchor || !menuRef.current) { setPos(null); return; }
    const rect = menuRef.current.getBoundingClientRect();
    const margin = 8;
    const left = Math.min(anchor.x, globalThis.innerWidth - rect.width - margin);
    const top = Math.min(anchor.y, globalThis.innerHeight - rect.height - margin);
    setPos({ top: Math.max(margin, top), left: Math.max(margin, left) });
  }, [anchor, filter]);

  const filtered = useMemo(() => {
    const q = filter.trim().toLowerCase();
    if (!q) return entries;
    return entries.filter((e) =>
      e.insert.toLowerCase().includes(q) || e.label.toLowerCase().includes(q),
    );
  }, [entries, filter]);

  // Clamp selectedIdx when the filtered list shrinks below it.
  useEffect(() => {
    if (selectedIdx >= filtered.length) setSelectedIdx(Math.max(0, filtered.length - 1));
  }, [filtered.length, selectedIdx]);

  // Close on outside click + Escape. mousedown so we beat the focus shuffle.
  useEffect(() => {
    if (!anchor) return;
    const onDown = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as globalThis.Node)) onClose();
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [anchor, onClose]);

  if (!anchor) return null;

  const handleKey = (e: React.KeyboardEvent) => {
    if (filtered.length === 0) return;
    if (e.key === 'ArrowDown') { e.preventDefault(); setSelectedIdx((i) => Math.min(filtered.length - 1, i + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setSelectedIdx((i) => Math.max(0, i - 1)); }
    else if (e.key === 'Enter') { e.preventDefault(); onPick(filtered[selectedIdx].insert); }
  };

  const workflowEntries = filtered.filter((e) => e.group === 'workflow');
  const globalEntries = filtered.filter((e) => e.group === 'global');
  const liveEntries = filtered.filter((e) => e.group === 'live');

  // Flat index for keyboard navigation across all groups; matches render order.
  const flatOrder = [...workflowEntries, ...globalEntries, ...liveEntries];

  const renderRow = (entry: WatchPickerEntry, flatIdx: number) => (
    <button
      key={`${entry.group}:${entry.insert}`}
      type="button"
      onMouseDown={(e) => { e.preventDefault(); onPick(entry.insert); }}
      onMouseEnter={() => setSelectedIdx(flatIdx)}
      className={`w-full flex items-center justify-between gap-2 px-3 py-1.5 text-left transition-colors ${
        flatIdx === selectedIdx ? 'bg-primary-fixed' : 'hover:bg-surface-high'
      }`}
    >
      <span className="flex items-center gap-1.5 min-w-0">
        {entry.hasLiveValue && (
          <span className="w-1.5 h-1.5 rounded-full bg-green-500 shrink-0" title={t('execution.watch.hasLiveValue')} />
        )}
        <code className="text-[11px] font-mono text-primary truncate">{entry.insert}</code>
      </span>
      <span className="text-[10px] font-label text-on-surface-variant truncate ml-2">{entry.label}</span>
    </button>
  );

  return createPortal(
    <div
      ref={menuRef}
      className="fixed z-50 bg-surface-lowest border border-outline-variant/40 rounded-md shadow-2xl flex flex-col"
      style={{
        top: pos?.top ?? anchor.y,
        left: pos?.left ?? anchor.x,
        width: 340,
        maxHeight: '60vh',
        // Hidden until measured to avoid a one-frame flash at the unclamped anchor.
        visibility: pos ? 'visible' : 'hidden',
      }}
      role="menu"
    >
      <div className="px-2 py-1.5 border-b border-outline-variant/20 shrink-0">
        <input
          ref={filterRef}
          type="text"
          value={filter}
          onChange={(e) => { setFilter(e.target.value); setSelectedIdx(0); }}
          onKeyDown={handleKey}
          placeholder={t('execution.watch.filterPlaceholder')}
          className="w-full bg-surface-high rounded px-2 py-1 text-[11px] font-mono outline-none focus:ring-1 focus:ring-primary/40"
        />
      </div>
      <div className="overflow-y-auto flex-1">
        {flatOrder.length === 0 && (
          <div className="px-3 py-4 text-center text-[11px] text-on-surface-variant">
            {entries.length === 0 ? t('execution.watch.noVariables') : t('execution.watch.noMatches', { filter })}
          </div>
        )}
        {workflowEntries.length > 0 && (
          <div>
            <div className="px-3 py-1 text-[9px] font-label font-bold uppercase tracking-widest text-on-surface-variant bg-surface-low/60 sticky top-0">
              {t('execution.watch.groupWorkflow')}
            </div>
            {workflowEntries.map((entry) => renderRow(entry, flatOrder.indexOf(entry)))}
          </div>
        )}
        {globalEntries.length > 0 && (
          <div>
            <div className="px-3 py-1 text-[9px] font-label font-bold uppercase tracking-widest text-on-surface-variant bg-surface-low/60 sticky top-0">
              {t('execution.watch.groupGlobals')}
            </div>
            {globalEntries.map((entry) => renderRow(entry, flatOrder.indexOf(entry)))}
          </div>
        )}
        {liveEntries.length > 0 && (
          <div>
            <div className="px-3 py-1 text-[9px] font-label font-bold uppercase tracking-widest text-on-surface-variant bg-surface-low/60 sticky top-0">
              {t('execution.watch.groupLive')}
            </div>
            {liveEntries.map((entry) => renderRow(entry, flatOrder.indexOf(entry)))}
          </div>
        )}
      </div>
      <div className="px-3 py-1 border-t border-outline-variant/20 bg-surface-lowest text-[10px] font-label text-outline shrink-0">
        {t('execution.watch.footerHint')}
      </div>
    </div>,
    document.body,
  );
}
