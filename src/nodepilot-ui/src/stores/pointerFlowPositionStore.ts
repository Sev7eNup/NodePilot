import { create } from 'zustand';

/**
 * Live cursor position in React-Flow (canvas) coordinates, or null when the pointer is off the
 * canvas. Written by the editor's (rAF-throttled) pointer handler and read by nodes to reveal
 * their ports on proximity. Deliberately NOT persisted — it's transient interaction state.
 */
interface PointerFlowPositionState {
  x: number | null;
  y: number | null;
  set: (x: number | null, y: number | null) => void;
}

export const usePointerFlowPosition = create<PointerFlowPositionState>((set) => ({
  x: null,
  y: null,
  set: (x, y) => set({ x, y }),
}));
