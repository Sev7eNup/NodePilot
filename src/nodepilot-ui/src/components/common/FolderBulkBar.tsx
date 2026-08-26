import { Close, TrashCan } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';

export interface FolderBulkBarProps {
  selectedCount: number;
  onDelete: () => void;
  onClear: () => void;
  disabled?: boolean;
  /** i18n namespace carrying the `folder.bulk.*` keys — `workflows` or `globals`. */
  ns: string;
}

/**
 * Action bar above a folder tree while rows are selected. Shared by both trees so the gesture
 * reads the same on /workflows and /globals; only the namespace of the labels differs.
 */
export function FolderBulkBar({
  selectedCount, onDelete, onClear, disabled = false, ns,
}: Readonly<FolderBulkBarProps>) {
  const { t } = useTranslation([ns]);
  if (selectedCount === 0) return null;

  return (
    <div
      className="flex items-center gap-2 border-b border-outline-variant bg-surface-container px-3 py-1.5"
      data-testid="folder-bulk-bar"
    >
      <span className="text-xs font-medium text-on-surface">
        {t(`${ns}:folder.bulk.selected`, { count: selectedCount })}
      </span>
      <button
        type="button"
        className="ml-auto flex items-center gap-1 rounded px-2 py-0.5 text-xs text-error hover:bg-error-container disabled:opacity-50 transition-colors"
        onClick={onDelete}
        disabled={disabled}
        data-testid="folder-bulk-delete"
      >
        <TrashCan size={12} />
        {t(`${ns}:folder.bulk.delete`)}
      </button>
      <button
        type="button"
        className="rounded p-0.5 text-on-surface-variant hover:bg-surface-high transition-colors"
        onClick={onClear}
        title={t(`${ns}:folder.bulk.clear`)}
        aria-label={t(`${ns}:folder.bulk.clear`)}
        data-testid="folder-bulk-clear"
      >
        <Close size={12} />
      </button>
    </div>
  );
}
