import React from 'react';
import { useTranslation } from 'react-i18next';
import { getWorkflowSnippets } from '../../../lib/workflowSnippets';
import { ACTIVITY_ICON_COMPONENTS, FALLBACK_ACTIVITY_ICON } from '../../../lib/activityIcons';
import { getActivityVisual } from '../nodes/activityConfig';
import { ChevronDown } from '@carbon/icons-react';

/**
 * Palette/picker glyph for an activity type.
 *
 * Icon and accent come from {@link getActivityVisual}, the same resolver the canvas nodes
 * use, so palette and canvas never drift apart. Built-ins resolve to the generated
 * `--act-<type>-*` design tokens; custom activities (`custom:<key>`) resolve to the runtime
 * catalog's icon and accent. The colour is a CSS variable applied via `style`, since there
 * is no Tailwind class for it.
 */
export function ActivityIcon({ type, size = 20 }: Readonly<{ type: string; size?: number }>) {
  const { icon, color } = getActivityVisual(type);
  const Icon = ACTIVITY_ICON_COMPONENTS[icon] ?? FALLBACK_ACTIVITY_ICON;

  return <Icon size={size} style={{ color }} />;
}

export function SnippetsSection({ collapsed, onToggle, onInsert, canWrite = true }: Readonly<{
  collapsed: boolean;
  onToggle: () => void;
  onInsert: (snippetId: string) => void;
  canWrite?: boolean;
}>) {
  const { t } = useTranslation('designer');
  const snippets = getWorkflowSnippets();
  return (
    <div>
      <button
        type="button"
        onClick={onToggle}
        className="flex items-center gap-1 w-full px-2 py-1 rounded hover:bg-surface-highest/50 transition-colors"
        aria-expanded={!collapsed}
      >
        <ChevronDown
          size={16}
          className="text-on-surface-variant shrink-0 transition-transform"
          style={{ transform: collapsed ? 'rotate(-90deg)' : 'rotate(0deg)' }}
          aria-hidden="true"
        />
        <h3 className="font-label text-[11px] font-bold text-on-surface-variant uppercase tracking-widest">
          {t('library.snippets')}
        </h3>
        <span className="ml-auto text-[10px] font-label text-outline tabular-nums">
          {snippets.length}
        </span>
      </button>
      {!collapsed && (
        <div className="space-y-0.5 mt-1">
          {[...snippets].sort((a, b) => a.name.localeCompare(b.name)).map((s) => {
            const SnippetIcon = ACTIVITY_ICON_COMPONENTS[s.icon] ?? FALLBACK_ACTIVITY_ICON;
            return (
            <button
              key={s.id}
              onClick={canWrite ? () => onInsert(s.id) : undefined}
              disabled={!canWrite}
              className={`w-full px-3 py-2 rounded-md transition-colors text-left group ${
                canWrite ? 'hover:bg-surface-highest' : 'opacity-50 cursor-not-allowed'
              }`}
              title={canWrite ? s.description : t('library.notInEditing')}
            >
              <div className="flex items-center gap-2">
                <SnippetIcon size={18} className="text-indigo-600" />
                <span className="font-label text-sm font-medium text-on-surface">{s.name}</span>
              </div>
              <p className="font-label text-[10px] text-on-surface-variant mt-0.5 leading-snug line-clamp-2 group-hover:line-clamp-none">
                {s.description}
              </p>
            </button>
            );
          })}
        </div>
      )}
    </div>
  );
}

/**
 * Splitter between a panel and its neighbour (canvas, list, …).
 *
 * A permanent grip pill marks the centre of the seam so the handle is visible without
 * hovering, while staying quiet enough not to compete with the panel's own border.
 *
 * The pointer target is wider than the drawn band: a transparent extender overhangs the
 * 4 px lane by 4 px on each side. It sits inside the wrapper so its events bubble to the
 * wrapper's drag handlers while the layout width stays 4 px, since widening the wrapper
 * itself would shift the canvas.
 */
export function ResizeHandle({ direction, ...props }: { direction: 'horizontal' | 'vertical' } & React.HTMLAttributes<HTMLDivElement>) {
  const isH = direction === 'horizontal';
  return (
    <div
      role="separator"
      aria-orientation={isH ? 'vertical' : 'horizontal'}
      {...props}
      className={`shrink-0 group relative z-20 ${
        isH ? 'w-1 cursor-col-resize' : 'h-1 cursor-row-resize'
      }`}
    >
      {/* Hit target — overhangs the lane on both sides. Transparent, no own styling. */}
      <span className={`absolute ${isH ? 'inset-y-0 -inset-x-1' : 'inset-x-0 -inset-y-1'}`} />
      {/* Hover/drag band along the whole seam. */}
      <span
        aria-hidden
        className={`pointer-events-none absolute bg-transparent transition-colors group-hover:bg-primary/25 group-active:bg-primary/45 ${
          isH ? 'inset-y-0 left-0 w-1' : 'inset-x-0 top-0 h-1'
        }`}
      />
      {/* Always-visible grip pill — grows and takes the accent colour under the cursor. */}
      <span
        aria-hidden
        className={`pointer-events-none absolute rounded-full bg-outline-variant/70 transition-all duration-150 group-hover:bg-primary group-active:bg-primary ${
          isH
            ? 'left-1/2 top-1/2 h-9 w-[3px] -translate-x-1/2 -translate-y-1/2 group-hover:h-14'
            : 'left-1/2 top-1/2 h-[3px] w-9 -translate-x-1/2 -translate-y-1/2 group-hover:w-14'
        }`}
      />
    </div>
  );
}

/**
 * Bottom-right corner grip for 2D-resizing a box. Drives both axes when wired to
 * two `useResizable` instances (one horizontal, one vertical). Pin it as a sibling
 * of the scroll area — not inside it — so it stays at the box corner regardless of
 * scroll. Hover/active tint matches {@link ResizeHandle}.
 */
export function CornerResizeHandle(props: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      {...props}
      className="group absolute bottom-0 right-0 z-20 h-4 w-4 cursor-se-resize"
      data-testid="folder-panel-corner-resize"
    >
      {/* Classic diagonal-grip glyph: three nested strokes tucked into the bottom-right
          corner. Purely decorative (pointer-events-none) — the drag lives on the wrapper. */}
      <svg
        viewBox="0 0 16 16"
        aria-hidden
        className="pointer-events-none absolute bottom-0 right-0 h-4 w-4 text-primary/70 transition-colors group-hover:text-primary group-active:text-primary"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinecap="round"
      >
        <line x1="14" y1="6" x2="6" y2="14" />
        <line x1="14" y1="10" x2="10" y2="14" />
        <line x1="14" y1="13.5" x2="13.5" y2="14" />
      </svg>
    </div>
  );
}
