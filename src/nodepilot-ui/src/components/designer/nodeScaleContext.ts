import { createContext } from 'react';

/**
 * Optional per-subtree override for the active node-scale index (`designStore.nodeScaleIndex`).
 *
 * `null` means use the persisted store value. MobileWorkflowView sets a larger index so the
 * read-only phone graph renders bigger icons and labels without changing the global preference.
 * ActivityNode and LabeledEdge read `useContext(NodeScaleOverrideContext) ?? storeIndex`, so a
 * canvas without a provider keeps the user's chosen scale.
 */
export const NodeScaleOverrideContext = createContext<number | null>(null);
