import { Add, ChevronDown, ChevronRight, DataBase, Folder, FolderOpen, Renew } from '@carbon/icons-react';
import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { globalFoldersApi, ROOT_FOLDER_ID, GLOBAL_VARIABLE_DRAG_MIME, type GlobalFolder } from '../../api/globalFolders';
import { SharedFolderContextMenu } from '../workflows/SharedFolderContextMenu';
import { toast } from '../../stores/toastStore';
import { confirmDialog } from '../../stores/confirmStore';

/**
 * Sidebar tree of global-variable folders (mirror of the workflow SharedFolderTree, minus RBAC).
 * The selected folder id flows back to the parent (GlobalVariablesPage) which filters the list.
 * Folder management (create / rename / delete / drop) is gated by a single `canManage` flag —
 * Admin-only, matching the rest of the globals surface — rather than per-folder capabilities.
 */
export interface GlobalFolderTreeProps {
  selectedFolderId: string | null;
  onFolderSelected: (folderId: string | null) => void;
  onTreeMutated?: () => void;
  /** Drop handler for a variable dragged onto a folder row. Only wired when `canManage`. */
  onVariableDropped?: (variableId: string, folderId: string) => void;
  /** Admin gate: shows the +subfolder button, right-click menu, and enables drop targets. */
  canManage: boolean;
}

interface TreeNode {
  folder: GlobalFolder;
  children: TreeNode[];
}

