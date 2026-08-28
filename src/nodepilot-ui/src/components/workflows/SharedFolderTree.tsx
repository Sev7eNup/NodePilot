import { Add, ChevronDown, ChevronRight, DataBase, Folder, FolderOpen, Renew } from '@carbon/icons-react';
import { useCallback, useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { sharedFoldersApi, ROOT_FOLDER_ID, type SharedFolder } from '../../api/sharedFolders';
import { SharedFolderContextMenu } from './SharedFolderContextMenu';
import { FolderBulkBar } from '../common/FolderBulkBar';
import { useBulkSelection } from '../../hooks/useBulkSelection';
import { useFolderBulkDelete } from '../../hooks/useFolderBulkDelete';
import { flattenVisible } from '../../lib/folderSelection';

/** Stable identity for the selection hook — declared at module level so it never changes. */
const folderKey = (folder: SharedFolder) => folder.id;

/** Module-level so the delete hook's memoized callbacks keep a stable dependency identity. */
const EMPTY_FOLDERS: SharedFolder[] = [];
const INVALIDATE_KEYS = [['shared-folders'], ['workflows']] as const;

/**
 * Sidebar that renders the org-level shared-folder tree from
 * <c>GET /api/shared-workflow-folders</c>. The selected folder id flows back to the parent
 * (WorkflowsPage), which filters the workflow list by it. Folders the caller cannot read are
 * absent from the API response, so this component renders what the server returned without
 * filtering. The "+ New" button is enabled only when <c>capabilities.canEdit</c> is true.
 */
export interface SharedFolderTreeProps {
  selectedFolderId: string | null;
  onFolderSelected: (folderId: string | null) => void;
  /** Called after a folder is created; the parent uses it to refresh workflow counts
   *  and re-fetch lists. */
  onTreeMutated?: () => void;
  /** Set this to accept a workflow dropped onto a tree node. Receives the workflow id (read
   *  from dataTransfer "application/x-nodepilot-workflow") and the destination folder id. The
   *  caller performs the API call, query invalidation and error reporting. A drop on a folder
   *  where the caller lacks canEdit is ignored. */
  onWorkflowDropped?: (workflowId: string, folderId: string) => void;
  /** Opens the folder-permissions modal for a folder. When set, the right-click menu gains a
   *  permissions entry on every folder the caller has `capabilities.canAdmin` on, including
   *  Root, which has no rename or delete entries. Omit for navigation-only embeddings such as
   *  the designer sidebar. */
  onManagePermissions?: (folderId: string) => void;
  /** When true, the "Shared Folders" header and refresh button are hidden, for narrow panels
   *  such as the designer sidebar where the parent already provides the context. */
  compact?: boolean;
  /** When true, folder management is hidden: no new-subfolder button and no right-click
   *  rename or delete menu. Suitable for navigation-only use. */
  hideManagement?: boolean;
  /**
   * Opt-in multi-select: a checkbox per row plus the bulk bar above the tree.
   *
   * Off by default because this component is also the designer's folder browser
   * (`WorkflowBrowser`), where a delete affordance does not belong. Only the workflows
   * page turns it on.
   */
  bulkDeleteEnabled?: boolean;
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
  onManagePermissions,
  compact = false,
  hideManagement = false,
  bulkDeleteEnabled = false,
}: Readonly<SharedFolderTreeProps>) {
  // Shared cache key with WorkflowsPage, so any mutation that calls
  // `queryClient.invalidateQueries({queryKey: ['shared-folders']})` (workflow create,
  // workflow move-folder, permission grant) also refreshes the workflowCount badges here.
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
  // The folder id currently under a drag. Drives the per-row drop-target highlight
  // without re-rendering the whole tree on every dragover frame.
  const [dragOverFolderId, setDragOverFolderId] = useState<string | null>(null);
  // Right-click context-menu state. The position is in viewport coordinates (clientX/clientY)
  // because the menu uses position:fixed. Only set when the row qualifies (canEdit and
  // non-Root); otherwise the browser's default menu shows.
  const [menuState, setMenuState] = useState<{ x: number; y: number; folder: SharedFolder } | null>(null);
  // Inline rename state. Like the create flow below, except the folder row itself is
  // replaced by an input instead of a new input appearing underneath it.
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

  // Selection runs over the visible rows, not the whole tree: `useBulkSelection` derives its
  // shift-range from the index in this list and prunes ids that leave it, so a collapsed branch
  // must not appear here. Root is excluded because it cannot be deleted.
  const selectableFolders = useMemo(
    () => (bulkDeleteEnabled
      ? flattenVisible(tree, collapsedIds).filter((f) => f.id !== ROOT_FOLDER_ID)
      : []),
    [bulkDeleteEnabled, tree, collapsedIds],
  );
  const selection = useBulkSelection(selectableFolders, folderKey);

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
      // Name unchanged, so close without calling the backend.
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
      // 400 (empty or over 120 characters) or 409 (sibling name collision). The response
      // body already carries a readable message, so show it unchanged in the error banner.
      setLocalError(t('workflows:folder.renameFailed', {
        defaultValue: 'Umbenennen fehlgeschlagen: {{msg}}',
        msg: (e as Error).message,
      }));
    } finally {
      setBusy(false);
    }
  };

  const deleteRecursive = useCallback(async (folder: SharedFolder) => {
    const result = await sharedFoldersApi.deleteRecursive(folder.id);
    return { deletedFolders: result.deletedFolders, deletedItems: result.deletedWorkflows };
  }, []);

  const bulkDelete = useFolderBulkDelete<SharedFolder>({
    folders: folders ?? EMPTY_FOLDERS,
    deleteRecursive,
    invalidateKeys: INVALIDATE_KEYS,
    countOf: (f) => f.workflowCount,
    pathOf: (f) => f.path,
    nameOf: (f) => f.name,
    ns: 'workflows',
    selectedFolderId,
    onFolderSelected,
    rootFolderId: ROOT_FOLDER_ID,
    onMutated: onTreeMutated,
  });

  const deleteSelected = async () => {
    // Keep only the failures selected, so a retry is one click away.
    const failedIds = await bulkDelete.deleteMany(selection.selectedItems);
    selection.retain(failedIds);
  };

  const renderNode = (node: TreeNode, depth: number) => {
    const isSelected = node.folder.id === selectedFolderId;
    const isRoot = node.folder.id === ROOT_FOLDER_ID;
    const canEdit = node.folder.capabilities.canEdit;
    // Rename and delete are meaningless on Root, but permissions are not: Root still gets a
    // menu of its own when the caller is FolderAdmin.
    const canRenameOrDelete = canEdit && !isRoot;
    const canManagePermissions = !!onManagePermissions && node.folder.capabilities.canAdmin;
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
            if (hideManagement) return;
            if (!canRenameOrDelete && !canManagePermissions) return;
            e.preventDefault();
            setMenuState({ x: e.clientX, y: e.clientY, folder: node.folder });
          }}
          data-testid={`shared-folder-${node.folder.id}`}
          onDragOver={(e) => {
            if (!dragEnabled) return;
            if (!e.dataTransfer.types.includes(WORKFLOW_DRAG_MIME)) return;
            // preventDefault tells the browser this element accepts the drop; without it
            // onDrop never fires.
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            if (dragOverFolderId !== node.folder.id) setDragOverFolderId(node.folder.id);
          }}
          onDragLeave={(e) => {
            // Only clear when leaving the row itself, not when crossing into a child element:
            // a relatedTarget inside the row still counts as inside.
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
          {/* Multi-select checkbox. Root has none because it cannot be deleted. The click is
              stopped from bubbling because the row itself filters to that folder, and ticking
              a box must not navigate. */}
          {bulkDeleteEnabled && !isRoot && (
            <span role="presentation" onClick={(e) => e.stopPropagation()}>
              <input
                type="checkbox"
                className="shrink-0 accent-primary cursor-pointer"
                checked={selection.isSelected(node.folder.id)}
                disabled={!canEdit}
                title={canEdit ? undefined : t('workflows:folder.bulk.noEditPermission')}
                aria-label={t('workflows:folder.bulk.selectRow', { name: node.folder.name })}
                onChange={(e) =>
                  selection.toggle(node.folder.id, (e.nativeEvent as MouseEvent).shiftKey)}
                data-testid={`shared-folder-select-${node.folder.id}`}
              />
            </span>
          )}
          {/* Chevron toggle, or a spacer for leaf nodes; both w-4 so labels stay aligned */}
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
      {bulkDeleteEnabled && (
        <FolderBulkBar
          selectedCount={selection.selectedCount}
          onDelete={deleteSelected}
          onClear={selection.clear}
          disabled={busy || bulkDelete.busy}
          ns="workflows"
        />
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
          onManagePermissions={
            onManagePermissions && menuState.folder.capabilities.canAdmin
              ? () => onManagePermissions(menuState.folder.id)
              : undefined
          }
          onRename={
            menuState.folder.capabilities.canEdit && menuState.folder.id !== ROOT_FOLDER_ID
              ? () => {
                  setRenamingId(menuState.folder.id);
                  setRenameValue(menuState.folder.name);
                  setLocalError(null);
                }
              : undefined
          }
          onDelete={
            menuState.folder.capabilities.canEdit && menuState.folder.id !== ROOT_FOLDER_ID
              ? () => bulkDelete.deleteOne(menuState.folder)
              : undefined
          }
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
      else roots.push(node);  // parent not visible, so render as an orphan root
    }
  }
  return roots;
}
