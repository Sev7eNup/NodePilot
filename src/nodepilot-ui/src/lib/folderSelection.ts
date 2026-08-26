/**
 * Pure helpers behind the folder multi-select. They live here rather than in a tree component so
 * they can be tested without rendering a tree — the interesting cases (a collapsed branch, a
 * selection that contains both a parent and its child) are all shape, not markup.
 *
 * Generic over the folder type: the workflow tree and the global-variable tree have the same
 * shape but different payloads (`workflowCount` vs `variableCount`), so the helpers only require
 * what they actually read and take a count accessor where they need one.
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
 * The folders that are actually on screen, in the order they are rendered.
 *
 * `useBulkSelection` derives its shift-range from the index inside `items`, and its prune effect
 * drops ids that are not in it — so `items` has to be exactly what the user sees. A plain preorder
 * walk would include the children of a collapsed node, and a shift-range could then quietly select
 * folders nobody can see.
 *
 * The flip side is deliberate: collapsing a branch takes its descendants out of the selection.
 * That costs nothing, because deleting the parent takes the whole subtree anyway — and it keeps
 * the rule simple: you can only select what is in front of you.
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
 * Reduces a selection to the folders that are nobody else's descendant.
 *
 * Without this, selecting a parent *and* one of its children sends two requests: the first deletes
 * the child along with the subtree, the second finds nothing and 404s. Deleting the cover set alone
 * removes exactly the same folders.
 *
 * Ancestry is walked over `parentFolderId` rather than `path`, because `path` is a display string
 * that a rename rewrites asynchronously — and a folder whose parent is not in `all` (no read
 * permission) is treated as top-most, which is the safe reading.
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
 * Is `folderId` inside one of the deleted subtrees?
 *
 * The folder the list is filtered by does not have to be one that was asked for — a descendant
 * disappears with its parent without ever being named in a request. Ancestry is resolved against
 * the folder list as it was *before* the delete, which is the only place that mapping still exists.
 *
 * `deletedRootIds` must carry only the roots that actually succeeded: resetting the filter after a
 * failed request would send the user back to "all folders" while their folder is still there.
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
  /** Folders that go, including the selected ones themselves. */
  folders: number;
  /** Items (workflows / variables) in those folders, as far as the caller can see them. */
  items: number;
}

/**
 * What deleting `roots` would remove. An estimate by nature: folders the caller cannot read are
 * missing from `all`, so the server may delete more. The confirmation says so, and the toast
 * afterwards reports the server's own numbers.
 *
 * Expects `roots` to already be a cover set — overlapping subtrees would otherwise be counted twice.
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
