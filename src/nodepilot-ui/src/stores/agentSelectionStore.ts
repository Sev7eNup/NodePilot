import { create } from 'zustand';

export const useAgentSelectionStore = create<{
  nodeId: string | null;
  memberId: string | null;
  select: (nodeId: string, memberId: string) => void;
}>((set) => ({ nodeId: null, memberId: null, select: (nodeId, memberId) => set({ nodeId, memberId }) }));
