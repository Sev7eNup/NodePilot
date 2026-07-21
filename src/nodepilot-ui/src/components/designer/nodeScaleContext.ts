import { createContext } from 'react';

/**
 * Optional per-subtree override for the active node-scale index (`designStore.nodeScaleIndex`).
 *
 * `null` → use the persisted store value (the desktop default). MobileWorkflowView provides a
 * larger index so the read-only phone graph renders bigger icons / labels (and edge labels)
 * without mutating the global, persisted design preference. ActivityNode and LabeledEdge read
 * `useContext(NodeScaleOverrideContext) ?? storeIndex`, so any canvas without a provider keeps
 * the user's chosen scale.
 */
export const NodeScaleOverrideContext = createContext<number | null>(null);
