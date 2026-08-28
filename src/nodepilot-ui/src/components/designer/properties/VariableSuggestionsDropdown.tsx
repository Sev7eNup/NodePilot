import { useEffect, useLayoutEffect, useState, type RefObject } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import type { VariableSuggestion } from './useVariableAutocomplete';

// In narrow FieldGrid cells the anchor is too small to show a long variable expression next
// to its label, so the dropdown gets its own minimum width.
const MIN_WIDTH = 420;

function computePos(rect: DOMRect) {
  const width = Math.max(rect.width, MIN_WIDTH);
  // If the input sits far to the right, the dropdown would extend past the viewport edge,
  // so shift it left far enough to keep its right edge on screen.
  const left = Math.max(8, Math.min(rect.left, globalThis.innerWidth - width - 8));
  return { top: rect.bottom + 4, left, width };
}

/**
 * Renders the `{{` autocomplete dropdown anchored beneath its host input. A portal keeps the
 * dropdown out of any `overflow-hidden` or `overflow-auto` ancestor, such as the panel scroll
 * container, so it can extend over the content below. The position is recomputed on open,
 * scroll and resize from the input's getBoundingClientRect.
 *
 * Items use onMouseDown instead of onClick because the host input fires onBlur first, which
 * closes the dropdown before an onClick could arrive.
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

  // Compute the position synchronously after layout so the dropdown appears in the right
  // place on the same frame it opens, without flicker.
  useLayoutEffect(() => {
    if (!open || !anchorRef.current) { setPos(null); return; }
    setPos(computePos(anchorRef.current.getBoundingClientRect()));
  }, [open, anchorRef]);

  // Reposition on scroll and resize. The capture phase catches the panel's internal scroll
  // container as well as the window. Closing is handled by the host input's onBlur.
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

  // Cap the height to leave a small gap at the viewport bottom, but keep a floor so an input
  // near the bottom of the screen still shows a usable list.
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
