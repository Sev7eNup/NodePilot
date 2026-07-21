import { ArrowUp, ChevronRight, FlowModeler, Folder } from '@carbon/icons-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ROOT_FOLDER_ID, type SharedFolder } from '../../api/sharedFolders';
import type { Workflow } from '../../types/api';

interface Props {
  /** Folder whose contents the popover opens on. */
  startFolderId: string;
  /** Full (RBAC-visible) folder tree — used for sub-folders + drill-in. */
  folders: SharedFolder[];
  /** Workflow list to filter by folder membership. */
  workflows: Workflow[];
  currentWorkflowId: string | undefined;
  onOpenWorkflow: (w: Workflow) => void;
  /** Hover bridge — keep the popover open while the pointer is inside it. */
  onMouseEnter: () => void;
  onMouseLeave: () => void;
}

const byName = (a: { name: string }, b: { name: string }) => a.name.localeCompare(b.name);

/**
 * Mini file-browser rendered inside a breadcrumb segment's popover. Lists the sub-folders
 * and the workflows that live directly in the viewed folder. Clicking a sub-folder drills
 * in (internal `viewFolderId` state); clicking a workflow opens it. An "up one level" button
 * navigates back as long as the parent folder is itself visible (RBAC).
 */
export function FolderContentsBrowser({
  startFolderId, folders, workflows, currentWorkflowId, onOpenWorkflow, onMouseEnter, onMouseLeave,
}: Readonly<Props>) {
  const { t } = useTranslation('designer');
  const [viewFolderId, setViewFolderId] = useState(startFolderId);

  const byId = useMemo(() => new Map(folders.map((f) => [f.id, f] as const)), [folders]);
  const view = byId.get(viewFolderId) ?? null;
  const parent = view?.parentFolderId ? byId.get(view.parentFolderId) ?? null : null;

  const subfolders = useMemo(
    () => folders.filter((f) => f.parentFolderId === viewFolderId).sort(byName),
    [folders, viewFolderId],
  );
  const folderWorkflows = useMemo(
    () => workflows.filter((w) => (w.folderId ?? ROOT_FOLDER_ID) === viewFolderId).sort(byName),
    [workflows, viewFolderId],
  );

  return (
    <div
      className="flex min-w-0 flex-col text-xs"
      onMouseEnter={onMouseEnter}
      onMouseLeave={onMouseLeave}
      data-testid="folder-contents-browser"
    >
      {/* Header: viewed folder + up-one-level */}
      <div className="flex items-center gap-1.5 border-b border-outline-variant/30 bg-surface-container/50 px-2.5 py-1.5">
        {parent && (
          <button
            type="button"
            className="shrink-0 rounded p-0.5 text-on-surface-variant transition-colors hover:bg-surface-high hover:text-on-surface"
            onClick={() => setViewFolderId(parent.id)}
            title={t('folderPath.upOneLevel')}
            aria-label={t('folderPath.upOneLevel')}
          >
            <ArrowUp size={13} />
          </button>
        )}
        <Folder size={13} className="shrink-0 text-amber-500" />
        <span className="truncate font-semibold text-on-surface">
          {view ? view.name : t('folderPath.homeLabel')}
        </span>
      </div>
      <div className="overflow-y-auto py-1" style={{ maxHeight: 360 }}>
        {/* Sub-folders */}
        {subfolders.length > 0 && (
          <>
            <div className="px-2.5 pb-0.5 pt-1 text-[10px] font-semibold uppercase tracking-wide text-on-surface-variant/70">
              {t('folderPath.subfolders')}
            </div>
            {subfolders.map((f) => (
              <button
                key={f.id}
                type="button"
                className="flex w-full items-center gap-2 px-2.5 py-1 text-left text-on-surface transition-colors hover:bg-surface-high"
                onClick={() => setViewFolderId(f.id)}
                title={t('folderPath.openFolder', { name: f.name })}
              >
                <Folder size={13} className="shrink-0 text-amber-400" />
                <span className="flex-1 truncate">{f.name}</span>
                <span className="shrink-0 text-on-surface-variant">{f.workflowCount}</span>
                <ChevronRight size={12} className="shrink-0 text-on-surface-variant/60" />
              </button>
            ))}
          </>
        )}

        {/* Workflows */}
        <div className="px-2.5 pb-0.5 pt-1.5 text-[10px] font-semibold uppercase tracking-wide text-on-surface-variant/70">
          {t('folderPath.workflows')}
        </div>
        {folderWorkflows.length === 0 ? (
          <div className="px-2.5 py-1 text-on-surface-variant">{t('browser.noWorkflowsInFolder')}</div>
        ) : (
          folderWorkflows.map((w) => {
            const isCurrent = w.id === currentWorkflowId;
            return (
              <button
                key={w.id}
                type="button"
                className={`flex w-full items-center gap-2 px-2.5 py-1 text-left transition-colors hover:bg-surface-high ${
                  isCurrent ? 'text-primary' : 'text-on-surface'
                }`}
                onClick={() => onOpenWorkflow(w)}
                title={isCurrent ? t('browser.currentWorkflow') : t('browser.openWorkflow')}
              >
                <FlowModeler size={13} className={`shrink-0 ${isCurrent ? 'text-primary' : 'text-on-surface-variant'}`} />
                <span className="flex-1 truncate">{w.name}</span>
                {isCurrent && (
                  <span className="shrink-0 rounded bg-primary-fixed px-1 text-[10px] font-semibold uppercase text-on-primary-fixed">
                    {t('browser.current')}
                  </span>
                )}
              </button>
            );
          })
        )}
      </div>
    </div>
  );
}
