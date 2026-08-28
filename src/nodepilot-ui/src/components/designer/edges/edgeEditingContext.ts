import { createContext } from 'react';
import type { ControlPoints } from './smartEdgePath';

/**
 * Gives edge components a channel back up to the parent to open the insert-step picker and
 * trigger reshape operations, without threading callbacks as props through React Flow's
 * EdgeTypes map. Undo history and the "unsaved changes" flag stay in the parent's actions;
 * edge components never call setEdges/commitHistory directly, so a change to the drag logic
 * here cannot break undo consistency.
 */
export const EdgeEditingContext = createContext<{
  onInsertRequest: (edgeId: string, x: number, y: number) => void;
  canWrite: boolean;
  beginEdgeReshape: (edgeId: string) => void;
  updateEdgeShape: (edgeId: string, controlPoints: ControlPoints) => void;
  resetEdgeShape: (edgeId: string) => void;
}>({
  onInsertRequest: () => {},
  canWrite: false,
  beginEdgeReshape: () => {},
  updateEdgeShape: () => {},
  resetEdgeShape: () => {},
});
