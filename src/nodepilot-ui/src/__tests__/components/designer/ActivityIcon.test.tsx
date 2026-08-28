import { describe, it, expect, beforeEach } from 'vitest';
import { render } from '@testing-library/react';
import { ActivityIcon } from '../../../components/designer/library/NodeLibrary';
import {
  useCustomActivityCatalogStore,
  type CustomActivityCatalogEntry,
} from '../../../lib/customActivities';

/**
 * ActivityIcon is the palette and picker glyph. It resolves both icon and colour through
 * getActivityVisual, the same resolver the canvas ActivityNode uses, so colours come from
 * the generated `--act-*` design tokens and follow dark mode. These tests pin that the
 * colour arrives as a CSS variable on `style` and never as a Tailwind class.
 */
const customEntry = (over: Partial<CustomActivityCatalogEntry> = {}): CustomActivityCatalogEntry => ({
  id: 'id-1', key: 'disk_check', type: 'custom:disk_check', name: 'Disk Check', description: null,
  icon: 'bolt', color: '#ff8800', runsRemote: false, timeout: 'always',
  inputs: [], outputs: [], isEnabled: true, version: 1, ...over,
});

function renderIcon(type: string, size?: number) {
  const { container } = render(<ActivityIcon type={type} size={size} />);
  const svg = container.querySelector('svg');
  expect(svg).not.toBeNull();
  return svg!;
}

describe('ActivityIcon', () => {
  beforeEach(() => useCustomActivityCatalogStore.getState().setCatalog([]));

  it('rendersBuiltInType_UsesDesignTokenColor', () => {
    const svg = renderIcon('sql');
    expect(svg.style.color).toBe('var(--act-sql-color)');
  });

  it('rendersBuiltInType_CarriesNoTailwindColorLiteral', () => {
    // The colour comes from a token, so no `text-<hue>-<shade>` class may appear.
    for (const type of ['runScript', 'manualTrigger', 'delay', 'decision', 'textFileEdit']) {
      const svg = renderIcon(type);
      expect(svg.getAttribute('class') ?? '').not.toMatch(/\btext-[a-z]+-\d{3}\b/);
      expect(svg.style.color).toBe(`var(--act-${type}-color)`);
    }
  });

  it('rendersTypesThatTheOldColorTableMissed', () => {
    // Every catalog entry has its own token, so none of these fall back to a muted default.
    for (const type of ['textFileEdit', 'forEach', 'startWorkflow', 'returnData', 'llmQuery']) {
      expect(renderIcon(type).style.color).toBe(`var(--act-${type}-color)`);
    }
  });

  it('rendersCustomActivity_UsesRuntimeAccentColor', () => {
    useCustomActivityCatalogStore.getState().setCatalog([customEntry()]);
    // jsdom may serialise a hex literal as rgb(), so accept either spelling of the colour.
    expect(['#ff8800', 'rgb(255, 136, 0)']).toContain(renderIcon('custom:disk_check').style.color);
  });

  it('rendersCustomActivityWithoutColor_FallsBackToIndigoAccent', () => {
    useCustomActivityCatalogStore.getState().setCatalog([customEntry({ color: null })]);
    expect(['#6366f1', 'rgb(99, 102, 241)']).toContain(renderIcon('custom:disk_check').style.color);
  });

  it('rendersUnknownType_FallsBackToRunScriptVisual', () => {
    expect(renderIcon('totallyUnknown').style.color).toBe('var(--act-runScript-color)');
  });

  it('honoursTheSizeProp', () => {
    const svg = renderIcon('sql', 18);
    expect(svg.getAttribute('width')).toBe('18');
    expect(svg.getAttribute('height')).toBe('18');
  });
});
