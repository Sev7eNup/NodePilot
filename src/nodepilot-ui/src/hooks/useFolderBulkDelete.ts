import { useCallback, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { toast } from '../stores/toastStore';
import { confirmDialog } from '../stores/confirmStore';
import { runBulkOperation } from '../lib/bulkOperations';
import { captureAuthBoundaryGeneration } from '../security/authBoundary';
import {
  type FolderLike,
  isInDeletedSubtree,
  subtreeImpact,
  topMostFolders,
} from '../lib/folderSelection';

/** Server counts of a recursive folder delete. Both trees report the same two numbers. */
export interface RecursiveDeleteCounts {
  deletedFolders: number;
  deletedItems: number;
}

export interface FolderBulkDeleteOptions<T extends FolderLike> {
  /** The tree as currently known — ancestry and the impact estimate are resolved against it. */
  folders: readonly T[];
  /** Recursive delete of one folder. Returns what the server actually removed. */
  deleteRecursive: (folder: T) => Promise<RecursiveDeleteCounts>;
  /** Query keys to invalidate after a run: the folder tree plus the list it filters. */
  invalidateKeys: readonly (readonly unknown[])[];
  /** Direct item count of one folder — `workflowCount` here, `variableCount` there. */
  countOf: (folder: T) => number;
  /** Display path used in the confirmation list and the failure report. */
  pathOf: (folder: T) => string;
  /** Display name used when a single folder is deleted. */
  nameOf: (folder: T) => string;
  /** i18n namespace prefix for the `folder.bulk.*` keys, e.g. `workflows` or `globals`. */
  ns: string;
  /** The folder the list is currently filtered by, and how to reset it. */
  selectedFolderId: string | null;
  onFolderSelected: (folderId: string) => void;
  /** Where the filter goes when the selected folder disappeared. */
  rootFolderId: string;
  /** Called after a successful run so the page can refresh derived counts. */
  onMutated?: () => void;
}

export interface FolderBulkDelete<T extends FolderLike> {
  /** True while a run is in flight — the caller disables its controls on it. */
  busy: boolean;
  /** Confirm + delete one folder (context menu). */
  deleteOne: (folder: T) => Promise<void>;
  /** Confirm + delete a selection, one request per top-most folder. Returns the ids that failed,
   *  so the caller can keep exactly those selected. */
  deleteMany: (selected: readonly T[]) => Promise<string[]>;
}

/**
 * The delete half of the folder multi-select, shared by the workflow tree and the global-variable
 * tree. Both do the same thing — reduce the selection to a cover set, confirm with the impact,
 * delete one subtree per request, report the server's numbers, and reset the filter when the
 * folder it pointed at is gone. Only the API call, the query keys, and the labels differ.
 *
 * Sequential single-folder requests rather than one batch call: every folder keeps its own
 * authorization check and its own audit rows, and a folder that refuses does not take the rest
 * of the run down with it.
 */
export function useFolderBulkDelete<T extends FolderLike>(
  options: FolderBulkDeleteOptions<T>,
): FolderBulkDelete<T> {
  const {
    folders, deleteRecursive, invalidateKeys, countOf, pathOf, nameOf, ns,
    selectedFolderId, onFolderSelected, rootFolderId, onMutated,
  } = options;

  const queryClient = useQueryClient();
  const { t } = useTranslation([ns, 'common']);
  const [busy, setBusy] = useState(false);

  const invalidateAll = useCallback(async () => {
    for (const key of invalidateKeys) {
      await queryClient.invalidateQueries({ queryKey: key });
    }
  }, [queryClient, invalidateKeys]);

  /**
   * Confirmation shared by the context menu and the bulk bar: names every folder that goes and
   * how much rides along. The numbers are an estimate — folders the caller cannot read are not
   * in `folders` — so the toast afterwards reports what the server actually removed.
   */
  const confirmDeletion = useCallback(async (roots: readonly T[]) => {
    const impact = subtreeImpact(roots, folders, countOf);
    const extraFolders = impact.folders - roots.length;
    return confirmDialog({
      message: roots.length === 1
        ? t(`${ns}:folder.deleteConfirm`, { name: nameOf(roots[0]) })
        : t(`${ns}:folder.bulk.deleteConfirm`, { count: roots.length }),
      details: roots.map((f) =>
        t(`${ns}:folder.bulk.impactRow`, { path: pathOf(f), count: countOf(f) })),
      // Only worth a sentence when something beyond the listed folders is affected.
      ...(impact.items > 0 || extraFolders > 0
        ? { confirmLabel: t(`${ns}:folder.bulk.deleteConfirmButton`, { items: impact.items }) }
        : {}),
      danger: true,
    });
  }, [folders, countOf, pathOf, nameOf, ns, t]);

  const deleteOne = useCallback(async (folder: T) => {
    if (!(await confirmDeletion([folder]))) return;
    setBusy(true);
    const foldersBefore = [...folders];
    try {
      // Always recursive: "delete only when empty" was the limitation this replaced. The
      // confirmation above already stated what goes with it.
      const counts = await deleteRecursive(folder);
      await invalidateAll();
      toast.success(t(`${ns}:folder.bulk.deleted`, {
        folders: counts.deletedFolders,
        items: counts.deletedItems,
      }));
      // The filtered folder may be the one deleted OR a descendant that went with it.
      if (isInDeletedSubtree(selectedFolderId, [folder.id], foldersBefore)) {
        onFolderSelected(rootFolderId);
      }
      onMutated?.();
    } catch (e) {
      // 423 (someone else holds an edit lock in the subtree), 409 (contents changed mid-delete)
      // or 400/403 — the backend message is already user-friendly, so pass it through as-is.
      toast.error(t(`${ns}:folder.deleteFailed`, { msg: (e as Error).message }));
    } finally {
      setBusy(false);
    }
  }, [confirmDeletion, folders, deleteRecursive, invalidateAll, t, ns,
      selectedFolderId, onFolderSelected, rootFolderId, onMutated]);

  const deleteMany = useCallback(async (selected: readonly T[]): Promise<string[]> => {
    // A selected folder that sits under another selected folder would be deleted by the parent's
    // request and 404 on its own.
    const roots = topMostFolders(selected, folders);
    if (roots.length === 0) {
      // Reachable only if the selection evaporated between the render that showed the button and
      // the click. Returning quietly is what made a dead Delete button look like a failed delete.
      toast.info(t(`${ns}:folder.bulk.nothingSelected`));
      return [];
    }
    if (!(await confirmDeletion(roots))) return [];

    setBusy(true);
    const generation = captureAuthBoundaryGeneration();
    const foldersBefore = [...folders];
    try {
      const result = await runBulkOperation(
        roots,
        async (folder) => { await deleteRecursive(folder); },
        { getLabel: pathOf, authBoundaryGeneration: generation },
      );

      await invalidateAll();

      if (result.succeeded.length > 0) {
        toast.success(t(`${ns}:folder.bulk.deletedCount`, { count: result.succeeded.length }));
      }
      if (result.failed.length > 0) {
        toast.error(
          `${t(`${ns}:folder.bulk.failedHeader`, { count: result.failed.length })}\n` +
            result.failed.map((f) => `${pathOf(f.item)}: ${f.message}`).join('\n'),
          30_000,  // failures need reading time; the success toast keeps the default
        );
      }

      // Only the folders that actually went may move the filter — and a filtered DESCENDANT
      // disappears without ever being requested, so the check runs against the pre-delete tree.
      const deletedIds = result.succeeded.map((f) => f.id);
      if (isInDeletedSubtree(selectedFolderId, deletedIds, foldersBefore)) {
        onFolderSelected(rootFolderId);
      }
      onMutated?.();
      return result.failed.map((f) => f.item.id);
    } finally {
      setBusy(false);
    }
  }, [confirmDeletion, folders, deleteRecursive, invalidateAll, pathOf, t, ns,
      selectedFolderId, onFolderSelected, rootFolderId, onMutated]);

  return { busy, deleteOne, deleteMany };
}
