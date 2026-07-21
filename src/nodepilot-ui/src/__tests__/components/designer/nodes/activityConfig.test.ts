import { describe, it, expect, beforeEach } from 'vitest';
import { getActivityVisual, activityConfig } from '../../../../components/designer/nodes/activityConfig';
import {
  useCustomActivityCatalogStore,
  type CustomActivityCatalogEntry,
} from '../../../../lib/customActivities';

const customEntry = (over: Partial<CustomActivityCatalogEntry> = {}): CustomActivityCatalogEntry => ({
  id: 'id-1', key: 'disk_check', type: 'custom:disk_check', name: 'Disk Check', description: null,
  icon: 'bolt', color: '#ff8800', runsRemote: false, timeout: 'always',
  inputs: [], outputs: [], isEnabled: true, version: 1, ...over,
});

describe('getActivityVisual — built-in activities', () => {
  beforeEach(() => useCustomActivityCatalogStore.getState().setCatalog([]));

  it('maps a built-in type to its CSS-var palette + catalog icon', () => {
    expect(getActivityVisual('sql')).toEqual({
      icon: 'storage',
      color: 'var(--act-sql-color)',
      bgColor: 'var(--act-sql-bg)',
      borderColor: 'var(--act-sql-border)',
    });
  });

  it('derives the three CSS vars from the activity type', () => {
    const v = getActivityVisual('runScript');
    expect(v.icon).toBe('terminal');
    expect(v.color).toBe('var(--act-runScript-color)');
    expect(v.bgColor).toBe('var(--act-runScript-bg)');
    expect(v.borderColor).toBe('var(--act-runScript-border)');
  });
});

describe('getActivityVisual — custom activities', () => {
  beforeEach(() => useCustomActivityCatalogStore.getState().setCatalog([]));

  it('derives a concrete palette (with 1a/55 opacity suffixes) from runtime facts', () => {
    useCustomActivityCatalogStore.getState().setCatalog([customEntry()]);
    expect(getActivityVisual('custom:disk_check')).toEqual({
      icon: 'bolt',
      color: '#ff8800',
      bgColor: '#ff88001a',
      borderColor: '#ff880055',
    });
  });

  it('falls back to the indigo accent when a custom activity defines no color', () => {
    useCustomActivityCatalogStore.getState().setCatalog([
      customEntry({ color: null, icon: '' }),
    ]);
    const v = getActivityVisual('custom:disk_check');
    expect(v.color).toBe('#6366f1');
    expect(v.bgColor).toBe('#6366f11a');
    expect(v.borderColor).toBe('#6366f155');
    // empty icon → 'extension' default
    expect(v.icon).toBe('extension');
  });

  it('falls back to indigo/extension for a custom type with no runtime facts', () => {
    // store is empty → getCustomActivityFacts returns undefined
    const v = getActivityVisual('custom:not_loaded');
    expect(v).toEqual({
      icon: 'extension',
      color: '#6366f1',
      bgColor: '#6366f11a',
      borderColor: '#6366f155',
    });
  });
});

describe('getActivityVisual — unknown fallback', () => {
  beforeEach(() => useCustomActivityCatalogStore.getState().setCatalog([]));

  it('falls back to the runScript palette for an unknown built-in type', () => {
    expect(getActivityVisual('totallyUnknown')).toEqual(activityConfig.runScript);
    expect(getActivityVisual('totallyUnknown').color).toBe('var(--act-runScript-color)');
    expect(getActivityVisual('totallyUnknown').icon).toBe('terminal');
  });
});
