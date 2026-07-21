import React from 'react';
import { useTranslation } from 'react-i18next';
import { getWorkflowSnippets } from '../../../lib/workflowSnippets';
import { ACTIVITY_ICONS } from '../../../lib/activityCatalog.generated';
import { isCustomActivityType, getCustomActivityFacts } from '../../../lib/customActivities';
import { ACTIVITY_ICON_COMPONENTS, FALLBACK_ACTIVITY_ICON } from '../../../lib/activityIcons';
import { ChevronDown } from '@carbon/icons-react';

const iconColors: Record<string, string> = {
  // Triggers
  manualTrigger: 'text-red-600', scheduleTrigger: 'text-yellow-600', webhookTrigger: 'text-orange-600',
  fileWatcherTrigger: 'text-green-600', databaseTrigger: 'text-purple-600', eventLogTrigger: 'text-sky-600',
  // Activities
  runScript: 'text-blue-600', fileOperation: 'text-amber-600', folderOperation: 'text-amber-600', fileHash: 'text-violet-600',
  zipOperation: 'text-yellow-700', serviceManagement: 'text-green-600', scheduledTask: 'text-fuchsia-600',
  registryOperation: 'text-purple-600', wmiQuery: 'text-cyan-600', startProgram: 'text-rose-600',
  powerManagement: 'text-red-700', waitForCondition: 'text-cyan-600',
  restApi: 'text-orange-600',
  sql: 'text-sky-700', xmlQuery: 'text-teal-600', jsonQuery: 'text-amber-700',
  emailNotification: 'text-pink-600', delay: 'text-on-surface-variant', log: 'text-slate-600',
  // Control Flow
  junction: 'text-indigo-600', decision: 'text-indigo-700',
};

export function ActivityIcon({ type, size = 20 }: Readonly<{ type: string; size?: number }>) {
  // Custom activities (custom:<key>) carry their own icon + optional accent colour in the runtime
  // catalog — they have no static ACTIVITY_ICONS / iconColors entry.
  if (isCustomActivityType(type)) {
    const facts = getCustomActivityFacts(type);
    const CustomIcon = ACTIVITY_ICON_COMPONENTS[facts?.icon ?? ''] ?? FALLBACK_ACTIVITY_ICON;
    return (
      <CustomIcon
        size={size}
        className={facts?.color ? undefined : 'text-indigo-500'}
        style={{ color: facts?.color ?? undefined }}
      />
    );
  }

  const colorClass = iconColors[type] || 'text-on-surface-variant';
  const Icon = ACTIVITY_ICON_COMPONENTS[ACTIVITY_ICONS[type] || 'help'] ?? FALLBACK_ACTIVITY_ICON;

  return <Icon size={size} className={colorClass} />;
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

export function ResizeHandle({ direction, ...props }: { direction: 'horizontal' | 'vertical' } & React.HTMLAttributes<HTMLDivElement>) {
  const isH = direction === 'horizontal';
  return (
    <div
      {...props}
      className={`shrink-0 group relative z-20 ${
        isH ? 'w-1 cursor-col-resize' : 'h-1 cursor-row-resize'
      }`}
    >
      <div className={`absolute transition-colors ${
        isH
          ? 'top-0 bottom-0 left-0 w-1 group-hover:bg-primary/30 group-active:bg-primary/60'
          : 'left-0 right-0 top-0 h-1 group-hover:bg-primary/30 group-active:bg-primary/60'
      }`} />
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
