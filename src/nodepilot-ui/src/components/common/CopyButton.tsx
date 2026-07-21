import { Checkmark, Copy } from '@carbon/icons-react';
import { useCallback, useState } from 'react';
import { useTranslation } from 'react-i18next';

/**
 * Small copy button: writes `text` to the clipboard and shows a checkmark for ~1.5s.
 * Reusable across the app (AI answers, code blocks). Clipboard errors (e.g. insecure
 * context, or jsdom in tests) are swallowed silently — no crash, just no checkmark.
 */
export function CopyButton({
  text, className, size = 13,
}: Readonly<{ text: string; className?: string; size?: number }>) {
  const { t } = useTranslation('common');
  const [copied, setCopied] = useState(false);

  const copy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard not available — ignore.
    }
  }, [text]);

  return (
    <button
      type="button"
      onClick={copy}
      title={copied ? t('copied') : t('copy')}
      aria-label={copied ? t('copied') : t('copy')}
      className={className ?? 'rounded p-1 text-on-surface-variant transition-colors hover:bg-surface-high hover:text-on-surface'}
    >
      {copied ? <Checkmark size={size} className="text-green-600 dark:text-green-400" /> : <Copy size={size} />}
    </button>
  );
}

export default CopyButton;
