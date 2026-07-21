import { Add, ChevronDown, ChevronRight, DataBase, Folder, FolderOpen, Renew } from '@carbon/icons-react';
import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { sharedFoldersApi, ROOT_FOLDER_ID, type SharedFolder } from '../../api/sharedFolders';
import { SharedFolderContextMenu } from './SharedFolderContextMenu';
import { toast } from '../../stores/toastStore';
import { confirmDialog } from '../../stores/confirmStore';

/**
 * Sidebar that renders the org-level shared-folder tree from
 * <c>GET /api/shared-workflow-folders</c>. The selected folder id flows back to the
 * parent (WorkflowsPage) which uses it to filter the list of workflows. Folders the
 * caller can't read are not in the API response, so this component does no client-side
 * filtering — it just renders what the server returned.
 *
 * Capabilities-aware: the "+ New" button under a folder is only enabled when
 * <c>capabilities.canEdit</c> is true.
 */
export interface SharedFolderTreeProps {
  selectedFolderId: string | null;
  onFolderSelected: (folderId: string | null) => void;
  /** Optional callback fired when a folder is created — the parent uses it to refresh
   *  workflow counts / re-fetch lists. */
  onTreeMutated?: () => void;
  /** Drag-and-drop: caller sets this to enable workflow→folder drop handling on tree
   *  nodes. Receives the workflow id (read from dataTransfer "application/x-nodepilot-workflow")
   *  and the destination folder id. The caller is responsible for the API call,
   *  query invalidation, and error reporting. Drop is silently ignored on folders
   *  where the caller lacks canEdit. */
  onWorkflowDropped?: (workflowId: string, folderId: string) => void;
  /** When true, the "Shared Folders" header + refresh button are hidden (for embedding in
   *  narrow panels like the designer sidebar where the parent already provides context). */
  compact?: boolean;
  /** When true, folder management affordances are hidden: no "+ new subfolder" button,
   *  no right-click rename/delete context menu. Suitable for navigation-only use. */
  hideManagement?: boolean;
}

export const WORKFLOW_DRAG_MIME = 'application/x-nodepilot-workflow';

interface TreeNode {
  folder: SharedFolder;
  children: TreeNode[];
}

