import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useFolderBulkDelete } from '../../hooks/useFolderBulkDelete';

/**
 * Covers the bulk-delete orchestration both folder trees share: reducing a selection to its
 * cover set, returning exactly the failed ids so a retry can keep them selected, and moving
 * the filter only when the folder it points at was deleted.
 */
vi.mock('../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});
import { confirmDialog } from '../../stores/confirmStore';
import { useToastStore } from '../../stores/toastStore';

interface TestFolder {
  id: string;
  parentFolderId: string | null;
  path: string;
  name: string;
  count: number;
}

const ROOT = 'root';
const folders: TestFolder[] = [
  { id: ROOT, parentFolderId: null, path: '/', name: 'Root', count: 0 },
  { id: 'a', parentFolderId: ROOT, path: '/a', name: 'a', count: 2 },
  { id: 'a1', parentFolderId: 'a', path: '/a/a1', name: 'a1', count: 3 },
  { id: 'b', parentFolderId: ROOT, path: '/b', name: 'b', count: 1 },
];
const byId = (id: string) => folders.find((f) => f.id === id)!;

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function setup(options: {
  deleteRecursive: (folder: TestFolder) => Promise<{ deletedFolders: number; deletedItems: number }>;
  selectedFolderId?: string | null;
  onFolderSelected?: (id: string) => void;
}) {
  const onFolderSelected = options.onFolderSelected ?? vi.fn();
  const hook = renderHook(
    () => useFolderBulkDelete<TestFolder>({
      folders,
      deleteRecursive: options.deleteRecursive,
      invalidateKeys: [['test-folders']],
      countOf: (f) => f.count,
      pathOf: (f) => f.path,
      nameOf: (f) => f.name,
      ns: 'workflows',
      selectedFolderId: options.selectedFolderId ?? null,
      onFolderSelected,
      rootFolderId: ROOT,
    }),
    { wrapper },
  );
  return { hook, onFolderSelected };
}

describe('useFolderBulkDelete', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(confirmDialog).mockResolvedValue(true);
    useToastStore.setState({ toasts: [] });
  });

  it('reduces the selection to its cover set and confirms exactly once', async () => {
    const deleteRecursive = vi.fn().mockResolvedValue({ deletedFolders: 2, deletedItems: 5 });
    const { hook } = setup({ deleteRecursive });

    await act(async () => { await hook.result.current.deleteMany([byId('a'), byId('a1')]); });

    expect(deleteRecursive).toHaveBeenCalledTimes(1);
    expect(deleteRecursive.mock.calls[0][0].id).toBe('a');
    expect(confirmDialog).toHaveBeenCalledTimes(1);
  });

  it('a declined confirmation deletes nothing', async () => {
    const deleteRecursive = vi.fn();
    vi.mocked(confirmDialog).mockResolvedValueOnce(false);
    const { hook } = setup({ deleteRecursive });

    let failed: string[] = ['unset'];
    await act(async () => { failed = await hook.result.current.deleteMany([byId('a')]); });

    expect(deleteRecursive).not.toHaveBeenCalled();
    expect(failed).toEqual([]);
  });

  it('returns only the failed ids so a retry keeps them selected', async () => {
    const deleteRecursive = vi.fn(async (f: TestFolder) => {
      if (f.id === 'b') throw new Error('refused');
      return { deletedFolders: 1, deletedItems: 2 };
    });
    const { hook } = setup({ deleteRecursive });

    let failed: string[] = [];
    await act(async () => { failed = await hook.result.current.deleteMany([byId('a'), byId('b')]); });

    // A failing folder does not abort the run; the other one is still deleted.
    expect(deleteRecursive).toHaveBeenCalledTimes(2);
    expect(failed).toEqual(['b']);
  });

  it('moves the filter only when the folder it points at actually went', async () => {
    // a1 is never requested; it is removed together with its parent.
    const deleteRecursive = vi.fn().mockResolvedValue({ deletedFolders: 2, deletedItems: 5 });
    const { hook, onFolderSelected } = setup({ deleteRecursive, selectedFolderId: 'a1' });

    await act(async () => { await hook.result.current.deleteMany([byId('a')]); });

    await waitFor(() => expect(onFolderSelected).toHaveBeenCalledWith(ROOT));
  });

  it('leaves the filter alone when the delete failed', async () => {
    const deleteRecursive = vi.fn().mockRejectedValue(new Error('nope'));
    const { hook, onFolderSelected } = setup({ deleteRecursive, selectedFolderId: 'a1' });

    await act(async () => { await hook.result.current.deleteMany([byId('a')]); });

    expect(onFolderSelected).not.toHaveBeenCalled();
    expect(useToastStore.getState().toasts.length).toBeGreaterThan(0);
  });

  it('leaves the filter alone when an unrelated folder went', async () => {
    const deleteRecursive = vi.fn().mockResolvedValue({ deletedFolders: 1, deletedItems: 1 });
    const { hook, onFolderSelected } = setup({ deleteRecursive, selectedFolderId: 'a1' });

    await act(async () => { await hook.result.current.deleteMany([byId('b')]); });

    expect(onFolderSelected).not.toHaveBeenCalled();
  });

  it('says so instead of doing nothing when the selection has evaporated', async () => {
    // Reachable when the selection empties between the render that showed the button and the
    // click. Reporting it keeps a dead Delete button from looking like a failed delete.
    const deleteRecursive = vi.fn();
    const { hook } = setup({ deleteRecursive });

    let failed: string[] = ['unset'];
    await act(async () => { failed = await hook.result.current.deleteMany([]); });

    expect(failed).toEqual([]);
    expect(deleteRecursive).not.toHaveBeenCalled();
    expect(confirmDialog).not.toHaveBeenCalled();
    expect(useToastStore.getState().toasts.length).toBeGreaterThan(0);
  });

  it('deleteOne surfaces the server message instead of throwing', async () => {
    const deleteRecursive = vi.fn().mockRejectedValue(new Error('checked out by someone else'));
    const { hook } = setup({ deleteRecursive });

    await act(async () => { await hook.result.current.deleteOne(byId('a')); });

    const messages = useToastStore.getState().toasts.map((toast) => toast.message).join(' ');
    expect(messages).toContain('checked out by someone else');
  });
});
