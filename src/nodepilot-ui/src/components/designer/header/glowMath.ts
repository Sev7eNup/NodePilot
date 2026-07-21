/**
 * Pure geometry for the toolbar proximity glow. Kept React-free and DOM-free so it can
 * be unit-tested with hand-built rects — the test environment monkey-patches
 * `getBoundingClientRect` (see src/__tests__/setup.ts) to return a fixed 600×800 box for
 * every element, which would make any layout-based assertion meaningless.
 *
 * NOTE: this file is `glowMath.ts`, NOT `toolbarGlow.ts`, on purpose — a lowercase
 * `toolbarGlow.ts` would collide with the `ToolbarGlow.tsx` component on case-insensitive
 * filesystems (Windows/macOS), and Vite would resolve `./ToolbarGlow` to the wrong file.
 *
 * Model (column + vertical approach):
 *   • The cursor belongs to exactly ONE section horizontally — the section whose x-range it
 *     is in, or (in the gaps between sections) the horizontally-nearest one. Outside the
 *     toolbar's whole horizontal span (e.g. over the logo / workflow-name to the left) it
 *     belongs to none. This guarantees at most one section ever glows.
 *   • Intensity is driven by VERTICAL distance: full (1) when the cursor is level with /
 *     touching the section, fading to 0 at `reach` px away — so the glow grows as the cursor
 *     approaches the bar from below.
 */

/** The subset of DOMRect the glow math actually reads. */
export interface GlowRect {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

/** How far (px) from a section the approach glow begins to ramp up. */
export const DEFAULT_GLOW_REACH = 120;

export interface ActiveGlow {
  /** Index into the `rects` array of the single section that should glow. */
  index: number;
  /** 0..1 glow strength for that section. */
  intensity: number;
}

/**
 * Picks the single section the cursor is approaching and how strongly it should glow.
 * Returns null when the cursor is outside the toolbar's horizontal extent (→ nothing glows).
 *
 * Horizontal position selects WHICH section (nearest column); vertical distance sets HOW
 * MUCH (squared falloff over `reach`, so the near field ramps up softly).
 */
export function activeSectionGlow(
  rects: readonly GlowRect[],
  cx: number,
  cy: number,
  reach: number = DEFAULT_GLOW_REACH,
): ActiveGlow | null {
  if (rects.length === 0) return null;

  let minLeft = Infinity;
  let maxRight = -Infinity;
  for (const r of rects) {
    if (r.left < minLeft) minLeft = r.left;
    if (r.right > maxRight) maxRight = r.right;
  }
  // Left of the first section / right of the last → don't light anything (logo / name area).
  if (cx < minLeft || cx > maxRight) return null;

  // Horizontally nearest section — 0 distance when the cursor is inside its x-range, so in
  // a gap the nearer neighbour wins and ties resolve to the earlier section deterministically.
  let index = 0;
  let bestH = Infinity;
  for (let i = 0; i < rects.length; i++) {
    const r = rects[i];
    const h = Math.max(r.left - cx, 0, cx - r.right);
    if (h < bestH) {
      bestH = h;
      index = i;
    }
  }

  const r = rects[index];
  const vertical = Math.max(r.top - cy, 0, cy - r.bottom); // 0 when level with the section
  if (reach <= 0) return { index, intensity: vertical === 0 ? 1 : 0 };
  const linear = Math.max(0, 1 - vertical / reach);
  return { index, intensity: linear * linear };
}

export interface BloomGeometry {
  /** Bloom left offset in px, relative to the section's left edge. */
  left: number;
  /** Bloom width in px. */
  width: number;
}

/**
 * Horizontal placement of a section's bloom. With no hovered button it spans the whole section
 * (plus an `overhang` fraction of the section width on each side); with a hovered button it
 * collapses to just under that button (plus `pad` px each side). Coordinates are px relative to
 * the section's left edge — exactly what `.np-glow-bloom`'s `left`/`width` consume.
 */
export function bloomGeometry(
  section: GlowRect,
  button: GlowRect | null,
  overhang: number,
  pad: number,
): BloomGeometry {
  if (button) {
    return {
      left: button.left - section.left - pad,
      width: button.right - button.left + pad * 2,
    };
  }
  const sectionWidth = section.right - section.left;
  return { left: -overhang * sectionWidth, width: sectionWidth * (1 + 2 * overhang) };
}
