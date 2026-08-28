import {
  Catalog,
  DataBase,
  FolderDetails,
  Time,
  Touch_1,
  Webhook,
  type CarbonIconType,
} from '@carbon/icons-react';

/**
 * Shared metadata for the primary trigger types: an icon, an accent colour and an i18n key in
 * the designer namespace for the label. It lives in its own module so WorkflowBrowser and
 * WorkflowInfoCard can both use it without importing each other.
 *
 * `labelKey` reuses the localised `browser.trigger.*` strings; `browser.noTrigger` is the
 * fallback when a workflow has no recognised trigger.
 */
export interface TriggerMeta {
  labelKey: string;
  icon: CarbonIconType;
  color: string;
}

export const TRIGGER_META: Record<string, TriggerMeta> = {
  manualTrigger:      { labelKey: 'browser.trigger.manual',    icon: Touch_1,         color: 'text-on-surface-variant' },
  scheduleTrigger:    { labelKey: 'browser.trigger.schedule',  icon: Time,        color: 'text-purple-600' },
  webhookTrigger:     { labelKey: 'browser.trigger.webhook',   icon: Webhook,      color: 'text-sky-600' },
  fileWatcherTrigger: { labelKey: 'browser.trigger.fileWatch', icon: FolderDetails, color: 'text-amber-600' },
  databaseTrigger:    { labelKey: 'browser.trigger.dbPoll',    icon: DataBase,     color: 'text-emerald-600' },
  eventLogTrigger:    { labelKey: 'browser.trigger.eventLog',  icon: Catalog,   color: 'text-rose-600' },
};
