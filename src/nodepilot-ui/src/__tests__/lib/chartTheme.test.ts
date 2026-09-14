import { createElement } from 'react';
import { act, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { CHART_SERIES_DARK, CHART_SERIES_LIGHT, DEFAULT_CHART_TOKENS, useChartTokens } from '../../lib/chartTheme';

/** The semantic status colours declared in index.css. They are reserved for state
 *  and must never double as a categorical series colour, or a chart legend would
 *  imply an outcome the data does not carry. */
const RESERVED_STATUS = [
  '#16a34a', '#4ade80', // success (light / dark)
  '#f59e0b', '#fbbf24', // warning + running
  '#ba1a1a', '#ffb4ab', // error
  '#818cf8',            // info
  '#fb923c',            // paused + custom
  '#9ca3af',            // skipped
];

describe('chartTheme categorical palette', () => {
  it('CHART_SERIES_LIGHT_and_DARK_haveMatchingSlotCounts', () => {
    // Slot N must mean the same series in both bases — otherwise toggling the
    // theme would re-assign colours to different entities.
    expect(CHART_SERIES_LIGHT).toHaveLength(8);
    expect(CHART_SERIES_DARK).toHaveLength(CHART_SERIES_LIGHT.length);
  });

  it.each([
    ['light', CHART_SERIES_LIGHT],
    ['dark', CHART_SERIES_DARK],
  ])('%s palette contains no duplicate slots', (_base, palette) => {
    expect(new Set(palette).size).toBe(palette.length);
  });

  it.each([
    ['light', CHART_SERIES_LIGHT],
    ['dark', CHART_SERIES_DARK],
  ])('%s palette does not reuse a reserved status colour', (_base, palette) => {
    const collisions = palette.filter((hex) => RESERVED_STATUS.includes(hex.toLowerCase()));
    expect(collisions).toEqual([]);
  });

  it.each([
    ['light', CHART_SERIES_LIGHT],
    ['dark', CHART_SERIES_DARK],
  ])('%s palette is all lowercase 6-digit hex (ECharts cannot resolve CSS vars)', (_base, palette) => {
    for (const hex of palette) expect(hex).toMatch(/^#[0-9a-f]{6}$/);
  });

  it('defaultTokens_areUsableWithoutAProbe', () => {
    // Chart builders must stay pure functions callable in tests and on first paint,
    // before any element has been measured.
    expect(DEFAULT_CHART_TOKENS.axis).toBeTruthy();
    expect(DEFAULT_CHART_TOKENS.grid).toBeTruthy();
    expect(DEFAULT_CHART_TOKENS.series).toHaveLength(8);
    expect(DEFAULT_CHART_TOKENS.reducedDecoration).toBe(false);
  });

  it('gridFallback_keepsItsAlpha', () => {
    // The probe normalises the values it reads to 6-digit hex, but the fallbacks are handed
    // through verbatim: hexifying this one would turn every recessive gridline into a solid
    // slab.
    expect(DEFAULT_CHART_TOKENS.grid).toBe('rgba(148,163,184,.16)');
  });
});

describe('chart token probe', () => {
  it('refreshes decoration and normalized colours on same-base skin changes and restores defaults', async () => {
    function Probe() {
      const { probeRef, tokens } = useChartTokens();
      return createElement('div', { ref: probeRef, 'data-testid': 'probe', 'data-tokens': JSON.stringify(tokens) });
    }
    const originalSkin = document.documentElement.dataset.skin;
    const { unmount } = render(createElement(Probe));
    const probe = screen.getByTestId('probe');
    try {
      act(() => {
        probe.style.setProperty('--np-reduced-decoration', '1');
        probe.style.setProperty('--color-primary', '#abc');
        document.documentElement.dataset.skin = 'light-minimal';
      });
      await waitFor(() => {
        const tokens = JSON.parse(probe.dataset.tokens!);
        expect(tokens.reducedDecoration).toBe(true);
        expect(tokens.primary).toBe('#aabbcc');
        expect(tokens.series).toEqual(CHART_SERIES_LIGHT);
      });
      act(() => {
        probe.style.removeProperty('--np-reduced-decoration');
        document.documentElement.dataset.skin = 'light';
      });
      await waitFor(() => expect(JSON.parse(probe.dataset.tokens!).reducedDecoration).toBe(false));
    } finally {
      unmount();
      if (originalSkin === undefined) delete document.documentElement.dataset.skin;
      else document.documentElement.dataset.skin = originalSkin;
    }
  });
});
