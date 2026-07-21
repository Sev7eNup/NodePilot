import { Catalog, DataBase, FolderDetails, Time, Webhook, type CarbonIconType } from '@carbon/icons-react';
import i18n from '../i18n';

// Shared badge metadata for the 5 non-manual trigger types.
// `label` is resolved at call time via a getter so the language switch is live.
type TriggerBadge = { readonly label: string; icon: CarbonIconType; className: string };

function badge(key: string, icon: CarbonIconType, className: string): TriggerBadge {
  return {
    get label() {
      return i18n.t(`triggers:badges.${key}`);
    },
    icon,
    className,
  };
}

export const TRIGGER_BADGE_META: Record<string, TriggerBadge> = {
  scheduleTrigger:    badge('schedule',    Time,        'bg-purple-100 text-purple-700 dark:bg-purple-400/15 dark:text-purple-300'),
  webhookTrigger:     badge('webhook',     Webhook,      'bg-sky-100 text-sky-700 dark:bg-sky-400/15 dark:text-sky-300'),
  fileWatcherTrigger: badge('fileWatcher', FolderDetails, 'bg-amber-100 text-amber-700 dark:bg-amber-400/15 dark:text-amber-300'),
  databaseTrigger:    badge('database',    DataBase,     'bg-emerald-100 text-emerald-700 dark:bg-emerald-400/15 dark:text-emerald-300'),
  eventLogTrigger:    badge('eventLog',    Catalog,   'bg-rose-100 text-rose-700 dark:bg-rose-400/15 dark:text-rose-300'),
};
