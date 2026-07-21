import i18n from '../../../i18n';
import { ACTIVITY_CATALOG, ACTIVITY_ICONS } from '../../../lib/activityCatalog.generated';
import { getEnabledCustomActivities } from '../../../lib/customActivities';
import { getActivityLabel } from '../properties/shared';

const entry = (type: string) => ({ type, label: getActivityLabel(type), icon: ACTIVITY_ICONS[type] ?? 'help' });

const sortByLabel = (items: ReturnType<typeof entry>[]) =>
  [...items].sort((a, b) => a.label.localeCompare(b.label));

export type ActivityCategoryKey = 'triggers' | 'actions' | 'controlFlow' | 'logic' | 'annotations' | 'customNodes';

export interface ActivityCategory {
  /** Stable identifier - used for filtering. */
  key: ActivityCategoryKey;
  /** Translated display name. */
  name: string;
  items: { type: string; label: string; icon: string }[];
}

const CATEGORY_TO_KEY = {
  trigger: 'triggers',
  action: 'actions',
  controlFlow: 'controlFlow',
  logic: 'logic'
} as const;

/** Build category list against the current language. Call this from inside a component
 *  that subscribes to the i18n language so labels stay in sync with the language switch. */
export function buildActivityCategories(): ActivityCategory[] {
  const byKey = new Map<ActivityCategoryKey, ReturnType<typeof entry>[]>();
  for (const descriptor of ACTIVITY_CATALOG) {
    const key = CATEGORY_TO_KEY[descriptor.category];
    const items = byKey.get(key) ?? [];
    items.push(entry(descriptor.type));
    byKey.set(key, items);
  }

  return [
    {
      key: 'triggers',
      name: i18n.t('activities:categories.triggers'),
      items: sortByLabel(byKey.get('triggers') ?? [])
    },
    {
      key: 'actions',
      name: i18n.t('activities:categories.actions'),
      items: sortByLabel(byKey.get('actions') ?? [])
    },
    {
      key: 'controlFlow',
      name: i18n.t('activities:categories.controlFlow'),
      items: sortByLabel(byKey.get('controlFlow') ?? [])
    },
    {
      key: 'logic',
      name: i18n.t('activities:categories.logic'),
      items: sortByLabel(byKey.get('logic') ?? [])
    },
    {
      key: 'annotations',
      name: i18n.t('activities:categories.annotations'),
      items: [
        { type: 'note', label: i18n.t('activities:labels.stickyNote'), icon: 'sticky_note_2' }
      ]
    },
    {
      // User-authored custom activities. Built from the runtime catalog (NOT the static generated
      // catalog), so they appear below the built-in categories. Empty → EditorSidebar drops it.
      key: 'customNodes',
      name: i18n.t('activities:categories.customNodes'),
      items: sortByLabel(
        getEnabledCustomActivities().map((c) => ({ type: c.type, label: c.name, icon: c.icon || 'extension' }))
      )
    }
  ];
}
