import { DocumentBlank, Search, Time } from '@carbon/icons-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../../api/client';
import type { Workflow } from '../../../types/api';

const RECENT_KEY = 'nodepilot.recentWorkflows';
const MAX_RECENT = 10;

/** Read recent workflow IDs from localStorage. Newest first. */
export function readRecentWorkflows(): string[] {
  try {
    const raw = localStorage.getItem(RECENT_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter((x) => typeof x === 'string') : [];
  } catch { return []; }
}

/** Push a workflow ID onto the recents list (dedupe + cap at MAX_RECENT). */
export function pushRecentWorkflow(id: string) {
  try {
    const existing = readRecentWorkflows().filter((x) => x !== id);
    const next = [id, ...existing].slice(0, MAX_RECENT);
    localStorage.setItem(RECENT_KEY, JSON.stringify(next));
  } catch { /* quota / private mode */ }
}

interface Props {
  onClose: () => void;
  /** Kept for compatibility; dirty navigation is guarded centrally by WorkflowEditorPage. */
  isDirty?: boolean;
}

export function WorkflowQuickSwitcher({ onClose }: Readonly<Props>) {
  const { t } = useTranslation('editor');
  const navigate = useNavigate();
  const { id: currentId } = useParams<{ id: string }>();
  const [query, setQuery] = useState('');
  const [activeIdx, setActiveIdx] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  const { data: workflows = [] } = useQuery({
    queryKey: ['workflows'],
    queryFn: () => api.get<Workflow[]>('/workflows'),
    staleTime: 30_000,
  });

  // Auto-focus the search field on mount.
  useEffect(() => { inputRef.current?.focus(); }, []);

  // Recents (most recent first), filtered to workflows that still exist.
  const recents = useMemo(() => {
    if (!workflows.length) return [];
    const ids = readRecentWorkflows();
    return ids
      .map((id) => workflows.find((w) => w.id === id))
      .filter((w): w is Workflow => !!w);
  }, [workflows]);

  // Build the filtered + ordered list. When query is empty: recents first, then the rest.
  // When query is non-empty: fuzzy match across all workflows, recents get a boost.
  const items = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) {
      const recentIds = new Set(recents.map((w) => w.id));
      const rest = workflows.filter((w) => !recentIds.has(w.id));
      return [
        ...recents.map((w) => ({ workflow: w, isRecent: true })),
        ...rest.map((w) => ({ workflow: w, isRecent: false })),
      ];
    }
    // Score = substring rank: name matches outrank description matches.
    const scored = workflows
      .map((w) => {
        const name = w.name.toLowerCase();
        const desc = (w.description ?? '').toLowerCase();
        const inName = name.includes(q);
        const inDesc = desc.includes(q);
        if (!inName && !inDesc) return null;
        const score = (inName ? 100 - name.indexOf(q) : 0) + (inDesc ? 10 : 0);
        return { workflow: w, isRecent: false, score };
      })
      .filter((x): x is { workflow: Workflow; isRecent: false; score: number } => !!x)
      .sort((a, b) => b.score - a.score);
    return scored;
  }, [workflows, recents, query]);

  // Keep activeIdx within bounds when items change (typing narrows the list).
  useEffect(() => { setActiveIdx((i) => Math.min(i, Math.max(0, items.length - 1))); }, [items.length]);

  const navigateTo = (id: string) => {
    if (currentId === id) {
      pushRecentWorkflow(id);
      onClose();
      return;
    }

    // Pass the current workflow as fromWorkflow in location state so the EditorHeader
    // back button can show where we came from. For editor-to-editor switches we avoid
    // pre-navigation side effects; the central useBlocker may still cancel this route
    // change, and WorkflowEditorPage records recents after a workflow actually loads.
    const currentWorkflow = currentId ? workflows.find((w) => w.id === currentId) : undefined;
    const state = currentId
      ? { fromWorkflow: { id: currentId, name: currentWorkflow?.name ?? currentId } }
      : undefined;
    if (!currentId) pushRecentWorkflow(id);
    navigate(`/workflows/${id}`, { state });
    onClose();
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActiveIdx((i) => Math.min(items.length - 1, i + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActiveIdx((i) => Math.max(0, i - 1)); }
    else if (e.key === 'Enter' && items[activeIdx]) { e.preventDefault(); navigateTo(items[activeIdx].workflow.id); }
    else if (e.key === 'Escape') { e.preventDefault(); onClose(); }
  };

  return (
    <div
      className="np-anim-backdrop fixed inset-0 z-[9998] bg-black/30 flex items-start justify-center pt-24"
      onMouseDown={onClose}
    >
      <div
        className="np-anim-overlay bg-surface-lowest border border-outline-variant/30 rounded-xl shadow-2xl w-[640px] max-w-[90vw] overflow-hidden"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="flex items-center gap-2 px-4 py-3 border-b border-outline-variant/20">
          <Search size={16} className="text-on-surface-variant shrink-0" />
          <input
            ref={inputRef}
            type="text"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder={t('quickSwitcher.placeholder')}
            className="flex-1 bg-transparent border-none outline-none text-sm font-label text-on-surface placeholder:text-outline"
          />
          <span className="text-[10px] font-label text-outline">{t('quickSwitcher.hint')}</span>
        </div>
        <div className="max-h-[420px] overflow-y-auto">
          {items.length === 0 && (
            <div className="px-4 py-8 text-center text-xs font-label text-outline">
              {query ? t('switcherNoMatch', { query }) : t('switcherEmpty')}
            </div>
          )}
          {items.map(({ workflow, isRecent }, idx) => (
            <button
              key={workflow.id}
              type="button"
              onMouseEnter={() => setActiveIdx(idx)}
              onClick={() => navigateTo(workflow.id)}
              className={`w-full flex items-center gap-3 px-4 py-2.5 text-left transition-colors ${
                idx === activeIdx ? 'bg-primary/10' : 'hover:bg-surface-high'
              }`}
            >
              {isRecent ? <Time size={14} className="text-on-surface-variant shrink-0" /> : <DocumentBlank size={14} className="text-on-surface-variant shrink-0" />}
              <div className="flex-1 min-w-0">
                <div className="text-sm font-label font-semibold text-on-surface truncate">{workflow.name}</div>
                {workflow.description && (
                  <div className="text-[11px] font-label text-on-surface-variant truncate">{workflow.description}</div>
                )}
              </div>
              <span className="text-[10px] font-mono text-outline shrink-0">{workflow.id.slice(0, 8)}</span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