export function SharedFolderTree({
  selectedFolderId,
  onFolderSelected,
  onTreeMutated,
  onWorkflowDropped,
  compact = false,
  hideManagement = false,
}: Readonly<SharedFolderTreeProps>) {
  // Shared cache key with WorkflowsPage so any mutation that calls
  // `queryClient.invalidateQueries({queryKey: ['shared-folders']})` (workflow create,
  // workflow move-folder, permission grant, …) automatically refreshes the tree's
  // workflowCount badges. Previously the tree maintained its own useState + manual
  // reload(), which meant counts only updated when the user clicked the ↻ icon or
  // refreshed the page — every workflow mutation in the parent went unnoticed here.
  const queryClient = useQueryClient();
  const { data: folders, error: queryError, isLoading } = useQuery({
    queryKey: ['shared-folders'],
    queryFn: () => sharedFoldersApi.list(),
  });
  const [localError, setLocalError] = useState<string | null>(null);
  const error = localError ?? (queryError ? (queryError as Error).message : null);

  const { t } = useTranslation(['workflows', 'common']);
  const [busy, setBusy] = useState(false);
  const [creatingUnderId, setCreatingUnderId] = useState<string | null>(null);
  const [newFolderName, setNewFolderName] = useState('');
  // The folder id currently under a drag — drives the per-row drop-target highlight
  // without re-rendering the entire tree on every dragover frame.
  const [dragOverFolderId, setDragOverFolderId] = useState<string | null>(null);
  // Right-click context-menu state. Position is in viewport-coords (clientX/clientY)
  // because the menu uses position:fixed. Only set when the row qualifies (canEdit AND
  // non-Root); otherwise the browser's default menu shows.
  const [menuState, setMenuState] = useState<{ x: number; y: number; folder: SharedFolder } | null>(null);
  // Inline-rename state — similar to the create flow below, except the folder row
  // itself is swapped for an input instead of a new input appearing underneath it.
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState('');

  // Empty set = all expanded (default). Adding an id collapses that branch.
  const [collapsedIds, setCollapsedIds] = useState<Set<string>>(new Set());
  const toggleCollapse = (id: string) =>
    setCollapsedIds(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });

  const reload = () => {
    setLocalError(null);
    queryClient.invalidateQueries({ queryKey: ['shared-folders'] });
  };

  const tree = useMemo(() => buildTree(folders ?? []), [folders]);

  const submitCreate = async (parentId: string | null) => {
    if (!newFolderName.trim()) return;
    setBusy(true);
    setLocalError(null);
    try {
      await sharedFoldersApi.create(parentId, newFolderName.trim());
      setCreatingUnderId(null);
      setNewFolderName('');
      await queryClient.invalidateQueries({ queryKey: ['shared-folders'] });
      onTreeMutated?.();
    } catch (e) {
      setLocalError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const submitRename = async (folder: SharedFolder) => {
    const trimmed = renameValue.trim();
    if (!trimmed) {
      setRenamingId(null);
      return;
    }
    if (trimmed === folder.name) {
      // No-op: same as the existing name → skip the backend call and just close.
      setRenamingId(null);
      return;
    }
    setBusy(true);
    setLocalError(null);
    try {
      await sharedFoldersApi.rename(folder.id, trimmed);
      setRenamingId(null);
      setRenameValue('');
      await queryClient.invalidateQueries({ queryKey: ['shared-folders'] });
      onTreeMutated?.();
    } catch (e) {
      // 400 (empty / >120 characters) or 409 (sibling name collision) — the backend
      // response body already carries a user-friendly message, which we surface as-is
      // in the error banner.
      setLocalError(t('workflows:folder.renameFailed', {
        defaultValue: 'Umbenennen fehlgeschlagen: {{msg}}',
        msg: (e as Error).message,
      }));
    } finally {
      setBusy(false);
    }
  };

  const confirmAndDelete = async (folder: SharedFolder) => {
    const ok = await confirmDialog({
      message: t('workflows:folder.deleteConfirm', {
        defaultValue: 'Folder "{{name}}" wirklich löschen?',
        name: folder.name,
      }),
      danger: true,
    });
    if (!ok) return;
    setBusy(true);
    setLocalError(null);
    try {
      await sharedFoldersApi.delete(folder.id);
      await queryClient.invalidateQueries({ queryKey: ['shared-folders'] });
      // If the deleted folder was the one currently selected, reset the selection to
      // root — otherwise WorkflowsPage would show "No workflows in this folder" for an
      // id that no longer exists.
      if (selectedFolderId === folder.id) onFolderSelected(ROOT_FOLDER_ID);
      onTreeMutated?.();
    } catch (e) {
      // 409 conflict (folder not empty) or 400/403 — the backend message is already
      // user-friendly (e.g. "Folder is not empty — move or delete sub-folders and
      // workflows first"). Pass it through as-is.
      toast.error(t('workflows:folder.deleteFailed', {
        defaultValue: 'Löschen fehlgeschlagen: {{msg}}',
        msg: (e as Error).message,
      }));
    } finally {
      setBusy(false);
    }
  };

  const renderNode = (node: TreeNode, depth: number) => {
    const isSelected = node.folder.id === selectedFolderId;
    const isRoot = node.folder.id === ROOT_FOLDER_ID;
    const canEdit = node.folder.capabilities.canEdit;
    const dragEnabled = !!onWorkflowDropped && canEdit;
    const isDropTarget = dragOverFolderId === node.folder.id;
    const isRenaming = renamingId === node.folder.id;
    return (
      <li key={node.folder.id} className="select-none">
        {isRenaming ? (
          <div className="flex items-center gap-1 px-2 py-1" style={{ paddingLeft: `${depth * 12 + 8}px` }}>
            <input
              autoFocus
              type="text"
              className="flex-1 rounded border border-outline-variant bg-surface-lowest text-on-surface px-2 py-0.5 text-sm focus:outline-none focus:border-primary"
              value={renameValue}
              onChange={(e) => setRenameValue(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') submitRename(node.folder);
                if (e.key === 'Escape') {
                  setRenamingId(null);
                  setRenameValue('');
                }
              }}
              disabled={busy}
              data-testid="shared-folder-rename-input"
            />
            <button
              type="button"
              className="rounded bg-primary px-3 py-0.5 text-xs text-on-primary hover:bg-primary-container hover:text-on-primary-container disabled:opacity-50 transition-colors"
              onClick={() => submitRename(node.folder)}
              disabled={busy || !renameValue.trim()}
            >
              OK
            </button>
          </div>
        ) : (
        <div
          className={`flex items-center gap-1.5 rounded pr-2 py-0.5 text-xs cursor-pointer transition-colors ${
            isSelected
              ? 'bg-primary-fixed text-on-primary-fixed font-medium'
              : 'text-on-surface hover:bg-surface-container'
          } ${isDropTarget ? 'ring-2 ring-primary bg-primary-container/40' : ''}`}
          style={{ paddingLeft: `${depth * 12 + 8}px` }}
          onClick={() => onFolderSelected(node.folder.id)}
          onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && onFolderSelected(node.folder.id)}
          role="treeitem"
          tabIndex={0}
          aria-selected={isSelected}
          onContextMenu={(e) => {
            if (isRoot || !canEdit || hideManagement) return;
            e.preventDefault();
            setMenuState({ x: e.clientX, y: e.clientY, folder: node.folder });
          }}
          data-testid={`shared-folder-${node.folder.id}`}
          onDragOver={(e) => {
            if (!dragEnabled) return;
            if (!e.dataTransfer.types.includes(WORKFLOW_DRAG_MIME)) return;
            // preventDefault is what tells the browser this element accepts the drop —
            // without it, onDrop never fires.
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            if (dragOverFolderId !== node.folder.id) setDragOverFolderId(node.folder.id);
          }}
          onDragLeave={(e) => {
            // Only clear when leaving the row itself, not when crossing into a child element
            // (relatedTarget contained inside the row counts as still-inside).
            if (e.currentTarget.contains(e.relatedTarget as Node | null)) return;
            if (dragOverFolderId === node.folder.id) setDragOverFolderId(null);
          }}
          onDrop={(e) => {
            if (!dragEnabled) return;
            const workflowId = e.dataTransfer.getData(WORKFLOW_DRAG_MIME);
            setDragOverFolderId(null);
            if (!workflowId) return;
            e.preventDefault();
            onWorkflowDropped?.(workflowId, node.folder.id);
          }}
        >
          {/* Chevron toggle or spacer for leaf nodes — both w-4 for consistent text alignment */}
          {node.children.length > 0 ? (
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); toggleCollapse(node.folder.id); }}
              className="shrink-0 w-4 h-4 flex items-center justify-center rounded hover:bg-black/10 dark:hover:bg-white/10 transition-colors"
              aria-label={collapsedIds.has(node.folder.id) ? 'Ausklappen' : 'Einklappen'}
            >
              {collapsedIds.has(node.folder.id)
                ? <ChevronRight size={10} />
                : <ChevronDown size={10} />}
            </button>
          ) : (
            <span className="w-4 shrink-0" aria-hidden />
          )}

          {/* Folder icon */}
          {isRoot
            ? <DataBase size={12} className="shrink-0 text-primary" />
            : (node.children.length > 0 && !collapsedIds.has(node.folder.id))
              ? <FolderOpen size={12} className="shrink-0 text-amber-500" />
              : <Folder size={12} className="shrink-0 text-amber-400" />
          }

          <span className="flex-1 truncate">
            {isRoot ? '\\' : node.folder.name}
          </span>
          <span className={`text-xs ${isSelected ? 'text-on-primary-fixed/80' : 'text-on-surface-variant'}`}>
            {node.folder.workflowCount}
          </span>
          {canEdit && !hideManagement && (
            <button
              type="button"
              className={`ml-0.5 hover:text-on-surface transition-colors ${
                isSelected ? 'text-on-primary-fixed/80 hover:text-on-primary-fixed' : 'text-on-surface-variant'
              }`}
              title={t('workflows:folder.createSubfolder')}
              onClick={(e) => {
                e.stopPropagation();
                setCreatingUnderId(node.folder.id);
                setNewFolderName('');
              }}
            >
              <Add size={10} />
            </button>
          )}
        </div>
        )}
        {creatingUnderId === node.folder.id && (
          <div className="flex items-center gap-1 px-2 py-1" style={{ paddingLeft: `${(depth + 1) * 12 + 8}px` }}>
            <input
              autoFocus
              type="text"
              className="flex-1 rounded border border-outline-variant bg-surface-lowest text-on-surface px-2 py-0.5 text-sm focus:outline-none focus:border-primary"
              placeholder={t('workflows:folder.newFolderPlaceholder')}
              value={newFolderName}
              onChange={(e) => setNewFolderName(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') submitCreate(node.folder.id);
                if (e.key === 'Escape') {
                  setCreatingUnderId(null);
                  setNewFolderName('');
                }
              }}
              disabled={busy}
              data-testid="shared-folder-create-input"
            />
            <button
              type="button"
              className="rounded bg-primary px-3 py-0.5 text-xs text-on-primary hover:bg-primary-container hover:text-on-primary-container disabled:opacity-50 transition-colors"
              onClick={() => submitCreate(node.folder.id)}
              disabled={busy || !newFolderName.trim()}
            >
              OK
            </button>
          </div>
        )}
        {node.children.length > 0 && !collapsedIds.has(node.folder.id) && (
          <ul>{node.children.map((c) => renderNode(c, depth + 1))}</ul>
        )}
      </li>
    );
  };

  return (
    <div className="flex h-full flex-col" data-testid="shared-folder-tree">
      {!compact && (
        <div className="flex items-center justify-between border-b border-outline-variant px-3 py-2">
          <h3 className="text-sm font-semibold text-on-surface">{t('workflows:folder.sharedFoldersHeading')}</h3>
          <button
            type="button"
            className="rounded px-1 text-xs text-on-surface-variant hover:text-on-surface hover:bg-surface-container transition-colors"
            title={t('workflows:folder.refresh')}
            onClick={reload}
            disabled={busy}
          >
            <Renew size={12} />
          </button>
        </div>
      )}
      <div className="flex-1 overflow-auto">
        {error && (
          <div className="px-3 py-2 text-xs text-error">
            Fehler beim Laden: {error}
          </div>
        )}
        {isLoading && !error && (
          <div className="px-3 py-2 text-xs text-on-surface-variant">Lade …</div>
        )}
        {folders && (
          <ul>{tree.map((n) => renderNode(n, 0))}</ul>
        )}
      </div>
      {menuState && (
        <SharedFolderContextMenu
          x={menuState.x}
          y={menuState.y}
          onRename={() => {
            setRenamingId(menuState.folder.id);
            setRenameValue(menuState.folder.name);
            setLocalError(null);
          }}
          onDelete={() => {
            confirmAndDelete(menuState.folder);
          }}
          onClose={() => setMenuState(null)}
        />
      )}
    </div>
  );
}

/** Build a parent-child tree from a flat folder list. Stable sort: depth, then name. */
function buildTree(folders: SharedFolder[]): TreeNode[] {
  const sorted = [...folders].sort((a, b) =>
    a.depth !== b.depth ? a.depth - b.depth : a.name.localeCompare(b.name),
  );
  const byId = new Map<string, TreeNode>();
  for (const f of sorted) byId.set(f.id, { folder: f, children: [] });

  const roots: TreeNode[] = [];
  for (const node of byId.values()) {
    if (node.folder.parentFolderId == null) {
      roots.push(node);
    } else {
      const parent = byId.get(node.folder.parentFolderId);
      if (parent) parent.children.push(node);
      else roots.push(node);  // parent not visible → render as orphan root
    }
  }
  return roots;
}
