import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type WorkflowViewMode = 'trigger' | 'folder';

interface State {
  viewMode: WorkflowViewMode;
  collapsedFolders: Record<string, boolean>;
  /** Pixel height of the info-details card beneath the workflow list (drag-resizable). */
  infoCardHeight: number;
  setViewMode: (m: WorkflowViewMode) => void;
  toggleFolder: (folderId: string) => void;
  setInfoCardHeight: (h: number) => void;
}

// A touch taller than the original 200px so the info card's last row isn't clipped;
// still drag-resizable from there.
const DEFAULT_INFO_CARD_HEIGHT = 320;

export const useWorkflowBrowserStore = create<State>()(
  persist(
    (set) => ({
      viewMode: 'folder',
      collapsedFolders: {},
      infoCardHeight: DEFAULT_INFO_CARD_HEIGHT,
      setViewMode: (m) => set({ viewMode: m }),
      toggleFolder: (folderId) =>
        set((s) => ({
          collapsedFolders: {
            ...s.collapsedFolders,
            [folderId]: !s.collapsedFolders[folderId],
          },
        })),
      setInfoCardHeight: (h) => set({ infoCardHeight: h }),
    }),
    {
      name: 'nodepilot-workflow-browser',
      version: 7,
      // The info-card default has been tuned across versions; realign older persisted heights
      // to the current default so everyone gets the intended out-of-box size. Later user
      // drags still persist (until the next default change).
      migrate: (persisted, version) => {
        const state = (persisted ?? {}) as Partial<State>;
        if (version < 7) return { ...state, infoCardHeight: DEFAULT_INFO_CARD_HEIGHT } as State;
        return state as State;
      },
    },
  ),
);
