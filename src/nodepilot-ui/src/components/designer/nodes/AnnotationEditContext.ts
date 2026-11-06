import { createContext } from 'react';
import type { Node } from '@xyflow/react';

/** Annotation editors share the canvas permission; previews default to read-only. */
export const AnnotationEditContext = createContext<{
  canWrite: boolean;
  updateGroupNode: (id: string, updater: (node: Node) => Node) => void;
  toggleGroupView: (id: string) => void;
} | null>(null);
