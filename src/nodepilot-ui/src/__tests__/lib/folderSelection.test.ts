import { describe, it, expect } from 'vitest';
import type { SharedFolder } from '../../api/sharedFolders';
import {
  flattenVisible,
  topMostFolders,
  subtreeImpact,
  isInDeletedSubtree,
  type FolderTreeNode,
} from '../../lib/folderSelection';

/**
 * Pure logic behind the folder multi-select. Three things can go wrong in ways a
 * rendered tree will not show: `flattenVisible` must match the visible order, or a
 * shift-range picks up collapsed folders and breaks `useBulkSelection`'s prune effect.
 * `topMostFolders` avoids a second request 404ing when the first already removed the
 * folder recursively. `subtreeImpact` must not count overlapping subtrees twice.
 */

function folder(id: string, parentFolderId: string | null, workflowCount = 0): SharedFolder {
  return {
    id,
    parentFolderId,
    name: id,
    path: `/${id}`,
    depth: 0,
    createdAt: '2026-01-01T00:00:00Z',
    createdByUserId: null,
    workflowCount,
    capabilities: { canRead: true, canRun: true, canEdit: true, canAdmin: false },
  };
}

function node(
  f: SharedFolder,
  children: FolderTreeNode<SharedFolder>[] = [],
): FolderTreeNode<SharedFolder> {
  return { folder: f, children };
}

/** The count accessor the workflow tree passes; globals pass `variableCount` instead. */
const workflowCountOf = (f: SharedFolder) => f.workflowCount;

// root
//  ├── a
//  │    ├── a1
//  │    └── a2
//  └── b
const root = folder('root', null);
const a = folder('a', 'root', 2);
const a1 = folder('a1', 'a', 3);
const a2 = folder('a2', 'a', 0);
const b = folder('b', 'root', 5);
const ALL = [root, a, a1, a2, b];
const TREE = [node(root, [node(a, [node(a1), node(a2)]), node(b)])];

describe('flattenVisible', () => {
  it('nothingCollapsed_returnsFullPreorder', () => {
    expect(flattenVisible(TREE, new Set()).map((f) => f.id)).toEqual(['root', 'a', 'a1', 'a2', 'b']);
  });

  it('collapsedBranch_omitsItsChildren', () => {
    // The case that matters: 'a' is collapsed, so a1/a2 must not be in the list — a shift-range
    // from 'root' to 'b' would otherwise pull invisible folders into the selection.
    expect(flattenVisible(TREE, new Set(['a'])).map((f) => f.id)).toEqual(['root', 'a', 'b']);
  });

  it('collapsedLeaf_isHarmless', () => {
    expect(flattenVisible(TREE, new Set(['a1'])).map((f) => f.id)).toEqual(['root', 'a', 'a1', 'a2', 'b']);
  });

  it('emptyTree_returnsEmpty', () => {
    expect(flattenVisible([], new Set())).toEqual([]);
  });
});

describe('topMostFolders', () => {
  it('siblings_areAllKept', () => {
    expect(topMostFolders([a, b], ALL).map((f) => f.id)).toEqual(['a', 'b']);
  });

  it('parentAndChild_dropsTheChild', () => {
    expect(topMostFolders([a, a1], ALL).map((f) => f.id)).toEqual(['a']);
  });

  it('grandparentAndGrandchild_dropsTheGrandchild', () => {
    expect(topMostFolders([root, a1], ALL).map((f) => f.id)).toEqual(['root']);
  });

  it('wholeBranchSelected_collapsesToTheTop', () => {
    expect(topMostFolders([a, a1, a2], ALL).map((f) => f.id)).toEqual(['a']);
  });

  it('folderWhoseParentIsNotVisible_countsAsTopMost', () => {
    // No read permission on the parent -> it is missing from `all`. Treating the folder as a
    // descendant would silently drop it from the selection.
    const orphan = folder('orphan', 'hidden-parent');
    expect(topMostFolders([orphan], [orphan]).map((f) => f.id)).toEqual(['orphan']);
  });

  it('emptySelection_returnsEmpty', () => {
    expect(topMostFolders([], ALL)).toEqual([]);
  });
});

describe('isInDeletedSubtree', () => {
  it('theDeletedFolderItself_isInside', () => {
    expect(isInDeletedSubtree('a', ['a'], ALL)).toBe(true);
  });

  it('descendantOfADeletedFolder_isInside', () => {
    // The case that would otherwise leave the filter pointing at a dead folder: a1 was never
    // requested but disappears with 'a'.
    expect(isInDeletedSubtree('a1', ['a'], ALL)).toBe(true);
  });

  it('unrelatedFolder_isOutside', () => {
    expect(isInDeletedSubtree('b', ['a'], ALL)).toBe(false);
  });

  it('emptyDeletedList_isAlwaysOutside', () => {
    // A failed request leaves this empty — and the filter has to stay where it is.
    expect(isInDeletedSubtree('a1', [], ALL)).toBe(false);
  });

  it('noFilter_isOutside', () => {
    expect(isInDeletedSubtree(null, ['a'], ALL)).toBe(false);
  });
});

describe('subtreeImpact', () => {
  it('leaf_countsItself', () => {
    expect(subtreeImpact([a1], ALL, workflowCountOf)).toEqual({ folders: 1, items: 3 });
  });

  it('branch_countsDescendants', () => {
    // a(2) + a1(3) + a2(0) = 3 folders, 5 workflows
    expect(subtreeImpact([a], ALL, workflowCountOf)).toEqual({ folders: 3, items: 5 });
  });

  it('siblings_sumUp', () => {
    expect(subtreeImpact([a, b], ALL, workflowCountOf)).toEqual({ folders: 4, items: 10 });
  });

  it('overlappingRoots_areNotCountedTwice', () => {
    // Guards the case where someone forgets to reduce the selection to its cover set.
    expect(subtreeImpact([a, a1], ALL, workflowCountOf)).toEqual({ folders: 3, items: 5 });
  });

  it('otherCountField_worksThroughTheAccessor', () => {
    // The globals tree carries `variableCount`. Only the accessor differs — proof that the
    // helper is not tied to the workflow shape.
    type VarFolder = { id: string; parentFolderId: string | null; variableCount: number };
    const vRoot: VarFolder = { id: 'root', parentFolderId: null, variableCount: 1 };
    const vChild: VarFolder = { id: 'child', parentFolderId: 'root', variableCount: 4 };
    expect(subtreeImpact([vRoot], [vRoot, vChild], (f) => f.variableCount))
      .toEqual({ folders: 2, items: 5 });
  });
});
