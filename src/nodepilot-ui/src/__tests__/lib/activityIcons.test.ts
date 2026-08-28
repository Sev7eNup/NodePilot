import { describe, it, expect } from 'vitest';
import { ACTIVITY_ICONS } from '../../lib/activityCatalog.generated';
import {
  ACTIVITY_ICON_COMPONENTS,
  FALLBACK_ACTIVITY_ICON,
  CUSTOM_ACTIVITY_ICON_CHOICES,
} from '../../lib/activityIcons';

describe('activityIcons', () => {
  it('maps every built-in activity/trigger catalog icon token to a Carbon component', () => {
    // An icon token without a Carbon mapping renders the fallback glyph and raises no error,
    // so parity between the catalog and the component map is checked here.
    const unmapped = Object.values(ACTIVITY_ICONS).filter((token) => !ACTIVITY_ICON_COMPONENTS[token]);
    expect(unmapped).toEqual([]);
  });

  it('maps every custom-activity picker choice to a Carbon component', () => {
    const unmapped = CUSTOM_ACTIVITY_ICON_CHOICES.filter((token) => !ACTIVITY_ICON_COMPONENTS[token]);
    expect(unmapped).toEqual([]);
  });

  it('uses dedicated curly braces for JSON while XML keeps the Carbon Code icon', () => {
    expect(ACTIVITY_ICONS.jsonQuery).toBe('curly_braces');
    expect(ACTIVITY_ICONS.xmlQuery).toBe('code');
    expect(ACTIVITY_ICON_COMPONENTS.curly_braces).toBeDefined();
    expect(ACTIVITY_ICON_COMPONENTS.curly_braces).not.toBe(ACTIVITY_ICON_COMPONENTS.code);
  });

  it('falls back for unknown / legacy tokens', () => {
    expect(ACTIVITY_ICON_COMPONENTS['not-a-real-token']).toBeUndefined();
    expect(FALLBACK_ACTIVITY_ICON).toBeDefined();
    // Resolution contract used at every render site: map lookup ?? fallback.
    const resolved = ACTIVITY_ICON_COMPONENTS['not-a-real-token'] ?? FALLBACK_ACTIVITY_ICON;
    expect(resolved).toBe(FALLBACK_ACTIVITY_ICON);
  });
});
