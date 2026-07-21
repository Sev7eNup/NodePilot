import { ACTIVITY_CATALOG, ACTIVITY_ICONS } from '../../../lib/activityCatalog.generated';
import { isCustomActivityType, getCustomActivityFacts } from '../../../lib/customActivities';

export interface ActivityVisual { icon: string; color: string; bgColor: string; borderColor: string }

export const activityConfig: Record<string, ActivityVisual> =
  Object.fromEntries(
    ACTIVITY_CATALOG.map((activity) => [
      activity.type,
      {
        icon: ACTIVITY_ICONS[activity.type] ?? 'help',
        color: `var(--act-${activity.type}-color)`,
        bgColor: `var(--act-${activity.type}-bg)`,
        borderColor: `var(--act-${activity.type}-border)`
      }
    ])
  );

const CUSTOM_FALLBACK_COLOR = '#6366f1'; // indigo — used when a custom activity defines no accent.

/**
 * Resolves the canvas visual for any activity type. Built-ins map to their CSS-var palette; custom
 * activities (custom:&lt;key&gt;) have no `--act-*` vars, so derive a concrete palette from the
 * runtime catalog's icon + accent colour (with an indigo fallback).
 */
export function getActivityVisual(activityType: string): ActivityVisual {
  if (isCustomActivityType(activityType)) {
    const facts = getCustomActivityFacts(activityType);
    const color = facts?.color || CUSTOM_FALLBACK_COLOR;
    return { icon: facts?.icon || 'extension', color, bgColor: `${color}1a`, borderColor: `${color}55` };
  }
  return activityConfig[activityType] ?? activityConfig.runScript;
}
