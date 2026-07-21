import { Close, Filter } from '@carbon/icons-react';
import { useState, useRef, useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import type { Node } from '@xyflow/react';

interface Props {
  nodes: Node[];
  hiddenTypes: Set<string>;
  onChange: (next: Set<string>) => void;
}

/**
 * Toolbar button + dropdown to filter activity types on the canvas. Clicking a checkbox
 * adds/removes the type from `hiddenTypes`; the parent applies those by setting `hidden`
 * on matching nodes. Counts in the dropdown reflect actual nodes in the current workflow.
 */
export function ActivityTypeFilter({ nodes, hiddenTypes, onChange }: Readonly<Props>) {
  const { t } = useTranslation('editor');
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  // Close on outside click.
  useEffect(() => {
    if (!open) return;
    const onClickOutside = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as globalThis.Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onClickOutside);
    return () => document.removeEventListener('mousedown', onClickOutside);
  }, [open]);

  const typeCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const n of nodes) {
      if (n.type !== 'activity') continue;
      const t = (n.data as Record<string, unknown>)?.activityType as string | undefined;
      if (!t) continue;
      counts.set(t, (counts.get(t) ?? 0) + 1);
    }
    return [...counts.entries()].sort((a, b) => b[1] - a[1]);  // most common first
  }, [nodes]);

  const toggleType = (t: string) => {
    const next = new Set(hiddenTypes);
    if (next.has(t)) next.delete(t); else next.add(t);
    onChange(next);
  };

  const clearAll = () => onChange(new Set());

  return (
    <div ref={containerRef} className="relative">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        title={t('activityFilter.title')}
        className={`flex items-center gap-1.5 rounded-md h-9 px-2.5 text-xs font-label font-semibold transition-colors ${
          hiddenTypes.size > 0 ? 'bg-amber-100 text-amber-800' : 'bg-surface-high hover:bg-surface-highest text-on-surface-variant'
        }`}
      >
        <Filter size={13} />
        {hiddenTypes.size > 0 && <span className="bg-amber-600 text-white rounded-full px-1.5 py-0 text-[9px] font-bold">{hiddenTypes.size}</span>}
      </button>
      {open && (
        <div className="absolute top-full mt-1 right-0 z-30 bg-surface-lowest border border-outline-variant/30 rounded-lg shadow-xl w-64 max-h-96 overflow-hidden flex flex-col">
          <div className="px-3 py-2 border-b border-outline-variant/20 flex items-center justify-between">
            <span className="text-xs font-label font-semibold text-on-surface">{t('activityFilter.hideTitle')}</span>
            {hiddenTypes.size > 0 && (
              <button type="button" onClick={clearAll} className="text-[10px] text-primary hover:underline">{t('activityFilter.clearAll')}</button>
            )}
          </div>
          <div className="overflow-y-auto py-1">
            {typeCounts.length === 0 && (
              <div className="px-3 py-3 text-xs font-label text-outline italic">{t('activityFilter.noActivities')}</div>
            )}
            {typeCounts.map(([type, count]) => {
              const hidden = hiddenTypes.has(type);
              return (
                <label
                  key={type}
                  className="flex items-center gap-2 px-3 py-1.5 hover:bg-surface-high cursor-pointer text-xs font-label"
                >
                  <input
                    type="checkbox"
                    checked={hidden}
                    onChange={() => toggleType(type)}
                    className="w-3 h-3 accent-primary"
                  />
                  <span className={`flex-1 truncate ${hidden ? 'text-on-surface-variant line-through' : 'text-on-surface'}`}>
                    {type}
                  </span>
                  <span className="text-[10px] font-mono text-outline tabular-nums shrink-0">{count}</span>
                </label>
              );
            })}
          </div>
          {hiddenTypes.size > 0 && (
            <div className="border-t border-outline-variant/20 px-3 py-1.5 bg-surface-low">
              <button
                type="button"
                onClick={clearAll}
                className="flex items-center gap-1 text-[11px] font-label font-semibold text-primary hover:text-primary-container"
              >
                <Close size={11} /> {t('activityFilter.showAllAgain')}
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
