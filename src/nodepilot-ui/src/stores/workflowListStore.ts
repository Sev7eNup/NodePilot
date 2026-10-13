import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export const WORKFLOW_COL_KEYS = [
  'name', 'activities', 'triggers', 'status', 'lastRun', 'successRate', 'runtime', 'created', 'updated',
] as const;
export type WorkflowColKey = (typeof WORKFLOW_COL_KEYS)[number];
export type SortDir = 'asc' | 'desc';

interface State {
  /** Sorted column of the workflows table; null keeps the server default (last updated first). */
  sortBy: WorkflowColKey | null;
  sortDir: SortDir;
  setSort: (sortBy: WorkflowColKey, sortDir: SortDir) => void;
}

const isColKey = (v: unknown): v is WorkflowColKey =>
  typeof v === 'string' && (WORKFLOW_COL_KEYS as readonly string[]).includes(v);

/** Table sort of the workflows page, kept so it survives opening a workflow and coming back. */
export const useWorkflowListStore = create<State>()(
  persist(
    (set) => ({
      sortBy: null,
      sortDir: 'asc',
      setSort: (sortBy, sortDir) => set({ sortBy, sortDir }),
    }),
    {
      name: 'nodepilot.workflows.list',
      version: 1,
      partialize: (s) => ({ sortBy: s.sortBy, sortDir: s.sortDir }),
      // Stored values are not trusted: an unknown column would leave the header chevron out of
      // step with the order the server returns.
      merge: (persisted, current) => {
        const stored = persisted as { sortBy?: unknown; sortDir?: unknown } | null | undefined;
        return {
          ...current,
          sortBy: isColKey(stored?.sortBy) ? stored.sortBy : null,
          sortDir: stored?.sortDir === 'desc' ? 'desc' : 'asc',
        };
      },
    },
  ),
);
