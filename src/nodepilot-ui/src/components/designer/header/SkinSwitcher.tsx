import { Checkmark, ColorPalette } from '@carbon/icons-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useThemeStore, THEMES, type Theme } from '../../../stores/themeStore';

// Colour-skin options for the designer-header switcher — derived from the THEMES registry
// (+ system) so a newly-added skin shows up here automatically. Labels live in the `nav`
// namespace (shared with the sidebar picker, so names stay in sync).
const SKIN_OPTIONS: { value: Theme; key: string }[] = [
  ...THEMES.map((s) => ({ value: s.id as Theme, key: s.labelKey })),
  { value: 'system' as Theme, key: 'themeSystem' },
];

/**
 * Colour-skin switcher — a trailing Palette icon opening a right-aligned radio popover of all
 * skins. Reuses the global theme store + shared skin list, so it stays in sync with the sidebar
 * picker. Shared by both editor-header layouts.
 */
export function SkinSwitcher() {
  const { t } = useTranslation('nav');
  const theme = useThemeStore((s) => s.theme);
  const setTheme = useThemeStore((s) => s.setTheme);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as globalThis.Node)) setOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [open]);

  const activeSkinKey = SKIN_OPTIONS.find((o) => o.value === theme)?.key ?? 'themeSystem';

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-haspopup="menu"
        aria-expanded={open}
        data-testid="toggle-skin"
        className="flex items-center rounded-md bg-surface-high p-1.5 text-on-surface-variant shadow-sm transition-colors hover:text-on-surface"
        title={t(activeSkinKey)}
      >
        <ColorPalette size={16} />
      </button>
      {open && (
        <div
          role="menu"
          className="absolute right-0 top-full z-50 mt-1 min-w-[160px] rounded-md border border-outline-variant/30 bg-surface-container py-1 shadow-lg"
        >
          {SKIN_OPTIONS.map(({ value, key }) => (
            <button
              key={value}
              type="button"
              role="menuitemradio"
              aria-checked={theme === value}
              onClick={() => { setTheme(value); setOpen(false); }}
              className="flex w-full items-center justify-between gap-3 px-3 py-1.5 text-[11px] text-on-surface-variant transition-colors hover:bg-surface-highest hover:text-on-surface"
            >
              <span>{t(key)}</span>
              {theme === value && <Checkmark size={15} className="text-primary" />}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
