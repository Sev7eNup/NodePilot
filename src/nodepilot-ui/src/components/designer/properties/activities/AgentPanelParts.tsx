import { Information } from '@carbon/icons-react';
import type { ReactNode } from 'react';

/** Titled block of the agent panel. Blocks are separated by a hairline and generous spacing so
 *  task, team, result and limits read as separate steps instead of one long form. */
export function PanelBlock({ title, hint, divider = true, action, children }: Readonly<{
  title: string;
  hint?: string;
  divider?: boolean;
  /** Right-aligned element in the block header, e.g. a count. */
  action?: ReactNode;
  children: ReactNode;
}>) {
  return <section className={`space-y-3 ${divider ? 'border-t border-outline-variant/20 pt-5' : ''}`}>
    <div>
      <div className="flex items-center justify-between gap-2">
        <h5 className="font-label text-[10px] font-bold uppercase tracking-widest text-on-surface-variant">{title}</h5>
        {action}
      </div>
      {hint && <p className="mt-1 text-xs leading-snug text-on-surface-variant">{hint}</p>}
    </div>
    {children}
  </section>;
}

/** Quiet, collapsible note for standing rules (read-only policy) that should not push the form down. */
export function Callout({ title, children }: Readonly<{ title: string; children: ReactNode }>) {
  return <details className="group rounded-lg border border-outline-variant/30 bg-surface-container/40 px-3 py-2">
    <summary className="flex cursor-pointer list-none items-center gap-2 text-xs font-semibold text-on-surface-variant">
      <Information size={14} aria-hidden="true" className="shrink-0 text-primary" />
      <span>{title}</span>
    </summary>
    <p className="mt-2 text-xs leading-snug text-on-surface-variant">{children}</p>
  </details>;
}

/** Small rounded label, e.g. "Lead" or "Reviewer". */
export function Pill({ tone = 'neutral', children }: Readonly<{ tone?: 'neutral' | 'primary'; children: ReactNode }>) {
  return <span className={`shrink-0 rounded-full px-2 py-0.5 font-label text-[10px] font-semibold ${
    tone === 'primary' ? 'bg-primary/15 text-primary' : 'bg-surface-highest text-on-surface-variant'}`}>{children}</span>;
}

/** Round initial used as a member avatar. */
export function Initial({ text, active, small = false }: Readonly<{ text: string; active: boolean; small?: boolean }>) {
  return <span aria-hidden="true" className={`flex shrink-0 items-center justify-center rounded-full font-headline font-bold ${
    small ? 'h-5 w-5 text-[10px]' : 'h-8 w-8 text-sm'} ${
    active ? 'bg-primary text-on-primary' : 'bg-surface-highest text-on-surface-variant'}`}>
    {text.trim().charAt(0).toUpperCase() || '?'}</span>;
}
