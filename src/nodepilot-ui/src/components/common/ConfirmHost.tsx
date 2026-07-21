import { useTranslation } from 'react-i18next';
import { ModalShell } from './ModalShell';
import { useConfirmStore } from '../../stores/confirmStore';

/**
 * Globally mounted host for confirmDialog() — the promise-based replacement
 * for blocking native confirm() calls. Renders at most one dialog; Escape and
 * backdrop click resolve as "cancel" (ModalShell wiring).
 */
export function ConfirmHost() {
  const pending = useConfirmStore((s) => s.pending);
  const settle = useConfirmStore((s) => s.settle);
  const { t } = useTranslation();
  if (!pending) return null;

  return (
    <ModalShell onClose={() => settle(false)} maxWidth="max-w-sm" z="z-[70]">
      <h2 className="text-base font-semibold text-on-surface">
        {pending.title ?? t('common:confirm')}
      </h2>
      <p className="mt-2 whitespace-pre-line break-words text-sm text-on-surface-variant">
        {pending.message}
      </p>
      <div className="mt-5 flex justify-end gap-2">
        <button
          type="button"
          onClick={() => settle(false)}
          className="rounded-lg px-3.5 py-1.5 text-sm text-on-surface-variant ring-1 ring-outline-variant/40 hover:bg-surface-low"
        >
          {pending.cancelLabel ?? t('common:cancel')}
        </button>
        <button
          type="button"
          autoFocus
          onClick={() => settle(true)}
          className={`rounded-lg px-3.5 py-1.5 text-sm font-medium hover:opacity-90 ${
            pending.danger ? 'bg-error text-white' : 'bg-primary text-on-primary'
          }`}
        >
          {pending.confirmLabel ?? t('common:ok')}
        </button>
      </div>
    </ModalShell>
  );
}
