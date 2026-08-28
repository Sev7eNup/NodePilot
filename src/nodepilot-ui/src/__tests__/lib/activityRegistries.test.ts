import { describe, it, expect } from 'vitest';
import { ACTIVITY_CATALOG } from '../../lib/activityCatalog.generated';
import { ACTIVITY_CONFIG_COMPONENTS } from '../../components/designer/properties/activityConfigMap';
import { getRegisteredActivityFactTypes } from '../../lib/activityConfigFacts';

/**
 * Cross-registry drift detector for activity wiring. An activity is declared in several
 * parallel string-keyed registries, so TypeScript cannot catch a missing entry. The
 * frontend-sync test pins the backend catalog against activityCatalog.generated.ts and
 * activityCssPalette.test.ts pins the CSS variables; this test pins activityConfigMap
 * (PropertiesPanel routing) and activityConfigFacts against the generated catalog.
 */

describe('Activity registry drift', () => {
  const catalogTypes = new Set<string>(ACTIVITY_CATALOG.map((a) => a.type));

  describe('activityConfigMap (PropertiesPanel routing)', () => {
    // A catalog entry without a routing entry leaves the PropertiesPanel with no editor
    // component, so the side panel stays empty on selection and no error is raised.
    for (const activity of ACTIVITY_CATALOG) {
      it(`routes "${activity.type}" to a config component`, () => {
        expect(
          ACTIVITY_CONFIG_COMPONENTS[activity.type],
          `No PropertiesPanel routing for activity "${activity.type}". ` +
          `Add it to ACTIVITY_CONFIG_COMPONENTS in ` +
          `src/components/designer/properties/activityConfigMap.ts.`
        ).toBeDefined();
      });
    }

    it('has no orphan routes (every routed type exists in the catalog)', () => {
      const orphans = Object.keys(ACTIVITY_CONFIG_COMPONENTS).filter((t) => !catalogTypes.has(t));
      expect(
        orphans,
        `activityConfigMap has routes for unknown activity types: ${orphans.join(', ')}. ` +
        `Either the activity was renamed/removed from ActivityCatalog and the route ` +
        `wasn't cleaned up, or the route uses a typo'd key.`
      ).toEqual([]);
    });
  });

  describe('activityConfigFacts (pre-publish validation + summary)', () => {
    // Facts entries are optional: simple activities such as delay or fileHash use the
    // default no-op stubs. Only the orphan check is enforced, not full catalog coverage.
    it('has no orphan entries (every facts key exists in the catalog)', () => {
      const orphans = getRegisteredActivityFactTypes().filter((t) => !catalogTypes.has(t));
      expect(
        orphans,
        `activityConfigFacts has entries for unknown activity types: ${orphans.join(', ')}. ` +
        `Either the activity was renamed/removed from ActivityCatalog and the facts ` +
        `entry wasn't cleaned up, or the entry uses a typo'd key.`
      ).toEqual([]);
    });
  });
});
