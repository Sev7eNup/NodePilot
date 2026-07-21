import { describe, it, expect } from 'vitest';
import {
  activeSectionGlow,
  bloomGeometry,
  DEFAULT_GLOW_REACH,
  type GlowRect,
} from '../../../../components/designer/header/glowMath';

/**
 * Pure geometry — tested with hand-built rects on purpose: src/__tests__/setup.ts
 * monkey-patches getBoundingClientRect to a fixed 600×800 box for every element, so a
 * layout-driven test would be meaningless. Keeping the math DOM-free sidesteps that.
 *
 * Two sections side by side, level at y∈[10,40]:
 *   sec0: x∈[100,200]   sec1: x∈[260,360]   (gap 200..260, midpoint 230)
 */
const SECTIONS: GlowRect[] = [
  { left: 100, right: 200, top: 10, bottom: 40 },
  { left: 260, right: 360, top: 10, bottom: 40 },
];

describe('activeSectionGlow — section selection', () => {
  it('returns null for an empty section list', () => {
    expect(activeSectionGlow([], 150, 25)).toBeNull();
  });

  it('returns null left of the whole bar (logo / workflow-name area)', () => {
    expect(activeSectionGlow(SECTIONS, 50, 25)).toBeNull();
  });

  it('returns null right of the whole bar', () => {
    expect(activeSectionGlow(SECTIONS, 400, 25)).toBeNull();
  });

  it('selects the section the cursor is horizontally inside', () => {
    expect(activeSectionGlow(SECTIONS, 150, 25)?.index).toBe(0);
    expect(activeSectionGlow(SECTIONS, 300, 25)?.index).toBe(1);
  });

  it('in a gap, selects the horizontally NEAREST section (never both)', () => {
    expect(activeSectionGlow(SECTIONS, 220, 25)?.index).toBe(0); // 20 from sec0 vs 40 from sec1
    expect(activeSectionGlow(SECTIONS, 240, 25)?.index).toBe(1); // 40 vs 20
  });

  it('resolves the gap midpoint deterministically to the earlier section', () => {
    expect(activeSectionGlow(SECTIONS, 230, 25)?.index).toBe(0); // tie → first
  });
});

describe('activeSectionGlow — vertical approach intensity', () => {
  it('is full (1) when the cursor is level with the section', () => {
    expect(activeSectionGlow(SECTIONS, 150, 25)?.intensity).toBe(1);
  });

  it('is full (1) when the cursor touches the bottom edge', () => {
    expect(activeSectionGlow(SECTIONS, 150, 40)?.intensity).toBe(1);
  });

  it('eases (squares) as the cursor approaches from below', () => {
    // Half a reach below the bottom edge → linear 0.5, squared → 0.25
    const cy = 40 + DEFAULT_GLOW_REACH / 2;
    expect(activeSectionGlow(SECTIONS, 150, cy)?.intensity).toBeCloseTo(0.25, 5);
  });

  it('is 0 exactly at the reach distance below', () => {
    expect(activeSectionGlow(SECTIONS, 150, 40 + DEFAULT_GLOW_REACH)?.intensity).toBe(0);
  });

  it('clamps to 0 beyond the reach distance', () => {
    expect(activeSectionGlow(SECTIONS, 150, 40 + DEFAULT_GLOW_REACH + 80)?.intensity).toBe(0);
  });

  it('grows as the cursor gets closer (monotonic)', () => {
    const far = activeSectionGlow(SECTIONS, 150, 40 + 90)!.intensity;
    const near = activeSectionGlow(SECTIONS, 150, 40 + 30)!.intensity;
    expect(near).toBeGreaterThan(far);
    expect(far).toBeGreaterThan(0);
  });

  it('honours a custom reach', () => {
    expect(activeSectionGlow(SECTIONS, 150, 40 + 50, 100)?.intensity).toBeCloseTo(0.25, 5);
    expect(activeSectionGlow(SECTIONS, 150, 40 + 100, 100)?.intensity).toBe(0);
  });

  it('with reach<=0, only a touching cursor lights (no divide-by-zero)', () => {
    expect(activeSectionGlow(SECTIONS, 150, 25, 0)?.intensity).toBe(1);
    expect(activeSectionGlow(SECTIONS, 150, 80, 0)?.intensity).toBe(0);
  });
});

describe('bloomGeometry', () => {
  const section: GlowRect = { left: 100, right: 200, top: 10, bottom: 40 }; // 100px wide

  it('spans the whole section (plus overhang) when no button is hovered', () => {
    expect(bloomGeometry(section, null, 0.04, 4)).toEqual({ left: -4, width: 108 });
  });

  it('collapses onto a hovered button (plus pad each side)', () => {
    const button: GlowRect = { left: 130, right: 170, top: 12, bottom: 38 };
    // left = 130 - 100 - 4 = 26; width = 40 + 8 = 48
    expect(bloomGeometry(section, button, 0.04, 4)).toEqual({ left: 26, width: 48 });
  });

  it('keeps button coordinates relative to the section left edge', () => {
    const button: GlowRect = { left: 100, right: 136, top: 12, bottom: 38 }; // at the section start
    expect(bloomGeometry(section, button, 0.04, 4)).toEqual({ left: -4, width: 44 });
  });
});
