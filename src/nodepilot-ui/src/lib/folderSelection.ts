/**
 * Pure helpers behind the folder multi-select, kept out of the tree components so they can be
 * tested without rendering a tree.
 *
 * Generic over the folder type: the workflow tree and the global-variable tree have the same
 * shape but different payloads (`workflowCount` vs `variableCount`), so the helpers require only
 * what they read and take a count accessor where they need one.
 */

/** The minimum a folder has to carry for these helpers: an identity and its parent. */
export interface FolderLike {
  id: string;
  parentFolderId: string | null;
}

export interface FolderTreeNode<T extends FolderLike> {
  folder: T;
  children: FolderTreeNode<T>[];
}

/**
 * Returns the folders that are visible on screen, in render order.
 *
 * `useBulkSelection` derives its shift-range from the index inside this list and prunes ids that
 * are missing from it, so the list has to match what the user sees. Children of a collapsed node
 * are left out, which also means collapsing a branch drops its descendants from the selection.
 */
export function flattenVisible<T extends FolderLike>(
  nodes: readonly FolderTreeNode<T>[],
  collapsedIds: ReadonlySet<string>,
): T[] {
  const out: T[] = [];
  const walk = (list: readonly FolderTreeNode<T>[]) => {
    for (const node of list) {
      out.push(node.folder);
      if (node.children.length > 0 && !collapsedIds.has(node.folder.id)) walk(node.children);
    }
  };
  walk(nodes);
  return out;
}

/**
 * Reduces a selection to the folders that are no other selected folder's descendant.
 *
 * Deleting this cover set removes the same folders while avoiding a second request for a child
 * that the parent's delete already took, which would 404.
 *
 * Ancestry is walked over `parentFolderId` rather than `path`, because `path` is a display string
 * that a rename rewrites asynchronously. A folder whose parent is not in `all` (no read
 * permission) is treated as top-most.
 */
export function topMostFolders<T extends FolderLike>(
  selected: readonly T[],
  all: readonly FolderLike[],
): T[] {
  const selectedIds = new Set(selected.map((f) => f.id));
  const parentById = new Map(all.map((f) => [f.id, f.parentFolderId] as const));

  const hasSelectedAncestor = (folder: T) => {
    const seen = new Set<string>([folder.id]);
    let parentId = folder.parentFolderId;
    while (parentId != null && !seen.has(parentId)) {
      if (selectedIds.has(parentId)) return true;
      seen.add(parentId);
      parentId = parentById.get(parentId) ?? null;
    }
    return false;
  };

  return selected.filter((f) => !hasSelectedAncestor(f));
}

/**
 * Reports whether `folderId` sits inside one of the deleted subtrees.
 *
 * A descendant disappears with its parent without ever being named in a request, so ancestry is
 * resolved against the folder list as it stood before the delete, the only place that mapping
 * still exists.
 *
 * `deletedRootIds` must carry only the roots that actually succeeded: otherwise a failed request
 * would reset the filter to "all folders" while the folder is still there.
 */
export function isInDeletedSubtree(
  folderId: string | null,
  deletedRootIds: readonly string[],
  all: readonly FolderLike[],
): boolean {
  if (folderId == null || deletedRootIds.length === 0) return false;
  const deleted = new Set(deletedRootIds);
  const parentById = new Map(all.map((f) => [f.id, f.parentFolderId] as const));

  const seen = new Set<string>();
  let current: string | null = folderId;
  while (current != null && !seen.has(current)) {
    if (deleted.has(current)) return true;
    seen.add(current);
    current = parentById.get(current) ?? null;
  }
  return false;
}

export interface SubtreeImpact {
  /** Folders removed, including the selected ones themselves. */
  folders: number;
  /** Items (workflows / variables) in those folders, as far as the caller can see them. */
  items: number;
}

/**
 * Estimates what deleting `roots` would remove. Folders the caller cannot read are missing from
 * `all`, so the server may delete more; the confirmation says so and the toast afterwards reports
 * the server's own numbers.
 *
 * `roots` must already be a cover set, otherwise overlapping subtrees are counted twice.
 */
export function subtreeImpact<T extends FolderLike>(
  roots: readonly T[],
  all: readonly T[],
  countOf: (folder: T) => number,
): SubtreeImpact {
  const childrenByParent = new Map<string, T[]>();
  for (const f of all) {
    if (f.parentFolderId == null) continue;
    const list = childrenByParent.get(f.parentFolderId);
    if (list) list.push(f);
    else childrenByParent.set(f.parentFolderId, [f]);
  }

  const visited = new Set<string>();
  let folders = 0;
  let items = 0;
  const stack = [...roots];
  while (stack.length > 0) {
    const current = stack.pop()!;
    if (visited.has(current.id)) continue;
    visited.add(current.id);
    folders += 1;
    items += countOf(current);
    const children = childrenByParent.get(current.id);
    if (children) stack.push(...children);
  }
  return { folders, items };
}
