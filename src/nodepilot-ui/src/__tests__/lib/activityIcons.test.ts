import { describe, it, expect } from 'vitest';
import { ACTIVITY_ICONS } from '../../lib/activityCatalog.generated';
import {
  ACTIVITY_ICON_COMPONENTS,
  FALLBACK_ACTIVITY_ICON,
  CUSTOM_ACTIVITY_ICON_CHOICES,
} from '../../lib/activityIcons';

describe('activityIcons', () => {
  it('maps every built-in activity/trigger catalog icon token to a Carbon component', () => {
    // Guards catalog<->map parity: a new activity whose icon token has no Carbon mapping would
    // silently render the fallback glyph instead of erroring. Fail loudly here instead.
    const unmapped = Object.values(ACTIVITY_ICONS).filter((token) => !ACTIVITY_ICON_COMPONENTS[token]);
    expect(unmapped).toEqual([]);
  });

  it('maps every custom-activity picker choice to a Carbon component', () => {
    const unmapped = CUSTOM_ACTIVITY_ICON_CHOICES.filter((token) => !ACTIVITY_ICON_COMPONENTS[token]);
    expect(unmapped).toEqual([]);
  });

  it('falls back for unknown / legacy tokens', () => {
    expect(ACTIVITY_ICON_COMPONENTS['not-a-real-token']).toBeUndefined();
    expect(FALLBACK_ACTIVITY_ICON).toBeDefined();
    // Resolution contract used at every render site: map lookup ?? fallback.
    const resolved = ACTIVITY_ICON_COMPONENTS['not-a-real-token'] ?? FALLBACK_ACTIVITY_ICON;
    expect(resolved).toBe(FALLBACK_ACTIVITY_ICON);
  });
});
