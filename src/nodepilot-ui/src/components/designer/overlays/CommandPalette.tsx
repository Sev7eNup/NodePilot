import {
  ChevronRight,
  ColorPalette,
  Compass,
  Dashboard,
  Download,
  Edit,
  Help,
  Locked,
  Play,
  Search,
  Terminal,
  View,
  type CarbonIconType,
} from '@carbon/icons-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';

/**
 * Group → icon mapping. Lives next to the palette so adding a new group is one tuple, and
 * the visual identity stays in sync with `buildCommandPaletteCommands` in WorkflowEditorPage.
 * Falling back to a generic icon for unrecognized group names keeps the layout stable when
 * a future builder ships a group before the icon mapping catches up.
 */
const GROUP_ICONS: Record<string, CarbonIconType> = {
  Lifecycle: Locked,
  Run: Play,
  Edit: Edit,
  Layout: Dashboard,
  Style: ColorPalette,
  View: View,
  Export: Download,
  Navigate: Compass,
  Help: Help,
};

export interface PaletteCommand {
  /** Unique id, used as React key. */
  id: string;
  /** Title shown in the result row — what the user is searching for. */
  title: string;
  /** Optional secondary line (path, shortcut hint, etc.). */
  subtitle?: string;
  /** Bucket label (e.g. "Edit", "View", "Run", "Navigate") for visual grouping. */
  group?: string;
  /** Optional keyboard shortcut to surface on the right of the row. */
  shortcut?: string;
  /** Optional disabled flag — shown in muted color and not invokable. */
  disabled?: boolean;
  /** Hidden from the compact standard-mode command surface. */
  expertOnly?: boolean;
  /** Action invoked on Enter / click. The palette closes itself afterwards. */
  run: () => void;
}

interface Props {
  commands: PaletteCommand[];
  onClose: () => void;
}

/**
 * VS-Code-style fuzzy command picker. Each character of the query has to appear in the
 * title in order; longer match runs win the score. Keep it tiny — the goal is that typing
 * "heat" finds the Heatmap toggle, not full Fuse.js-level fuzzy-search semantics.
 */
function fuzzyScore(query: string, target: string): number {
  if (!query) return 1;
  const q = query.toLowerCase();
  const t = target.toLowerCase();
  if (t.includes(q)) return 1000 - t.indexOf(q); // substring beats subseq
  let qi = 0;
  let lastMatchPos = -1;
  let runStart = -1;
  let bestRun = 0;
  for (let ti = 0; ti < t.length && qi < q.length; ti++) {
    if (t[ti] === q[qi]) {
      if (lastMatchPos === ti - 1) {
        if (runStart < 0) runStart = ti - 1;
        bestRun = Math.max(bestRun, ti - runStart + 1);
      } else {
        runStart = -1;
      }
      lastMatchPos = ti;
      qi++;
    }
  }
  if (qi !== q.length) return 0; // not a subsequence at all
  return 100 + bestRun * 10 - (t.length - q.length) * 0.1;
}

