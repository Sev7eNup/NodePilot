import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Close, Download, FolderMoveTo, Power, TrashCan } from '@carbon/icons-react';
import type { Workflow } from '../../types/api';
import type { WorkflowBulkActions } from '../../hooks/useWorkflowBulkActions';
import { BulkMoveFolderDialog } from './BulkMoveFolderDialog';

export interface WorkflowBulkBarProps {
  selected: Workflow[];
  actions: WorkflowBulkActions;
  onClear: () => void;
  canDelete: (w: Workflow) => boolean;
  canEdit: (w: Workflow) => boolean;
  /** Global Admin/Operator — the roles the export endpoint accepts. */
  canExport: boolean;
}

/**
 * Action bar for the workflow list's multi-selection. Presentational: the work itself lives in
 * `useWorkflowBulkActions`, because the drag-and-drop path needs the same move action without
 * going through a button here.
 *
 * Gating rule: a button is enabled only when the WHOLE selection qualifies. Acting on a subset
 * and silently skipping the rest reads as "it worked" when it half did; a disabled button whose
 * tooltip names the reason is the honest version.
 */
export function WorkflowBulkBar({
  selected, actions, onClear, canDelete, canEdit, canExport,
}: Readonly<WorkflowBulkBarProps>) {
  const { t } = useTranslation(['workflows', 'common']);
  const [moveOpen, setMoveOpen] = useState(false);

  const count = selected.length;
  const { progress, running } = actions;

  const allDeletable = count > 0 && selected.every(canDelete);
  const allEditable = count > 0 && selected.every(canEdit);
  // POST /enable refuses ANY checked-out workflow with 423 — including one the caller locked
  // themselves — so a locked row in the selection disables Enable rather than failing N times.
  const lockedCount = selected.filter((w) => !!w.checkedOutByUserId).length;

  const btn = 'flex items-center gap-1.5 px-3 py-1.5 rounded-md text-sm transition-colors disabled:opacity-40 disabled:cursor-not-allowed';
  const neutral = `${btn} bg-surface-lowest border border-outline-variant text-on-surface hover:bg-surface-low`;

  return (
    <>
      <div
        data-testid="workflow-bulk-bar"
        className="np-card sticky top-0 z-20 mb-3 px-3 py-2 flex flex-wrap items-center gap-2 np-fade-up"
      >
        <span className="text-sm font-semibold text-on-surface whitespace-nowrap">
          {t('workflows:bulk.selected', { count })}
        </span>

        {running && progress ? (
          <>
            <span className="text-sm text-outline tabular-nums truncate">
              {t('workflows:bulk.progress', {
                done: progress.done, total: progress.total, name: progress.current,
              })}
            </span>
            <button
              type="button"
              onClick={actions.requestAbort}
              disabled={actions.abortRequested}
              data-testid="bulk-cancel"
              className={`${btn} ml-auto bg-surface-container text-on-surface hover:bg-surface-high`}
            >
              {actions.abortRequested ? t('workflows:bulk.cancelling') : t('common:cancel')}
            </button>
          </>
        ) : (
          <>
            <span className="w-px h-5 bg-outline-variant/60 mx-1" />

            <button
              type="button"
              onClick={() => setMoveOpen(true)}
              disabled={!allEditable}
              title={allEditable ? undefined : t('workflows:bulk.noPermission')}
              data-testid="bulk-move"
              className={neutral}
            >
              <FolderMoveTo size={16} /> {t('workflows:bulk.move')}
            </button>

            <button
              type="button"
              onClick={() => actions.setEnabled(selected, true)}
              disabled={!allEditable || lockedCount > 0}
              title={
                lockedCount > 0
                  ? t('workflows:bulk.enableBlockedByLock', { count: lockedCount })
                  : (allEditable ? undefined : t('workflows:bulk.noPermission'))
              }
              data-testid="bulk-enable"
              className={`${btn} bg-surface-lowest border border-outline-variant text-green-600 hover:bg-green-500/10`}
            >
              <Power size={16} /> {t('workflows:bulk.enable')}
            </button>

            <button
              type="button"
              onClick={() => actions.setEnabled(selected, false)}
              disabled={!allEditable}
              title={allEditable ? undefined : t('workflows:bulk.noPermission')}
              data-testid="bulk-disable"
              className={`${btn} bg-surface-lowest border border-outline-variant text-on-surface-variant hover:bg-surface-low`}
            >
              <Power size={16} /> {t('workflows:bulk.disable')}
            </button>

            {canExport && (
              <button
                type="button"
                onClick={() => actions.exportWorkflows(selected)}
                data-testid="bulk-export"
                className={neutral}
              >
                <Download size={16} /> {t('workflows:bulk.export')}
              </button>
            )}

            <button
              type="button"
              onClick={() => actions.deleteWorkflows(selected)}
              disabled={!allDeletable}
              title={allDeletable ? undefined : t('workflows:bulk.noDeletePermission')}
              data-testid="bulk-delete"
              className={`${btn} ml-auto bg-red-600 text-white hover:bg-red-700`}
            >
              <TrashCan size={16} /> {t('workflows:bulk.delete')}
            </button>

            <button
              type="button"
              onClick={onClear}
              title={t('workflows:bulk.clear')}
              aria-label={t('workflows:bulk.clear')}
              data-testid="bulk-clear"
              className="p-1.5 rounded-md text-on-surface-variant hover:bg-surface-container transition-colors"
            >
              <Close size={16} />
            </button>
          </>
        )}
      </div>

      {moveOpen && (
        <BulkMoveFolderDialog
          count={count}
          onCancel={() => setMoveOpen(false)}
          onConfirm={(targetFolderId) => {
            setMoveOpen(false);
            void actions.moveWorkflows(selected, targetFolderId);
          }}
        />
      )}
    </>
  );
}
