import { useEffect, useLayoutEffect, useState, type RefObject } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import type { VariableSuggestion } from './useVariableAutocomplete';

// In narrow FieldGrid cells (~180px) the anchor width isn't enough for long variable
// expressions + label. 420px covers roughly 30 monospace characters + 25 sans-serif
// label characters + padding/gap.
const MIN_WIDTH = 420;

function computePos(rect: DOMRect) {
  const width = Math.max(rect.width, MIN_WIDTH);
  // If the input sits far to the right in the panel, a 420px-wide dropdown would stick
  // out past the right edge of the viewport — shift it left so the right edge stays on-screen.
  const left = Math.max(8, Math.min(rect.left, globalThis.innerWidth - width - 8));
  return { top: rect.bottom + 4, left, width };
}

/**
 * Renders the `{{`-autocomplete dropdown anchored beneath its host input. Uses a portal
 * so the dropdown escapes any `overflow-hidden` / `overflow-auto` ancestor (the panel
 * scroll container, the redesigned Section card, etc.) and can extend over everything
 * below it. Position is recomputed on scroll/resize/open via the input's
 * getBoundingClientRect.
 *
 * onMouseDown (not onClick) on the items because the host's input fires onBlur first —
 * onClick would land in the void after the dropdown closes itself.
 */
export function VariableSuggestionsDropdown({
  open,
  suggestions,
  selectedIdx,
  onPick,
  anchorRef,
  showHelp = true,
}: {
  open: boolean;
  suggestions: VariableSuggestion[];
  selectedIdx: number;
  onPick: (expression: string) => void;
  /** The input/textarea the dropdown should anchor under. Required for portal positioning. */
  anchorRef: RefObject<HTMLInputElement | HTMLTextAreaElement | null>;
  showHelp?: boolean;
}) {
  const { t } = useTranslation('properties');
  const [pos, setPos] = useState<{ top: number; left: number; width: number } | null>(null);

  // Compute position synchronously after layout so the dropdown appears at the right
  // place on the same frame it opens (no flicker).
  useLayoutEffect(() => {
    if (!open || !anchorRef.current) { setPos(null); return; }
    setPos(computePos(anchorRef.current.getBoundingClientRect()));
  }, [open, anchorRef]);

  // Re-position on scroll (capture phase to catch the panel's internal scroll container,
  // not just window) and on resize. Closing handled by the host input's onBlur.
  useEffect(() => {
    if (!open) return;
    const update = () => {
      if (!anchorRef.current) return;
      setPos(computePos(anchorRef.current.getBoundingClientRect()));
    };
    globalThis.addEventListener('scroll', update, true);
    globalThis.addEventListener('resize', update);
    return () => {
      globalThis.removeEventListener('scroll', update, true);
      globalThis.removeEventListener('resize', update);
    };
  }, [open, anchorRef]);

  if (!open || suggestions.length === 0 || !pos) return null;

  // Cap height to leave a small gap to the viewport bottom; if the input is near the
  // bottom of the screen, still allow at least 200 px so the user gets a useful list.
  const maxHeight = Math.max(200, globalThis.innerHeight - pos.top - 8);

  return createPortal(
    <div
      className="fixed z-50 bg-surface-lowest border border-outline-variant/40 rounded-md shadow-2xl overflow-y-auto"
      style={{ top: pos.top, left: pos.left, width: pos.width, maxHeight }}
      role="listbox"
    >
      {suggestions.map((s, i) => (
        <button
          key={s.expression}
          type="button"
          onMouseDown={(e) => { e.preventDefault(); onPick(s.expression); }}
          className={`w-full flex items-center justify-between gap-2 px-3 py-1.5 text-left transition-colors ${
            i === selectedIdx ? 'bg-primary-fixed' : 'hover:bg-surface-high'
          }`}
        >
          <code className="text-[11px] font-mono text-primary truncate">{s.expression}</code>
          <span className="text-[10px] font-label text-on-surface-variant truncate">{s.label}</span>
        </button>
      ))}
      {showHelp && (
        <div className="sticky bottom-0 px-3 py-1 border-t border-outline-variant/20 bg-surface-lowest text-[10px] font-label text-outline">
          {t('autocomplete.help')}
        </div>
      )}
    </div>,
    document.body,
  );
}
