import { CheckmarkFilled, Close, Information, WarningAltFilled } from '@carbon/icons-react';
import { createPortal } from 'react-dom';
import { useToastStore, type ToastKind } from '../../stores/toastStore';

const KIND_STYLES: Record<ToastKind, { ring: string; icon: string }> = {
  success: { ring: 'ring-emerald-500/40', icon: 'text-emerald-500' },
  error: { ring: 'ring-error/40', icon: 'text-error' },
  info: { ring: 'ring-outline-variant/40', icon: 'text-on-surface-variant' },
};

function KindIcon({ kind, className }: Readonly<{ kind: ToastKind; className: string }>) {
  if (kind === 'success') return <CheckmarkFilled size={16} className={className} />;
  if (kind === 'error') return <WarningAltFilled size={16} className={className} />;
  return <Information size={16} className={className} />;
}

/**
 * Global toast stack — non-blocking replacement for the former alert() call
 * sites. Mounted once in App.tsx; portal keeps it above every page/overlay,
 * np-shell wrapper keeps theme tokens applied (same trick as ModalShell).
 */
export function ToastHost() {
  const toasts = useToastStore((s) => s.toasts);
  const dismiss = useToastStore((s) => s.dismiss);
  if (toasts.length === 0) return null;

  return createPortal(
    <div className="np-shell">
      <div
        className="fixed bottom-4 right-4 z-[80] flex w-[min(24rem,calc(100vw-2rem))] flex-col items-stretch gap-2"
        role="status"
        aria-live="polite"
      >
        {toasts.map((t) => {
          const style = KIND_STYLES[t.kind];
          return (
            <div
              key={t.id}
              data-testid={`toast-${t.kind}`}
              className={`flex items-start gap-2.5 rounded-lg bg-surface-lowest px-3.5 py-2.5 shadow-2xl ring-1 ${style.ring}`}
            >
              <KindIcon kind={t.kind} className={`mt-0.5 shrink-0 ${style.icon}`} />
              <span className="min-w-0 flex-1 whitespace-pre-line break-words text-sm text-on-surface">
                {t.message}
              </span>
              <button
                type="button"
                onClick={() => dismiss(t.id)}
                className="shrink-0 rounded p-0.5 text-on-surface-variant hover:text-on-surface"
                aria-label="Dismiss"
              >
                <Close size={14} />
              </button>
            </div>
          );
        })}
      </div>
    </div>,
    document.body,
  );
}
