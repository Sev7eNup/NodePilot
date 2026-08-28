import { useEffect, useState, useRef, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { resolveVariablePreview, type VariablePreview } from '../../../lib/variablePreview';
import type { StepExecution } from '../../../types/api';

interface Props {
  /** Wrapper element that owns the hover surface, typically the variable-row div. */
  children: ReactNode;
  /** Producing step's last terminal-run StepExecution, or undefined if the step never ran. */
  step: StepExecution | undefined;
  /** Full `{{step.field}}` expression to preview. Selects the channel: output, error or param. */
  expression: string;
  /** Optional className for the wrapping span. */
  className?: string;
  /** Hover handlers from the parent, used for variable-flow highlighting. Both run alongside the
   * tooltip. */
  onMouseEnter?: () => void;
  onMouseLeave?: () => void;
  /** Click handler from the parent, used for clipboard copy. The tooltip closes when the row is
   * left. */
  onClick?: () => void;
  /** Title attribute on the wrapper. */
  title?: string;
}

/**
 * Wraps a hoverable surface, typically a variable row in AvailableVariablesList, and shows a
 * tooltip with the last-run value of that variable. Positioning is plain CSS at a fixed offset
 * beside the row, with a 250 ms delay so quick mouse passes do not flash the tooltip.
 *
 * A custom tooltip rather than the native `title` attribute, because `title` is single-line,
 * has an OS-controlled delay, and cannot render a code block or a channel-label badge.
 */
export function VariablePreviewTooltip({
  children, step, expression, className, onMouseEnter, onMouseLeave, onClick, title,
}: Readonly<Props>) {
  const { t } = useTranslation('properties');
  const [open, setOpen] = useState(false);
  const [preview, setPreview] = useState<VariablePreview | null>(null);
  const openTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    return () => { if (openTimer.current) clearTimeout(openTimer.current); };
  }, []);

  const handleEnter = () => {
    onMouseEnter?.();
    if (openTimer.current) clearTimeout(openTimer.current);
    openTimer.current = setTimeout(() => {
      setPreview(resolveVariablePreview(step, expression));
      setOpen(true);
    }, 250);
  };
  const handleLeave = () => {
    onMouseLeave?.();
    if (openTimer.current) clearTimeout(openTimer.current);
    setOpen(false);
  };

  return (
    <div
      className={`relative ${className ?? ''}`}
      onMouseEnter={handleEnter}
      onMouseLeave={handleLeave}
      onClick={onClick}
      onKeyDown={onClick ? (e) => (e.key === 'Enter' || e.key === ' ') && onClick() : undefined}
      role={onClick ? 'button' : undefined}
      tabIndex={onClick ? 0 : undefined}
      title={title}
    >
      {children}
      {open && preview && (
        <div
          // Right-anchored so the tooltip stays inside the right-hand properties panel.
          // Pointer events are disabled so the cursor still reaches the underlying row.
          className="absolute z-40 right-full top-0 mr-2 w-[320px] rounded-md bg-on-surface text-surface-lowest shadow-lg pointer-events-none border border-outline-variant/30"
          role="tooltip"
        >
          <div className="px-2 py-1 border-b border-outline-variant/20 text-[10px] font-label font-bold uppercase tracking-wide opacity-80 flex items-center justify-between gap-2">
            <span>{preview.sourceLabel}</span>
            {preview.truncated && <span className="text-amber-300">{t('preview.truncated')}</span>}
          </div>
          <pre className="px-2 py-1.5 text-[11px] whitespace-pre-wrap break-all font-mono leading-snug max-h-48 overflow-hidden">
            {preview.value || <span className="italic opacity-60">{t('preview.empty')}</span>}
          </pre>
        </div>
      )}
      {open && !preview && (
        <div
          className="absolute z-40 right-full top-0 mr-2 w-[260px] rounded-md bg-on-surface text-surface-lowest shadow-lg pointer-events-none border border-outline-variant/30 px-2 py-1.5 text-[11px] italic opacity-90"
          role="tooltip"
        >
          Kein Wert vom letzten Lauf — Workflow noch nicht ausgeführt oder Step lief nicht.
        </div>
      )}
    </div>
  );
}
