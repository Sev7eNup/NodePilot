import { DirectionMerge } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import type { SettingsSectionResponse } from '../../api/adminSettings';

type Props = {
  open: boolean;
  /** Server snapshot returned in the 412 body — the "their values" side of the merge. */
  serverSnapshot: SettingsSectionResponse<unknown> | null;
  /** Operator's draft — the "my values" side of the merge. */
  localDraft: unknown;
  /** Operator picks "overwrite server with my draft": parent retries the PUT with the server's
   * current ETag. */
  onKeepMine: () => void;
  /** Operator picks "discard my changes": parent replaces the form state with the server snapshot. */
  onTakeTheirs: () => void;
  /** Operator dismisses the dialog without resolving: stays in the form, no save happens. */
  onCancel: () => void;
};

/**
 * Conflict dialog shown when a PUT returns 412. Shows the server's current state and the
 * operator's pending draft side by side as JSON, with three choices: keep mine, take theirs,
 * or cancel and resolve it by hand in the form. Shows both sides rather than offering an
 * in-place merge editor, since operators tend to just redo their changes on the new state.
 */
export function EtagConflictDialog({ open, serverSnapshot, localDraft, onKeepMine, onTakeTheirs, onCancel }: Readonly<Props>) {
  const { t } = useTranslation(['adminSettings']);
  if (!open || !serverSnapshot) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/30 backdrop-blur-sm">
      <div className="bg-surface-lowest rounded-lg shadow-xl w-full max-w-3xl p-6 space-y-4">
        <div className="flex items-center gap-2">
          <DirectionMerge size={18} className="text-amber-600" />
          <h3 className="text-lg font-semibold text-on-surface">{t('adminSettings:etagConflictTitle')}</h3>
        </div>
        <p className="text-sm text-on-surface-variant">{t('adminSettings:etagConflictBody')}</p>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <div>
            <h4 className="text-xs font-medium uppercase tracking-wide text-on-surface-variant mb-1">
              {t('adminSettings:etagConflictKeepMine')}
            </h4>
            <pre className="text-xs bg-surface-low rounded p-3 max-h-72 overflow-auto whitespace-pre-wrap break-words">
{JSON.stringify(localDraft, null, 2)}
            </pre>
          </div>
          <div>
            <h4 className="text-xs font-medium uppercase tracking-wide text-on-surface-variant mb-1">
              {t('adminSettings:etagConflictTakeTheirs')}
            </h4>
            <pre className="text-xs bg-surface-low rounded p-3 max-h-72 overflow-auto whitespace-pre-wrap break-words">
{JSON.stringify(serverSnapshot.payload, null, 2)}
            </pre>
          </div>
        </div>

        <div className="flex flex-wrap gap-2 justify-end">
          <button
            type="button"
            onClick={onCancel}
            className="px-4 py-2 text-sm rounded-md text-on-surface hover:bg-surface-low"
          >
            {t('adminSettings:etagConflictCancel')}
          </button>
          <button
            type="button"
            onClick={onTakeTheirs}
            className="px-4 py-2 text-sm rounded-md bg-surface-high text-on-surface hover:bg-surface-highest"
          >
            {t('adminSettings:etagConflictTakeTheirs')}
          </button>
          <button
            type="button"
            onClick={onKeepMine}
            className="px-4 py-2 text-sm rounded-md bg-blue-600 text-white hover:bg-blue-700"
          >
            {t('adminSettings:etagConflictKeepMine')}
          </button>
        </div>
      </div>
    </div>
  );
}