export function GlobalFolderTree({
  selectedFolderId,
  onFolderSelected,
  onTreeMutated,
  onVariableDropped,
  canManage,
}: Readonly<GlobalFolderTreeProps>) {
  const queryClient = useQueryClient();
  const { data: folders, error: queryError, isLoading } = useQuery({
    queryKey: ['global-folders'],
    queryFn: () => globalFoldersApi.list(),
  });
  const [localError, setLocalError] = useState<string | null>(null);
  const error = localError ?? (queryError ? (queryError as Error).message : null);

  const { t } = useTranslation(['globals', 'common']);
  const [busy, setBusy] = useState(false);
  const [creatingUnderId, setCreatingUnderId] = useState<string | null>(null);
  const [newFolderName, setNewFolderName] = useState('');
  const [dragOverFolderId, setDragOverFolderId] = useState<string | null>(null);
  const [menuState, setMenuState] = useState<{ x: number; y: number; folder: GlobalFolder } | null>(null);
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState('');

  const [collapsedIds, setCollapsedIds] = useState<Set<string>>(new Set());
  const toggleCollapse = (id: string) =>
    setCollapsedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });

  const reload = () => {
    setLocalError(null);
    queryClient.invalidateQueries({ queryKey: ['global-folders'] });
  };

  const tree = useMemo(() => buildTree(folders ?? []), [folders]);

  const submitCreate = async (parentId: string | null) => {
    if (!newFolderName.trim()) return;
    setBusy(true);
    setLocalError(null);
    try {
      await globalFoldersApi.create(parentId, newFolderName.trim());
      setCreatingUnderId(null);
      setNewFolderName('');
      await queryClient.invalidateQueries({ queryKey: ['global-folders'] });
      onTreeMutated?.();
    } catch (e) {
      setLocalError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const submitRename = async (folder: GlobalFolder) => {
    const trimmed = renameValue.trim();
    if (!trimmed || trimmed === folder.name) {
      setRenamingId(null);
      return;
    }
    setBusy(true);
    setLocalError(null);
    try {
      await globalFoldersApi.rename(folder.id, trimmed);
      setRenamingId(null);
      setRenameValue('');
      await queryClient.invalidateQueries({ queryKey: ['global-folders'] });
      onTreeMutated?.();
    } catch (e) {
      setLocalError(t('globals:folder.renameFailed', { msg: (e as Error).message }));
    } finally {
      setBusy(false);
    }
  };

  const confirmAndDelete = async (folder: GlobalFolder) => {
    const ok = await confirmDialog({
      message: t('globals:folder.deleteConfirm', { name: folder.name }),
      danger: true,
    });
    if (!ok) return;
    setBusy(true);
    setLocalError(null);
    try {
      await globalFoldersApi.delete(folder.id);
      await queryClient.invalidateQueries({ queryKey: ['global-folders'] });
      if (selectedFolderId === folder.id) onFolderSelected(ROOT_FOLDER_ID);
      onTreeMutated?.();
    } catch (e) {
      toast.error(t('globals:folder.deleteFailed', { msg: (e as Error).message }));
    } finally {
      setBusy(false);
    }
  };

  const renderNode = (node: TreeNode, depth: number) => {
    const isSelected = node.folder.id === selectedFolderId;
    const isRoot = node.folder.id === ROOT_FOLDER_ID;
    const dragEnabled = !!onVariableDropped && canManage;
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
                if (e.key === 'Escape') { setRenamingId(null); setRenameValue(''); }
              }}
              disabled={busy}
              data-testid="global-folder-rename-input"
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
            if (isRoot || !canManage) return;
            e.preventDefault();
            setMenuState({ x: e.clientX, y: e.clientY, folder: node.folder });
          }}
          data-testid={`global-folder-${node.folder.id}`}
          onDragOver={(e) => {
            if (!dragEnabled) return;
            if (!e.dataTransfer.types.includes(GLOBAL_VARIABLE_DRAG_MIME)) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            if (dragOverFolderId !== node.folder.id) setDragOverFolderId(node.folder.id);
          }}
          onDragLeave={(e) => {
            if (e.currentTarget.contains(e.relatedTarget as Node | null)) return;
            if (dragOverFolderId === node.folder.id) setDragOverFolderId(null);
          }}
          onDrop={(e) => {
            if (!dragEnabled) return;
            const variableId = e.dataTransfer.getData(GLOBAL_VARIABLE_DRAG_MIME);
            setDragOverFolderId(null);
            if (!variableId) return;
            e.preventDefault();
            onVariableDropped?.(variableId, node.folder.id);
          }}
        >
          {node.children.length > 0 ? (
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); toggleCollapse(node.folder.id); }}
              className="shrink-0 w-4 h-4 flex items-center justify-center rounded hover:bg-black/10 dark:hover:bg-white/10 transition-colors"
              aria-label={collapsedIds.has(node.folder.id) ? t('common:expand', { defaultValue: 'Expand' }) : t('common:collapse', { defaultValue: 'Collapse' })}
            >
              {collapsedIds.has(node.folder.id) ? <ChevronRight size={10} /> : <ChevronDown size={10} />}
            </button>
          ) : (
            <span className="w-4 shrink-0" aria-hidden />
          )}

          {isRoot
            ? <DataBase size={12} className="shrink-0 text-primary" />
            : (node.children.length > 0 && !collapsedIds.has(node.folder.id))
              ? <FolderOpen size={12} className="shrink-0 text-amber-500" />
              : <Folder size={12} className="shrink-0 text-amber-400" />}

          <span className="flex-1 truncate">
            {isRoot ? t('globals:folder.allRoot', { defaultValue: 'All variables' }) : node.folder.name}
          </span>
          <span className={`text-xs ${isSelected ? 'text-on-primary-fixed/80' : 'text-on-surface-variant'}`}>
            {node.folder.variableCount}
          </span>
          {canManage && (
            <button
              type="button"
              className={`ml-0.5 hover:text-on-surface transition-colors ${
                isSelected ? 'text-on-primary-fixed/80 hover:text-on-primary-fixed' : 'text-on-surface-variant'
              }`}
              title={t('globals:folder.createSubfolder')}
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
              placeholder={t('globals:folder.newFolderPlaceholder')}
              value={newFolderName}
              onChange={(e) => setNewFolderName(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') submitCreate(node.folder.id);
                if (e.key === 'Escape') { setCreatingUnderId(null); setNewFolderName(''); }
              }}
              disabled={busy}
              data-testid="global-folder-create-input"
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
    <div className="flex h-full flex-col" data-testid="global-folder-tree">
      <div className="flex items-center justify-between border-b border-outline-variant px-3 py-2">
        <h3 className="text-sm font-semibold text-on-surface">{t('globals:folder.heading')}</h3>
        <button
          type="button"
          className="rounded px-1 text-xs text-on-surface-variant hover:text-on-surface hover:bg-surface-container transition-colors"
          title={t('globals:folder.refresh')}
          onClick={reload}
          disabled={busy}
        >
          <Renew size={12} />
        </button>
      </div>
      <div className="flex-1 overflow-auto">
        {error && <div className="px-3 py-2 text-xs text-error">{t('globals:folder.loadError', { msg: error })}</div>}
        {isLoading && !error && <div className="px-3 py-2 text-xs text-on-surface-variant">{t('common:loadingDots')}</div>}
        {folders && <ul>{tree.map((n) => renderNode(n, 0))}</ul>}
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
          onDelete={() => confirmAndDelete(menuState.folder)}
          onClose={() => setMenuState(null)}
        />
      )}
    </div>
  );
}

/** Build a parent-child tree from a flat folder list. Stable sort: depth, then name. */
function buildTree(folders: GlobalFolder[]): TreeNode[] {
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
      else roots.push(node);
    }
  }
  return roots;
}
