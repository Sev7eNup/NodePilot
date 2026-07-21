import { describe, it, expect } from 'vitest';
import { groupLabelFontSize } from '../../lib/groupLabel';

describe('groupLabelFontSize', () => {
  it('returnsTheBaseSizeAtZoom1', () => {
    expect(groupLabelFontSize(1)).toBe(16);
  });

  it('keepsTheBaseWhenZoomedInSoItGrowsNaturallyWithTheCanvas', () => {
    // base wins (floor 13/2 = 6.5 < 16); on screen = 16 * 2 = 32px → grows when zooming in.
    expect(groupLabelFontSize(2)).toBe(16);
    expect(groupLabelFontSize(3)).toBe(16);
  });

  it('floorsTheOnScreenSizeWhenZoomedOut', () => {
    // zoom 0.5 → 13/0.5 = 26 flow px → on screen 26 * 0.5 = 13px (the readable floor).
    expect(groupLabelFontSize(0.5)).toBe(26);
    expect(groupLabelFontSize(0.5) * 0.5).toBeCloseTo(13);
    // zoom 0.25 → 52 flow px → 52 * 0.25 = 13px on screen.
    expect(groupLabelFontSize(0.25)).toBe(52);
    expect(groupLabelFontSize(0.25) * 0.25).toBeCloseTo(13);
  });

  it('honorsCustomBaseAndFloor', () => {
    expect(groupLabelFontSize(1, 20, 14)).toBe(20);
    expect(groupLabelFontSize(0.5, 20, 14)).toBe(28); // 14/0.5
  });

  it('fallsBackToBaseForInvalidOrZeroZoom', () => {
    expect(groupLabelFontSize(0)).toBe(16);
    expect(groupLabelFontSize(Number.NaN)).toBe(16);
    expect(groupLabelFontSize(-1)).toBe(16);
  });
});
