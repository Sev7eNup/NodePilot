import { describe, it, expect, beforeEach } from 'vitest';
import { render } from '@testing-library/react';
import { ActivityIcon } from '../../../components/designer/library/NodeLibrary';
import {
  useCustomActivityCatalogStore,
  type CustomActivityCatalogEntry,
} from '../../../lib/customActivities';

/**
 * ActivityIcon is the palette/picker glyph. It used to carry its own 23-entry table of
 * Tailwind colour literals (`text-blue-600`, …) parallel to the generated `--act-*` design
 * tokens — incomplete and with no dark-mode variant. It now resolves icon AND colour through
 * getActivityVisual, the same resolver the canvas ActivityNode uses. These tests pin that:
 * the colour must arrive as a CSS variable on `style`, never as a Tailwind class.
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
    // The whole point of the token switch: no `text-<hue>-<shade>` class may come back.
    for (const type of ['runScript', 'manualTrigger', 'delay', 'decision', 'textFileEdit']) {
      const svg = renderIcon(type);
      expect(svg.getAttribute('class') ?? '').not.toMatch(/\btext-[a-z]+-\d{3}\b/);
      expect(svg.style.color).toBe(`var(--act-${type}-color)`);
    }
  });

  it('rendersTypesThatTheOldColorTableMissed', () => {
    // These five were absent from the deleted `iconColors` map and fell through to the muted
    // default; they now get their own token like every other catalog entry.
    for (const type of ['textFileEdit', 'forEach', 'startWorkflow', 'returnData', 'llmQuery']) {
      expect(renderIcon(type).style.color).toBe(`var(--act-${type}-color)`);
    }
  });

  it('rendersCustomActivity_UsesRuntimeAccentColor', () => {
    useCustomActivityCatalogStore.getState().setCatalog([customEntry()]);
    // jsdom may serialise a hex literal as rgb() — accept either spelling of the same colour.
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
