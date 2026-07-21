import { describe, it, expect } from 'vitest';
import { ACTIVITY_CATALOG } from '../../lib/activityCatalog.generated';
import { ACTIVITY_CONFIG_COMPONENTS } from '../../components/designer/properties/activityConfigMap';
import { getRegisteredActivityFactTypes } from '../../lib/activityConfigFacts';

/**
 * Cross-registry drift detector for activity wiring.
 *
 * Adding a new activity touches four parallel registries — and TypeScript can't catch
 * a missing entry in any of them because each registry is a plain Record/string-keyed
 * structure:
 *
 *   1. NodePilot.Core.Activities.ActivityCatalog   (backend, C#)
 *   2. activityCatalog.generated.ts                (frontend mirror — verified by
 *                                                   ActivityCatalogFrontendSyncTests on
 *                                                   the C# side)
 *   3. activityConfigMap.ts                        (PropertiesPanel routing → which
 *                                                   React component edits this activity's
 *                                                   config blob)
 *   4. activityConfigFacts.ts                      (pre-publish validation + canvas
 *                                                   summary + smart defaults)
 *   5. index.css                                   (color/bg/border CSS vars — covered by
 *                                                   activityCssPalette.test.ts separately)
 *
 * The frontend-sync test already pins (1) ↔ (2). The CSS-palette test pins (5). This
 * test pins (3) and (4) against the canonical catalog, so a missed registry surfaces at
 * CI time rather than as a runtime "panel renders nothing" / "publish accepts garbage".
 */

describe('Activity registry drift', () => {
  const catalogTypes = new Set<string>(ACTIVITY_CATALOG.map((a) => a.type));

  describe('activityConfigMap (PropertiesPanel routing)', () => {
    // Adding an activity to the catalog without a routing entry leaves the
    // PropertiesPanel with no editor component to render — the side-panel is empty
    // when the user selects the node. This is the failure mode that "feels broken"
    // without throwing any error.
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
    // Unlike activityConfigMap, NOT every catalog entry needs a facts entry — simpler
    // activities (delay, fileHash) get along fine with the default no-op stubs. So we
    // only enforce the orphan check, not the inverse "every catalog entry must have
    // facts" rule (that would be too prescriptive — keep facts opt-in by intent).
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
