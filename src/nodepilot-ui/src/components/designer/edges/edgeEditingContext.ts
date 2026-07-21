import { createContext } from 'react';
import type { ControlPoints } from './smartEdgePath';

/**
 * Edges need a channel back up to the parent to open the insert-step picker and trigger
 * reshape operations. This context avoids having to thread those callbacks as props through
 * React Flow's internals and its EdgeTypes map.
 *
 * Undo history and the "unsaved changes" flag live exclusively in the parent's actions
 * (begin/update/reset) — the edge component itself never touches setEdges/commitHistory
 * directly, so an accidental change to the drag logic here can't break undo consistency.
 * This mirrors the pattern used by the node-delete handler in WorkflowEditorPage.tsx:
 * commit history and mark the workflow dirty BEFORE the actual setEdges call.
 *
 * Lives in its own module (rather than inside LabeledEdge.tsx) so EdgeReshapeHandles and
 * LabeledEdge don't end up importing each other in a circle. The old export name
 * `EdgeInsertContext` is still re-exported from LabeledEdge so existing tests don't need
 * to change.
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
