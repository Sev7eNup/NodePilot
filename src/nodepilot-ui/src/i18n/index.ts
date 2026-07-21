import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import LanguageDetector from 'i18next-browser-languagedetector';

import deCommon from './locales/de/common.json';
import deNav from './locales/de/nav.json';
import deAuth from './locales/de/auth.json';
import deDashboard from './locales/de/dashboard.json';
import deWorkflows from './locales/de/workflows.json';
import deEditor from './locales/de/editor.json';
import deProperties from './locales/de/properties.json';
import deActivities from './locales/de/activities.json';
import deTriggers from './locales/de/triggers.json';
import deExecutions from './locales/de/executions.json';
import deMachines from './locales/de/machines.json';
import deCredentials from './locales/de/credentials.json';
import deSettings from './locales/de/settings.json';
import deUsers from './locales/de/users.json';
import deAudit from './locales/de/audit.json';
import deGlobals from './locales/de/globals.json';
import deMaintenance from './locales/de/maintenance.json';
import deFormat from './locales/de/format.json';
import deErrors from './locales/de/errors.json';
import deLint from './locales/de/lint.json';
import deAi from './locales/de/ai.json';
import deDatabase from './locales/de/database.json';
import deAdminSettings from './locales/de/adminSettings.json';
import deBackup from './locales/de/backup.json';
import deDesigner from './locales/de/designer.json';
import deCustomActivities from './locales/de/customActivities.json';
import deOperations from './locales/de/operations.json';
import deAlerts from './locales/de/alerts.json';
import deSupportLog from './locales/de/supportLog.json';
import deMetrics from './locales/de/metrics.json';

import enCommon from './locales/en/common.json';
import enNav from './locales/en/nav.json';
import enAuth from './locales/en/auth.json';
import enDashboard from './locales/en/dashboard.json';
import enWorkflows from './locales/en/workflows.json';
import enEditor from './locales/en/editor.json';
import enProperties from './locales/en/properties.json';
import enActivities from './locales/en/activities.json';
import enTriggers from './locales/en/triggers.json';
import enExecutions from './locales/en/executions.json';
import enMachines from './locales/en/machines.json';
import enCredentials from './locales/en/credentials.json';
import enSettings from './locales/en/settings.json';
import enUsers from './locales/en/users.json';
import enAudit from './locales/en/audit.json';
import enGlobals from './locales/en/globals.json';
import enMaintenance from './locales/en/maintenance.json';
import enFormat from './locales/en/format.json';
import enErrors from './locales/en/errors.json';
import enLint from './locales/en/lint.json';
import enAi from './locales/en/ai.json';
import enDatabase from './locales/en/database.json';
import enAdminSettings from './locales/en/adminSettings.json';
import enBackup from './locales/en/backup.json';
import enDesigner from './locales/en/designer.json';
import enCustomActivities from './locales/en/customActivities.json';
import enOperations from './locales/en/operations.json';
import enAlerts from './locales/en/alerts.json';
import enSupportLog from './locales/en/supportLog.json';
import enMetrics from './locales/en/metrics.json';

export const SUPPORTED_LANGS = ['de', 'en'] as const;
export type AppLang = (typeof SUPPORTED_LANGS)[number];

export const resources = {
  de: {
    common: deCommon,
    nav: deNav,
    auth: deAuth,
    dashboard: deDashboard,
    workflows: deWorkflows,
    editor: deEditor,
    properties: deProperties,
    activities: deActivities,
    triggers: deTriggers,
    executions: deExecutions,
    machines: deMachines,
    credentials: deCredentials,
    settings: deSettings,
    users: deUsers,
    audit: deAudit,
    globals: deGlobals,
    maintenance: deMaintenance,
    format: deFormat,
    errors: deErrors,
    lint: deLint,
    ai: deAi,
    database: deDatabase,
    adminSettings: deAdminSettings,
    backup: deBackup,
    designer: deDesigner,
    customActivities: deCustomActivities,
    operations: deOperations,
    alerts: deAlerts,
    supportLog: deSupportLog,
    metrics: deMetrics,
  },
  en: {
    common: enCommon,
    nav: enNav,
    auth: enAuth,
    dashboard: enDashboard,
    workflows: enWorkflows,
    editor: enEditor,
    properties: enProperties,
    activities: enActivities,
    triggers: enTriggers,
    executions: enExecutions,
    machines: enMachines,
    credentials: enCredentials,
    settings: enSettings,
    users: enUsers,
    audit: enAudit,
    globals: enGlobals,
    maintenance: enMaintenance,
    format: enFormat,
    errors: enErrors,
    lint: enLint,
    ai: enAi,
    database: enDatabase,
    adminSettings: enAdminSettings,
    backup: enBackup,
    designer: enDesigner,
    customActivities: enCustomActivities,
    operations: enOperations,
    alerts: enAlerts,
    supportLog: enSupportLog,
    metrics: enMetrics,
  },
} as const;

void i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources,
    fallbackLng: 'de',
    supportedLngs: SUPPORTED_LANGS as unknown as string[],
    defaultNS: 'common',
    ns: Object.keys(resources.de),
    interpolation: { escapeValue: false },
    detection: {
      order: ['localStorage', 'navigator'],
      lookupLocalStorage: 'nodepilot.lang',
      caches: ['localStorage'],
    },
    returnNull: false,
  });

if (typeof document !== 'undefined') {
  document.documentElement.setAttribute('lang', i18n.language || 'de');
  i18n.on('languageChanged', (lng) => {
    document.documentElement.setAttribute('lang', lng);
  });
}

export default i18n;
