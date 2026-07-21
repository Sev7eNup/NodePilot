import { createContext } from 'react';

/**
 * Lets each <ToolbarSection> register its DOM node with the <ToolbarGlow> provider without
 * prop-drilling through the toolbar's JSX. The provider keeps the live set of section
 * elements and, on pointer move, writes each one's proximity intensity straight to its
 * `--np-glow` CSS variable — no React state, no re-render.
 *
 * `register` returns its own unregister fn, called from the section's effect cleanup
 * (handles conditional clusters mounting/unmounting and StrictMode double-invoke cleanly).
 *
 * Default is a no-op so a <ToolbarSection> rendered outside a provider stays inert — mirrors
 * the EdgeEditingContext defaults pattern in ../edges/edgeEditingContext.ts.
 */
export const ToolbarGlowContext = createContext<{
  register: (el: HTMLElement) => () => void;
}>({
  register: () => () => {},
});
