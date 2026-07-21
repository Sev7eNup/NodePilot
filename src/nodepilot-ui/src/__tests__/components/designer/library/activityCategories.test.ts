import { describe, it, expect, beforeEach } from 'vitest';
import { buildActivityCategories } from '../../../../components/designer/library/activityCategories';
import { ACTIVITY_CATALOG } from '../../../../lib/activityCatalog.generated';
import {
  useCustomActivityCatalogStore,
  type CustomActivityCatalogEntry,
} from '../../../../lib/customActivities';

const customEntry = (over: Partial<CustomActivityCatalogEntry> = {}): CustomActivityCatalogEntry => ({
  id: 'id-1', key: 'k', type: 'custom:k', name: 'Custom', description: null,
  icon: 'extension', color: null, runsRemote: false, timeout: 'always',
  inputs: [], outputs: [], isEnabled: true, version: 1, ...over,
});

// Activity types the backend catalog groups under each built-in category (returnData is
// deliberately category controlFlow, so it shows up in the controlFlow list — verified here).
const catalogTypesFor = (category: string) =>
  ACTIVITY_CATALOG.filter((a) => a.category === category).map((a) => a.type).sort();

describe('buildActivityCategories', () => {
  beforeEach(() => useCustomActivityCatalogStore.getState().setCatalog([]));

  it('returns the six categories in the fixed order', () => {
    const cats = buildActivityCategories();
    expect(cats.map((c) => c.key)).toEqual([
      'triggers', 'actions', 'controlFlow', 'logic', 'annotations', 'customNodes',
    ]);
  });

  it('translates the category display names (en locale)', () => {
    const byKey = Object.fromEntries(buildActivityCategories().map((c) => [c.key, c.name]));
    expect(byKey.triggers).toBe('Triggers');
    expect(byKey.actions).toBe('Actions');
    expect(byKey.controlFlow).toBe('Control Flow');
    expect(byKey.logic).toBe('Logic');
    expect(byKey.annotations).toBe('Annotations');
    expect(byKey.customNodes).toBe('Custom Nodes');
  });

  it('places each catalog activity into the mapped category', () => {
    const cats = buildActivityCategories();
    const typesOf = (key: string) => cats.find((c) => c.key === key)!.items.map((i) => i.type).sort();

    expect(typesOf('triggers')).toEqual(catalogTypesFor('trigger'));
    expect(typesOf('actions')).toEqual(catalogTypesFor('action'));
    expect(typesOf('controlFlow')).toEqual(catalogTypesFor('controlFlow'));
    expect(typesOf('logic')).toEqual(catalogTypesFor('logic'));
  });

  it('keeps returnData in the controlFlow category', () => {
    const controlFlow = buildActivityCategories().find((c) => c.key === 'controlFlow')!;
    expect(controlFlow.items.map((i) => i.type)).toContain('returnData');
  });

  it('sorts the items within every catalog-driven category by label', () => {
    for (const cat of buildActivityCategories()) {
      if (cat.key === 'annotations' || cat.key === 'customNodes') continue;
      const labels = cat.items.map((i) => i.label);
      const sorted = [...labels].sort((a, b) => a.localeCompare(b));
      expect(labels, `${cat.key} not label-sorted`).toEqual(sorted);
    }
  });

  it('gives annotations a single fixed sticky-note entry', () => {
    const annotations = buildActivityCategories().find((c) => c.key === 'annotations')!;
    expect(annotations.items).toEqual([
      { type: 'note', label: 'Sticky Note', icon: 'sticky_note_2' },
    ]);
  });

  it('leaves customNodes empty when no custom activities are registered', () => {
    const customNodes = buildActivityCategories().find((c) => c.key === 'customNodes')!;
    expect(customNodes.items).toEqual([]);
  });

  it('merges enabled custom activities into customNodes, label-sorted, excluding disabled', () => {
    useCustomActivityCatalogStore.getState().setCatalog([
      customEntry({ id: 'z', key: 'zebra', type: 'custom:zebra', name: 'Zebra', icon: 'pets' }),
      customEntry({ id: 'a', key: 'alpha', type: 'custom:alpha', name: 'Alpha', icon: 'star' }),
      customEntry({ id: 'd', key: 'draft', type: 'custom:draft', name: 'Draft', isEnabled: false }),
    ]);

    const customNodes = buildActivityCategories().find((c) => c.key === 'customNodes')!;
    expect(customNodes.items).toEqual([
      { type: 'custom:alpha', label: 'Alpha', icon: 'star' },
      { type: 'custom:zebra', label: 'Zebra', icon: 'pets' },
    ]);
  });

  it('uses the extension fallback icon for a custom activity with no icon', () => {
    useCustomActivityCatalogStore.getState().setCatalog([
      customEntry({ id: 'n', key: 'noicon', type: 'custom:noicon', name: 'No Icon', icon: '' }),
    ]);
    const customNodes = buildActivityCategories().find((c) => c.key === 'customNodes')!;
    expect(customNodes.items).toEqual([
      { type: 'custom:noicon', label: 'No Icon', icon: 'extension' },
    ]);
  });
});
