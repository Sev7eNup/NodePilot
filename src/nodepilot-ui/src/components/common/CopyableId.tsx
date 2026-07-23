import { useState, type MouseEvent } from 'react';
import { Checkmark } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';

/**
 * Inline copyable id chip: renders `(<short>)` with an 8-char prefix of `id` and copies the
 * FULL id to the clipboard on click. Matches the Executions-page convention (short prefix
 * visible, full id in the tooltip via `executions:copyId`, click copies the full id). A
 * checkmark flashes for ~1.5s as feedback. Clipboard errors (insecure context, jsdom) are
 * swallowed silently — no crash, just no checkmark.
 */
export function CopyableId({ id, className }: Readonly<{ id: string; className?: string }>) {
  const { t } = useTranslation(['executions', 'common']);
  const [copied, setCopied] = useState(false);

  const copy = async (e: MouseEvent) => {
    e.stopPropagation();
    try {
      await navigator.clipboard.writeText(id);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard not available — ignore.
    }
  };

  const tooltip = copied ? t('common:copied') : t('executions:copyId', { id });

  return (
    <button
      type="button"
      onClick={copy}
      title={tooltip}
      aria-label={tooltip}
      className={`np-copyable-id shrink-0 rounded px-1 py-0.5 font-mono text-[10px] text-outline transition-colors hover:bg-surface-high hover:text-primary ${className ?? ''}`}
    >
      ({id.slice(0, 8)})
      {copied && <Checkmark size={10} className="ml-0.5 inline-block text-green-600 dark:text-green-400" aria-hidden="true" />}
    </button>
  );
}

export default CopyableId;