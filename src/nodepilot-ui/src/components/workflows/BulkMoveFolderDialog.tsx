import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { DataBase, Folder } from '@carbon/icons-react';
import { ModalShell } from '../common/ModalShell';
import { ROOT_FOLDER_ID, sharedFoldersApi, type SharedFolder } from '../../api/sharedFolders';

/**
 * Destination picker for the bulk "move to folder" action.
 *
 * Deliberately NOT a reuse of SharedFolderTree: there, `onFolderSelected(null)` means "clear the
 * filter / show all folders", and a move has no such destination — every move needs exactly one
 * concrete folder. A flat list ordered by `path` and indented by `depth` carries the same tree
 * information without that ambiguity, and it reuses the `['shared-folders']` query the page has
 * already loaded, so opening the dialog costs no round-trip.
 *
 * Folders the caller cannot edit are listed but disabled — hiding them would make the tree look
 * different from the one in the sidebar and leave the user hunting for a folder that is simply
 * out of reach.
 */
export function BulkMoveFolderDialog({
  count,
  onCancel,
  onConfirm,
}: Readonly<{
  count: number;
  onCancel: () => void;
  onConfirm: (targetFolderId: string) => void;
}>) {
  const { t } = useTranslation(['workflows', 'common']);
  const [targetId, setTargetId] = useState<string | null>(null);

  const { data: folders, isLoading } = useQuery({
    queryKey: ['shared-folders'],
    queryFn: () => sharedFoldersApi.list(),
    staleTime: 30_000,
  });

  // Sort by path so parents precede their children and siblings stay alphabetical — the same
  // reading order as the sidebar tree, without needing to rebuild the tree structure.
  const ordered = useMemo(
    () => [...(folders ?? [])].sort((a, b) => a.path.localeCompare(b.path)),
    [folders],
  );

  // Root has no user-facing name of its own; the sidebar tree renders it as a bare backslash,
  // so this list does the same rather than inventing a second label for the same folder.
  const label = (f: SharedFolder) => (f.id === ROOT_FOLDER_ID ? '\\' : f.name);

  return (
    <ModalShell onClose={onCancel} maxWidth="max-w-lg">
      <div data-testid="bulk-move-dialog">
        <h3 className="text-lg font-semibold text-on-surface mb-1">{t('workflows:bulk.moveTitle')}</h3>
        <p className="text-sm text-outline mb-4">{t('workflows:bulk.moveHint', { count })}</p>

        {isLoading ? (
          <p className="text-outline text-sm py-4">{t('common:loadingDots')}</p>
        ) : (
          <div className="max-h-[45vh] overflow-y-auto rounded-md border border-outline-variant divide-y divide-outline-variant/30">
            {ordered.map((f) => {
              const selected = targetId === f.id;
              const disabled = !f.capabilities.canEdit;
              return (
                <button
                  key={f.id}
                  type="button"
                  disabled={disabled}
                  onClick={() => setTargetId(f.id)}
                  data-testid={`bulk-move-target-${f.id}`}
                  title={disabled ? t('workflows:bulk.noFolderPermission') : f.path}
                  className={`w-full flex items-center gap-2 px-3 py-2 text-left text-sm transition-colors
                    ${selected ? 'bg-blue-500/15 text-on-surface font-medium' : 'text-on-surface-variant hover:bg-surface-low'}
                    disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:bg-transparent`}
                  style={{ paddingLeft: `${12 + f.depth * 16}px` }}
                >
                  {f.id === ROOT_FOLDER_ID
                    ? <DataBase size={14} className="shrink-0 text-primary" />
                    : <Folder size={14} className="shrink-0 text-amber-400" />}
                  <span className="truncate">{label(f)}</span>
                  <span className="ml-auto text-xs text-outline tabular-nums shrink-0">{f.workflowCount}</span>
                </button>
              );
            })}
          </div>
        )}

        <div className="flex justify-end gap-2 mt-4">
          <button
            type="button"
            onClick={onCancel}
            className="px-4 py-2 bg-surface-container text-on-surface rounded-md hover:bg-surface-high text-sm"
          >
            {t('common:cancel')}
          </button>
          <button
            type="button"
            disabled={!targetId}
            onClick={() => targetId && onConfirm(targetId)}
            data-testid="bulk-move-confirm"
            className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 text-sm disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {t('workflows:bulk.moveConfirm')}
          </button>
        </div>
      </div>
    </ModalShell>
  );
}
