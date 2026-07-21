import { CircleDash, Close, MagicWandFilled } from '@carbon/icons-react';
import { useState, useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';

interface Props {
  title: string;
  /** Sub-headline under the title — e.g. "Describe what the script should do". */
  subtitle?: string;
  placeholder?: string;
  /** Label for the submit button. Defaults to "Generate". */
  submitLabel?: string;
  /** Optional "replace the entire editor content" toggle — useful for script generation. */
  showReplaceToggle?: boolean;
  defaultReplaceAll?: boolean;
  /**
   * Called with the (trimmed) prompt plus the replace toggle. On success the dialog
   * closes itself automatically; if the callback throws, its message is shown as an
   * error and the dialog stays open.
   */
  onSubmit: (prompt: string, replaceAll: boolean) => Promise<void>;
  onClose: () => void;
}

/**
 * Generic input dialog for AI calls (script generation + workflow generation).
 * Owns its own loading/error state; the caller only gets a thin
 * `onSubmit(prompt, replaceAll)` interface.
 */
export function AiPromptDialog({
  title,
  subtitle,
  placeholder,
  submitLabel,
  showReplaceToggle = false,
  defaultReplaceAll = false,
  onSubmit,
  onClose,
}: Readonly<Props>) {
  const { t } = useTranslation(['ai', 'common']);
  const submitLabelResolved = submitLabel ?? t('ai:scriptDialog.generate');
  const [prompt, setPrompt] = useState('');
  const [replaceAll, setReplaceAll] = useState(defaultReplaceAll);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);

  // Auto-focus the text field when the dialog opens so the user can start typing immediately.
  useEffect(() => { textareaRef.current?.focus(); }, []);

  const handleSubmit = useCallback(async () => {
    const trimmed = prompt.trim();
    if (!trimmed || submitting) return;
    setError(null);
    setSubmitting(true);
    try {
      await onSubmit(trimmed, replaceAll);
      // Closing does NOT happen automatically here — the caller closes the dialog
      // after inserting the result. That gives it a chance to sync editor state
      // before the dialog disappears.
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(msg);
    } finally {
      setSubmitting(false);
    }
  }, [prompt, replaceAll, submitting, onSubmit]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (e.key === 'Escape' && !submitting) onClose();
    // Ctrl/Cmd+Enter submits — the usual convention for multi-line prompt fields.
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      handleSubmit();
    }
  }, [onClose, submitting, handleSubmit]);

  return (
    <div
      className="fixed inset-0 z-[60] bg-black/30 backdrop-blur-sm flex items-center justify-center"
      role="dialog"
      aria-modal="true"
      aria-labelledby="ai-prompt-dialog-title"
      onKeyDown={handleKeyDown}
    >
      <div className="bg-surface-lowest rounded-xl shadow-2xl ring-1 ring-outline-variant/20 w-full max-w-lg flex flex-col overflow-hidden">

        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 bg-surface-low border-b border-outline-variant/20">
          <div className="flex items-center gap-2">
            <MagicWandFilled size={16} className="text-primary" />
            <span id="ai-prompt-dialog-title" className="text-sm font-headline font-bold text-on-surface">{title}</span>
          </div>
          <button
            onClick={onClose}
            disabled={submitting}
            className="p-1 text-on-surface-variant hover:text-error hover:bg-error-container/30 rounded transition-colors disabled:opacity-40"
            aria-label={t('common:close')}
          >
            <Close size={14} />
          </button>
        </div>

        <div className="px-4 py-4 space-y-3">
          {subtitle && (
            <p className="text-xs text-on-surface-variant font-label leading-snug">{subtitle}</p>
          )}

          <textarea
            ref={textareaRef}
            value={prompt}
            onChange={(e) => setPrompt(e.target.value)}
            placeholder={placeholder ?? t('ai:scriptDialog.promptPlaceholder')}
            rows={6}
            disabled={submitting}
            aria-label="AI prompt"
            className="w-full input-field font-mono text-sm resize-y"
          />

          {showReplaceToggle && (
            <label className="flex items-center gap-2 cursor-pointer text-xs text-on-surface select-none">
              <input
                type="checkbox"
                checked={replaceAll}
                onChange={(e) => setReplaceAll(e.target.checked)}
                disabled={submitting}
                className="w-4 h-4 rounded border-outline-variant accent-primary"
              />
              <span>{t('ai:scriptDialog.replaceAll')}</span>
            </label>
          )}

          {error && (
            <div
              role="alert"
              className="bg-error-container/20 border border-error/30 rounded px-2 py-1.5 text-xs text-on-error-container font-label whitespace-pre-wrap"
            >
              {error}
            </div>
          )}

          <p className="text-[10px] font-label text-on-surface-variant leading-snug">
            <strong className="text-amber-700">{t('ai:scriptDialog.warningPrefix', { defaultValue: 'Hinweis:' })}</strong> {t('ai:scriptDialog.reviewWarning', { defaultValue: 'KI-Output bitte vor dem Speichern lesen. Schadhafte Anweisungen können in Upstream-Daten eingeschleust sein.' })}
          </p>
        </div>

        {/* Footer */}
        <div className="flex items-center justify-end gap-2 px-4 py-3 bg-surface-low border-t border-outline-variant/20">
          <button
            onClick={onClose}
            disabled={submitting}
            className="px-3 py-1.5 text-xs font-label text-on-surface-variant hover:text-on-surface hover:bg-surface-high rounded-md transition-colors disabled:opacity-40"
          >
            {t('common:cancel')}
          </button>
          <button
            onClick={handleSubmit}
            disabled={submitting || prompt.trim().length === 0}
            className="flex items-center gap-1.5 px-4 py-1.5 bg-gradient-to-br from-primary to-primary-container text-on-primary text-xs font-label font-semibold rounded-md shadow-sm hover:shadow-lg hover:brightness-110 disabled:opacity-50 disabled:cursor-not-allowed transition-all cursor-pointer"
          >
            {submitting ? <CircleDash size={12} className="animate-spin" /> : <MagicWandFilled size={12} />}
            {submitting ? t('ai:scriptDialog.generating') : submitLabelResolved}
          </button>
        </div>
      </div>
    </div>
  );
}

export default AiPromptDialog;
