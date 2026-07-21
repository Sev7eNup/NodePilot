import { describe, it, expect } from 'vitest';
import { TRIGGER_BADGE_META } from '../../lib/triggerBadgeMeta';
import { EXTERNAL_TRIGGER_TYPES } from '../../lib/activityTypes';

/**
 * TRIGGER_BADGE_META is a static lookup table consumed by WorkflowsPage and DashboardPage.
 * It must stay in lockstep with the trigger types listed in EXTERNAL_TRIGGER_TYPES (lib/
 * activityTypes.ts) — those are exactly the 5 trigger kinds that get pill badges in list
 * views. Pin the keys + label/className shape so a refactor can't drop one.
 */

describe('TRIGGER_BADGE_META', () => {
  it('coversAllFiveExternalTriggerTypes', () => {
    expect(Object.keys(TRIGGER_BADGE_META).sort()).toEqual([...EXTERNAL_TRIGGER_TYPES].sort());
  });

  it('everyEntryHasLabelIconAndClassName', () => {
    for (const [key, meta] of Object.entries(TRIGGER_BADGE_META)) {
      expect(meta.label, `label for ${key}`).toBeTruthy();
      expect(meta.icon, `icon for ${key}`).toBeDefined();
      expect(meta.className, `className for ${key}`).toMatch(/bg-/);
      expect(meta.className, `className for ${key}`).toMatch(/text-/);
    }
  });

  it('eachTriggerHasDistinctVisualColor', () => {
    // Visual hint: the 5 trigger types should not share the same className — that would
    // make them indistinguishable in list views.
    const classNames = Object.values(TRIGGER_BADGE_META).map((m) => m.className);
    expect(new Set(classNames).size).toBe(classNames.length);
  });

  it('labelsAreShortEnoughForPillBadges', () => {
    // Pills clip if labels exceed ~14 chars in the dashboard 1-col layout. Bumped
    // from 10 to 14 to accommodate translated labels ("Datei-Watcher", "File Watcher").
    for (const [key, meta] of Object.entries(TRIGGER_BADGE_META)) {
      expect(meta.label.length, `label "${meta.label}" for ${key}`).toBeLessThanOrEqual(14);
    }
  });
});
