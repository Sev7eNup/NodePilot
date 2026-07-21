import type { ReactNode } from 'react';

/**
 * Shared empty-state block: tonal icon disc + title + optional hint/action.
 * Replaces the scattered bare-gray-text placeholders (execution panel, history,
 * output tab, pickers) with one consistent, token-driven treatment.
 */
export function EmptyState({ icon, title, hint, action, compact = false }: Readonly<{
  icon: ReactNode;
  title: string;
  hint?: string;
  action?: ReactNode;
  /** Tighter paddings for small dropdown/picker contexts. */
  compact?: boolean;
}>) {
  return (
    <div className={`flex flex-col items-center justify-center h-full text-center ${compact ? 'gap-1.5 py-4' : 'gap-2 py-8'}`}>
      <div className={`flex items-center justify-center rounded-full bg-surface-high text-outline ${compact ? 'w-9 h-9' : 'w-12 h-12'}`}>
        {icon}
      </div>
      <span className="font-label text-sm text-on-surface-variant">{title}</span>
      {hint && <span className="font-label text-xs text-outline max-w-[340px]">{hint}</span>}
      {action}
    </div>
  );
}