export function CommandPalette({ commands, onClose }: Readonly<Props>) {
  const { t } = useTranslation('editor');
  const [query, setQuery] = useState('');
  const [activeIdx, setActiveIdx] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => { inputRef.current?.focus(); }, []);

  const ranked = useMemo(() => {
    const scored = commands
      .map((c) => ({ c, s: fuzzyScore(query, c.title + ' ' + (c.subtitle ?? '') + ' ' + (c.group ?? '')) }))
      .filter((x) => x.s > 0)
      .sort((a, b) => b.s - a.s);
    return scored.map((x) => x.c);
  }, [commands, query]);

  useEffect(() => { setActiveIdx((i) => Math.min(i, Math.max(0, ranked.length - 1))); }, [ranked.length]);

  const invoke = (cmd: PaletteCommand) => {
    if (cmd.disabled) return;
    cmd.run();
    onClose();
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActiveIdx((i) => Math.min(ranked.length - 1, i + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActiveIdx((i) => Math.max(0, i - 1)); }
    else if (e.key === 'Enter' && ranked[activeIdx]) { e.preventDefault(); invoke(ranked[activeIdx]); }
    else if (e.key === 'Escape') { e.preventDefault(); onClose(); }
  };

  // Group results by their `group` field, preserving rank order within each bucket.
  const grouped = useMemo(() => {
    const map = new Map<string, PaletteCommand[]>();
    for (const c of ranked) {
      const g = c.group ?? 'Other';
      if (!map.has(g)) map.set(g, []);
      map.get(g)!.push(c);
    }
    return [...map.entries()];
  }, [ranked]);

  // Compute flat→absolute index mapping for highlight + click handlers.
  let cursor = 0;

  const totalCommands = commands.length;
  const visibleCount = ranked.length;

  return (
    <div
      className="np-anim-backdrop fixed inset-0 z-[9998] bg-black/40 backdrop-blur-[2px] flex items-start justify-center pt-24"
      onMouseDown={onClose}
    >
      <div
        className="np-anim-overlay bg-surface-lowest border border-outline-variant/40 rounded-2xl shadow-[0_24px_64px_-8px_rgba(0,0,0,0.35)] w-[720px] max-w-[92vw] overflow-hidden flex flex-col"
        onMouseDown={(e) => e.stopPropagation()}
      >
        {/* Search header — slightly taller, primary-tinted icon, kbd-style hint chips on the right. */}
        <div className="flex items-center gap-3 px-5 py-3.5 border-b border-outline-variant/25 bg-gradient-to-b from-surface-lowest to-surface-low/30">
          <Search size={18} className="text-primary/70 shrink-0" />
          <input
            ref={inputRef}
            type="text"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder={t('commandPalette.placeholder')}
            className="flex-1 bg-transparent border-none outline-none text-[15px] font-label text-on-surface placeholder:text-outline/70"
          />
          <div className="flex items-center gap-1 shrink-0">
            <Kbd>↑</Kbd>
            <Kbd>↓</Kbd>
            <Kbd>Enter</Kbd>
            <Kbd>Esc</Kbd>
          </div>
        </div>

        {/* Results list */}
        <div className="max-h-[520px] overflow-y-auto">
          {ranked.length === 0 && (
            <div className="px-4 py-12 text-center">
              <Terminal size={28} className="text-outline/50 mx-auto mb-3" />
              <div className="text-sm font-label text-on-surface-variant">
                {t('commandPalette.noMatch', { query })}
              </div>
              <div className="text-[11px] font-label text-outline mt-1.5">
                {t('commandPalette.tryDifferent', { commands: totalCommands, groups: Object.keys(GROUP_ICONS).length })}
              </div>
            </div>
          )}
          {grouped.map(([groupName, items]) => {
            const Icon = GROUP_ICONS[groupName] ?? ChevronRight;
            return (
              <div key={groupName} className="py-1">
                {/* Group header — icon + uppercase label + count, separator above */}
                <div className="flex items-center gap-2 px-5 pt-2 pb-1.5 text-[10px] font-label font-bold uppercase tracking-[0.1em] text-on-surface-variant/80">
                  <Icon size={11} className="text-on-surface-variant/70" />
                  <span>{t(`palette.groups.${groupName}`, { defaultValue: groupName })}</span>
                  <span className="text-outline/60 font-mono normal-case tracking-normal">· {items.length}</span>
                </div>
                {items.map((cmd) => {
                  const idx = cursor++;
                  const isActive = idx === activeIdx;
                  return (
                    <button
                      key={cmd.id}
                      type="button"
                      disabled={cmd.disabled}
                      onMouseEnter={() => setActiveIdx(idx)}
                      onClick={() => invoke(cmd)}
                      className={`relative w-full flex items-center gap-3 pl-5 pr-4 py-2 text-left transition-colors group disabled:cursor-not-allowed ${
                        isActive
                          ? 'bg-primary/10'
                          : 'hover:bg-surface-high/70'
                      } ${cmd.disabled ? 'opacity-50' : ''}`}
                    >
                      {/* Left accent stripe — only on the active row, primary color, pulls the eye. */}
                      {isActive && (
                        <span className="absolute left-0 top-1 bottom-1 w-[3px] rounded-r bg-primary" />
                      )}
                      <div className="flex-1 min-w-0">
                        <div className={`text-[13.5px] font-label font-semibold truncate ${isActive ? 'text-primary' : 'text-on-surface'}`}>
                          {cmd.title}
                        </div>
                        {cmd.subtitle && (
                          <div className="text-[11px] font-label text-on-surface-variant/80 truncate mt-0.5">
                            {cmd.subtitle}
                          </div>
                        )}
                      </div>
                      {cmd.shortcut && (
                        <Kbd subtle={!isActive}>{cmd.shortcut}</Kbd>
                      )}
                    </button>
                  );
                })}
              </div>
            );
          })}
        </div>

        {/* Footer — context line: result count vs total + hint */}
        <div className="flex items-center justify-between px-5 py-2 border-t border-outline-variant/25 bg-surface-low/40 text-[10px] font-label text-on-surface-variant/70">
          <span>
            {query
              ? t('commandPalette.showingOf', { visible: visibleCount, total: totalCommands })
              : t('commandPalette.available', { total: totalCommands })
            }
          </span>
          <span className="font-mono">Ctrl+Shift+P</span>
        </div>
      </div>
    </div>
  );
}

/**
 * Tiny kbd-style chip — used for the header keyboard-hints and per-row shortcut pills.
 * `subtle` reduces contrast for inactive rows so the active row's chip stays the visual focus.
 */
function Kbd({ children, subtle = false }: Readonly<{ children: React.ReactNode; subtle?: boolean }>) {
  return (
    <span
      className={`inline-flex items-center justify-center min-w-[20px] h-[20px] px-1.5 rounded-md text-[10px] font-mono font-semibold shrink-0 border ${
        subtle
          ? 'bg-surface-high/70 border-outline-variant/30 text-on-surface-variant/70'
          : 'bg-surface-high border-outline-variant/50 text-on-surface'
      }`}
    >
      {children}
    </span>
  );
}
