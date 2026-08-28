import { Close, SecurityServices } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';

/**
 * Type-the-phrase confirmation dialog for the two actions that grant write access to
 * the SQL console: enabling `DbAdmin:AllowWriteQueries` in admin settings, and running a
 * write statement from the query pane. Confirm stays disabled until the typed input
 * matches `phrase` exactly. Wording is passed in already translated, since the two call
 * sites use different i18n namespaces.
 */
export function TypedPhraseConfirmDialog({
  phrase, input, onInput, onCancel, onConfirm, title, body, prompt, confirmLabel,
}: Readonly<{
  phrase: string;
  input: string;
  onInput: (v: string) => void;
  onCancel: () => void;
  onConfirm: () => void;
  title: string;
  body: string;
  /** Instruction line — the caller interpolates `phrase` into its own key. */
  prompt: string;
  confirmLabel: string;
}>) {
  const { t } = useTranslation(['common']);
  const ok = input === phrase;

  return (
    <div
      className="fixed inset-0 bg-black/30 backdrop-blur-sm flex items-center justify-center z-50"
      onClick={onCancel}
      onKeyDown={(e) => e.key === 'Escape' && onCancel()}
      role="presentation"
      tabIndex={-1}
    >
      <div
        className="bg-surface-lowest rounded-lg shadow-xl p-6 w-full max-w-md"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.stopPropagation()}
        role="presentation"
      >
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-lg font-semibold text-on-surface flex items-center gap-2">
            <SecurityServices size={18} className="text-amber-600" />
            {title}
          </h3>
          <button onClick={onCancel} className="p-1 text-on-surface-variant hover:bg-surface-container rounded">
            <Close size={16} />
          </button>
        </div>
        <p className="text-sm text-on-surface-variant mb-3">
          {body}
        </p>
        <p className="text-xs text-on-surface-variant mb-1">
          {prompt}
        </p>
        <input
          type="text"
          value={input}
          onChange={(e) => onInput(e.target.value)}
          autoFocus
          className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm font-mono focus:outline-none focus:ring-2 focus:ring-amber-500"
        />
        <div className="flex justify-end gap-2 mt-4">
          <button
            onClick={onCancel}
            className="px-4 py-2 text-sm text-on-surface-variant hover:bg-surface-container rounded-md"
          >
            {t('common:cancel')}
          </button>
          <button
            onClick={onConfirm}
            disabled={!ok}
            className="px-4 py-2 bg-amber-600 text-white text-sm rounded-md hover:bg-amber-700 disabled:opacity-50"
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
