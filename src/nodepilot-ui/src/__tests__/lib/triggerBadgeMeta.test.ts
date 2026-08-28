import { describe, it, expect } from 'vitest';
import { TRIGGER_BADGE_META } from '../../lib/triggerBadgeMeta';
import { EXTERNAL_TRIGGER_TYPES } from '../../lib/activityTypes';

/**
 * TRIGGER_BADGE_META is a static lookup table used by WorkflowsPage and DashboardPage.
 * It must stay in sync with EXTERNAL_TRIGGER_TYPES (lib/activityTypes.ts), the trigger
 * kinds that get pill badges in list views. These tests pin the keys and the
 * label/className shape so a refactor cannot drop one.
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
    // Distinct classNames keep the trigger types visually distinguishable in list views.
    const classNames = Object.values(TRIGGER_BADGE_META).map((m) => m.className);
    expect(new Set(classNames).size).toBe(classNames.length);
  });

  it('labelsAreShortEnoughForPillBadges', () => {
    // Pill badges clip when a label exceeds 14 characters in the single-column dashboard
    // layout. The limit leaves room for translated labels.
    for (const [key, meta] of Object.entries(TRIGGER_BADGE_META)) {
      expect(meta.label.length, `label "${meta.label}" for ${key}`).toBeLessThanOrEqual(14);
    }
  });
});
