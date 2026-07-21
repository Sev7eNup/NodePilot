import i18n from '../i18n';
import { ACTIVITY_TYPES } from './activityTypes';

export type ActivityConfig = Record<string, unknown>;
export type RequiredConfigChecker = (config: ActivityConfig) => string | null;
export type ActivitySummarizer = (config: ActivityConfig) => string;

export interface SmartDefaults {
  targetMachineId?: string | null;
  credentialId?: string | null;
  config?: ActivityConfig;
}

interface SmartDefaultContext {
  lastData: ActivityConfig;
  lastConfig: ActivityConfig;
}

interface ActivityConfigFacts {
  requiredConfig?: RequiredConfigChecker;
  summarize?: ActivitySummarizer;
  smartDefaults?: (context: SmartDefaultContext) => SmartDefaults;
}

const isTemplate = (value: unknown): boolean =>
  typeof value === 'string' && value.includes('{{');

const hasValue = (value: unknown): boolean =>
  value !== undefined && value !== null && value !== '' && !Number.isNaN(value);

const hasValueOrTemplate = (value: unknown): boolean => isTemplate(value) || hasValue(value);

const inheritExecutionContext = ({ lastData }: SmartDefaultContext): SmartDefaults => ({
  targetMachineId: (lastData.targetMachineId as string | null) ?? null,
  credentialId: (lastData.credentialId as string | null) ?? null,
});

const summarizePathOperation = (config: ActivityConfig): string => {
  const op = (config.operation as string) || 'copy';
  const path = (config.path as string) || i18n.t('activities:summaries.noPath');
  if (op === 'rename') return i18n.t('activities:summaries.fileOpRename', {
    path,
    newName: (config.newName as string) || i18n.t('activities:summaries.noNewName'),
  });
  if (op === 'copy' || op === 'move') return i18n.t('activities:summaries.fileOpCopyMove', {
    op,
    path,
    destination: (config.destination as string) || i18n.t('activities:summaries.noDestination'),
  });
  return i18n.t('activities:summaries.fileOpOther', { op, path });
};

const requirePathOperation = (knownOps: string[]): RequiredConfigChecker => (config) => {
  const op = (config.operation as string) || 'copy';
  if (!hasValueOrTemplate(config.path)) return 'Pfad ist erforderlich.';
  if ((op === 'copy' || op === 'move') && !hasValueOrTemplate(config.destination))
    return 'Ziel (destination) ist erforderlich.';
  if (op === 'rename' && !hasValueOrTemplate(config.newName))
    return 'Neuer Name (newName) ist erforderlich.';
  if (!knownOps.includes(op)) return `Unbekannte Operation: ${op}. Erlaubt: ${knownOps.join(', ')}.`;
  return null;
};

export function extractBaseUrl(url: string): string {
  if (!url) return '';
  if (url.startsWith('{{') && url.endsWith('}}')) return url;

  const schemeMatch = /^([a-zA-Z][a-zA-Z0-9+.-]*):\/\//.exec(url);
  if (!schemeMatch) {
    const lastSlash = url.lastIndexOf('/');
    if (lastSlash <= 0) return url;
    return url.slice(0, lastSlash);
  }

  const schemeEnd = schemeMatch[0].length;
  const rest = url.slice(schemeEnd);
  const queryIdx = rest.search(/[?#]/);
  const cleanRest = queryIdx >= 0 ? rest.slice(0, queryIdx) : rest;
  const firstPathSlash = cleanRest.indexOf('/');
  if (firstPathSlash < 0) return url.slice(0, schemeEnd) + cleanRest;
  const secondPathSlash = cleanRest.indexOf('/', firstPathSlash + 1);
  const sliceEnd = secondPathSlash < 0 ? cleanRest.length : secondPathSlash;
  return url.slice(0, schemeEnd) + cleanRest.slice(0, sliceEnd);
}

const ACTIVITY_CONFIG_FACTS: Record<string, ActivityConfigFacts> = {
  [ACTIVITY_TYPES.RUN_SCRIPT]: {
    requiredConfig: (config) => !hasValueOrTemplate(config.script) ? 'Script darf nicht leer sein.' : null,
    summarize: (config) => {
      const script = (config.script as string) || '';
      if (!script) return i18n.t('activities:summaries.runScriptNoScript');
      const lines = script.split('\n').filter(Boolean);
      return lines.length > 3 ? lines.slice(0, 3).join('\n') + '\n...' : lines.join('\n');
    },
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.FILE_OPERATION]: {
    requiredConfig: requirePathOperation(['copy', 'move', 'delete', 'exists', 'create', 'rename']),
    summarize: summarizePathOperation,
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.FOLDER_OPERATION]: {
    requiredConfig: requirePathOperation(['copy', 'move', 'delete', 'exists', 'list', 'create', 'rename']),
    summarize: summarizePathOperation,
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.FILE_HASH]: {
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.ZIP_OPERATION]: {
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.SERVICE_MANAGEMENT]: {
    requiredConfig: (config) => {
      if (!hasValueOrTemplate(config.serviceName)) return 'Service-Name ist erforderlich.';
      if (!hasValueOrTemplate(config.action)) return 'Aktion ist erforderlich.';
      const action = (config.action as string) || '';
      if (action === 'create' && !hasValueOrTemplate(config.binaryPath))
        return 'Binary-Pfad (binaryPath) ist erforderlich f\u00fcr Create.';
      if (action === 'setStartType' && !hasValueOrTemplate(config.startupType))
        return 'Startart (startupType) ist erforderlich f\u00fcr Set Startup Type.';
      return null;
    },
    summarize: (config) => `${(config.action as string) || 'status'} "${(config.serviceName as string) || '...'}"`,
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.SCHEDULED_TASK]: {
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.REGISTRY_OPERATION]: {
    requiredConfig: (config) => {
      if (!hasValueOrTemplate(config.keyPath)) return 'Registry-Pfad (keyPath) ist erforderlich.';
      if (!hasValueOrTemplate(config.operation)) return 'Operation ist erforderlich.';
      const op = String(config.operation || '').toLowerCase();
      const knownOps = ['read', 'write', 'deletevalue', 'deletekey', 'createkey', 'exists', 'listsubkeys', 'listvalues'];
      if (!knownOps.includes(op)) return `Unbekannte Operation: ${config.operation}`;
      if (op === 'write') {
        if (!hasValueOrTemplate(config.valueName)) return "Operation 'write' ben\u00f6tigt Value-Name.";
        if (config.valueType !== undefined && config.valueType !== '') {
          const allowedTypes = ['String', 'ExpandString', 'Binary', 'DWord', 'MultiString', 'QWord'];
          if (!allowedTypes.includes(String(config.valueType))) return `Unbekannter Value-Typ: ${config.valueType}`;
        }
      }
      if (op === 'deletevalue' && !hasValueOrTemplate(config.valueName)) {
        return "Operation 'deleteValue' ben\u00f6tigt Value-Name.";
      }
      return null;
    },
    summarize: (config) => `${(config.operation as string) || 'read'}: ${(config.keyPath as string) || '...'}`,
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.WMI_QUERY]: {
    requiredConfig: (config) => {
      const mode = ((config.mode as string) || 'query').toLowerCase();
      if (mode === 'wql') {
        return !hasValueOrTemplate(config.query) ? 'WQL-Query ist im wql-Modus erforderlich.' : null;
      }
      if (mode === 'invokemethod') {
        if (!hasValueOrTemplate(config.className)) return 'WMI-Klassenname ist erforderlich.';
        if (!hasValueOrTemplate(config.methodName)) return 'Methodenname ist im invokeMethod-Modus erforderlich.';
        return null;
      }
      return !hasValueOrTemplate(config.className) ? 'WMI-Klassenname ist erforderlich.' : null;
    },
    summarize: (config) => {
      const mode = ((config.mode as string) || 'query').toLowerCase();
      if (mode === 'wql') {
        const query = (config.query as string) || '';
        return query ? `WQL: ${query.split('\n')[0].slice(0, 60)}${query.length > 60 ? '\u2026' : ''}` : i18n.t('activities:summaries.wmiNoQuery');
      }
      if (mode === 'invokemethod') {
        const className = (config.className as string) || i18n.t('activities:summaries.wmiNoClass');
        const methodName = (config.methodName as string) || i18n.t('activities:summaries.wmiNoMethod');
        return `${className}.${methodName}()`;
      }
      return (config.className as string) || i18n.t('activities:summaries.wmiNoClass');
    },
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.START_PROGRAM]: {
    requiredConfig: (config) => !hasValueOrTemplate(config.filePath) ? 'Dateipfad (filePath) ist erforderlich.' : null,
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.POWER_MANAGEMENT]: {
    requiredConfig: (config) => !hasValueOrTemplate(config.action) ? 'Aktion ist erforderlich.' : null,
    summarize: (config) => {
      const action = (config.action as string) || 'shutdown';
      const delay = (config.delaySeconds as number) ?? 0;
      return delay > 0 ? i18n.t('activities:summaries.powerInDelay', { action, delay }) : action;
    },
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.WAIT_FOR_CONDITION]: {
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.REST_API]: {
    requiredConfig: (config) => !hasValueOrTemplate(config.url) ? 'URL ist erforderlich.' : null,
    summarize: (config) => `${(config.method as string) || 'GET'} ${(config.url as string) || '...'}`,
    smartDefaults: ({ lastConfig }) => {
      const baseUrl = extractBaseUrl((lastConfig.url as string) || '');
      return baseUrl ? { config: { url: baseUrl } } : {};
    },
  },
  [ACTIVITY_TYPES.SQL]: {
    requiredConfig: (config) => {
      const provider = (config.provider as string) || 'sqlserver';
      const builderKey = provider === 'sqlite' ? 'dataSource' : provider === 'postgres' ? 'host' : 'server';
      const hasBuilder = hasValueOrTemplate(config[builderKey]);
      if (!hasValueOrTemplate(config.connectionRef) && !hasBuilder && !hasValueOrTemplate(config.connectionString))
        return 'Connection-Daten fehlen - entweder Connection Ref, Builder-Felder (Server/Host/Datei) oder Connection String setzen.';
      if (!hasValueOrTemplate(config.query)) return 'SQL-Query darf nicht leer sein.';
      return null;
    },
    summarize: (config) => {
      const query = (config.query as string) || '';
      const provider = (config.provider as string) || 'sqlserver';
      return query ? `${provider}: ${query.split('\n')[0].slice(0, 60)}${query.length > 60 ? '\u2026' : ''}` : i18n.t('activities:summaries.sqlNoQuery');
    },
    smartDefaults: ({ lastConfig }) => {
      const defaults: ActivityConfig = {};
      if (lastConfig.provider) defaults.provider = lastConfig.provider;
      if (lastConfig.connectionRef) defaults.connectionRef = lastConfig.connectionRef;
      return Object.keys(defaults).length > 0 ? { config: defaults } : {};
    },
  },
  [ACTIVITY_TYPES.TEXT_FILE_EDIT]: {
    requiredConfig: (config) => {
      const op = (config.operation as string) || 'append';
      if (!hasValueOrTemplate(config.path)) return 'Pfad ist erforderlich.';
      const knownOps = ['append', 'prepend', 'insert', 'replaceLine', 'delete', 'replace'];
      if (!knownOps.includes(op)) return `Unbekannte Operation: ${op}. Erlaubt: ${knownOps.join(', ')}.`;

      const needsContent = op === 'append' || op === 'prepend' || op === 'insert' || op === 'replaceLine';
      if (needsContent && !hasValueOrTemplate(config.content))
        return `Operation '${op}' benötigt 'content'.`;

      const needsLineNumber = op === 'insert' || op === 'replaceLine';
      if (needsLineNumber && !hasValue(config.lineNumber))
        return `Operation '${op}' benötigt 'lineNumber' (≥ 1).`;

      if (op === 'delete') {
        const hasLineNumber = hasValue(config.lineNumber);
        const hasLineRange = Array.isArray(config.lineRange) && (config.lineRange as unknown[]).length === 2;
        const hasMatchPattern = hasValueOrTemplate(config.matchPattern);
        const selectorCount = (hasLineNumber ? 1 : 0) + (hasLineRange ? 1 : 0) + (hasMatchPattern ? 1 : 0);
        if (selectorCount === 0)
          return "Operation 'delete' benötigt genau eines von: lineNumber, lineRange, matchPattern.";
        if (selectorCount > 1)
          return "Operation 'delete' akzeptiert nur eines von: lineNumber, lineRange, matchPattern.";
      }

      if (op === 'replace') {
        if (!hasValueOrTemplate(config.matchPattern))
          return "Operation 'replace' benötigt 'matchPattern'.";
        // `replace: ""` is legal — empty replacement = delete the matches in-place.
        // We require the key to exist (hasOwnProperty), not the value to be non-empty.
        if (config.replace === undefined || config.replace === null)
          return "Operation 'replace' benötigt 'replace' (leerer String ist erlaubt = Löschen).";
      }

      return null;
    },
    summarize: (config) => {
      const op = (config.operation as string) || 'append';
      const path = (config.path as string) || i18n.t('activities:summaries.noPath');
      return i18n.t('activities:summaries.fileOpOther', { op, path });
    },
    smartDefaults: inheritExecutionContext,
  },
  [ACTIVITY_TYPES.EMAIL_NOTIFICATION]: {
    requiredConfig: (config) => {
      if (!hasValueOrTemplate(config.to)) return 'Empf\u00e4nger (to) ist erforderlich.';
      if (!hasValueOrTemplate(config.subject)) return 'Betreff (subject) ist erforderlich.';
      return null;
    },
    summarize: (config) => `To: ${(config.to as string) || i18n.t('activities:summaries.emailNoRecipient')}`,
    smartDefaults: ({ lastConfig }) => lastConfig.isHtml !== undefined
      ? { config: { isHtml: lastConfig.isHtml } }
      : {},
  },
  [ACTIVITY_TYPES.DELAY]: {
    requiredConfig: (config) => {
      const seconds = config.seconds;
      if (!hasValueOrTemplate(seconds)) return 'Verz\u00f6gerung (seconds) ist erforderlich.';
      if (typeof seconds === 'number' && seconds <= 0) return 'Verz\u00f6gerung muss > 0 sein.';
      return null;
    },
    summarize: (config) => i18n.t('activities:summaries.delayWait', { seconds: (config.seconds as number) || 5 }),
  },
  [ACTIVITY_TYPES.GENERATE_TEXT]: {
    // Pre-publish guard so an empty custom charset is caught in the UI rather than only at
    // runtime (backend TryBuildCharset). For every other mode the charset is implicit.
    requiredConfig: (config) =>
      config.mode === 'custom' && !hasValueOrTemplate(config.customCharset)
        ? 'Eigener Zeichensatz (customCharset) ist erforderlich.'
        : null,
    summarize: (config) => i18n.t('activities:summaries.generateText', {
      length: (config.length as number) || 16,
      mode: (config.mode as string) || 'alphanumeric',
    }),
  },
  [ACTIVITY_TYPES.LLM_QUERY]: {
    requiredConfig: (config) => {
      if (!hasValueOrTemplate(config.prompt)) return 'Prompt ist erforderlich.';
      // baseUrl is optional; when a literal is given it must be an absolute http/https URL.
      // Templates ({{globals.LLM_BASE_URL}}) are resolved by the StepRunner before the activity.
      const baseUrl = config.baseUrl;
      if (hasValue(baseUrl) && !isTemplate(baseUrl) && !/^https?:\/\//i.test(String(baseUrl)))
        return 'Endpunkt (baseUrl) muss eine absolute http/https-URL sein.';
      if (hasValue(config.temperature)) {
        const temp = Number(config.temperature);
        if (Number.isNaN(temp) || temp < 0 || temp > 2) return 'Temperature muss zwischen 0 und 2 liegen.';
      }
      if (hasValue(config.maxTokens)) {
        const mt = Number(config.maxTokens);
        if (Number.isNaN(mt) || mt <= 0) return 'maxTokens muss eine positive Zahl sein.';
      }
      return null;
    },
    summarize: (config) => {
      const model = (config.model as string) || i18n.t('activities:summaries.llmQueryDefaultModel');
      const prompt = (config.prompt as string) || '';
      const first = prompt.split('\n')[0].slice(0, 50);
      return prompt ? `${model} · ${first}${prompt.length > 50 ? '…' : ''}` : model;
    },
  },
  [ACTIVITY_TYPES.XML_QUERY]: {
    requiredConfig: (config) => {
      if (!hasValueOrTemplate(config.path) && !hasValueOrTemplate(config.content)) return 'Pfad oder Inhalt (path/content) ist erforderlich.';
      if (!hasValueOrTemplate(config.xpath)) return 'XPath-Ausdruck ist erforderlich.';
      return null;
    },
  },
  [ACTIVITY_TYPES.JSON_QUERY]: {
    requiredConfig: (config) => {
      if (!hasValueOrTemplate(config.path) && !hasValueOrTemplate(config.content)) return 'Pfad oder Inhalt (path/content) ist erforderlich.';
      if (!hasValueOrTemplate(config.jsonPath)) return 'JSONPath-Ausdruck ist erforderlich.';
      return null;
    },
  },
  [ACTIVITY_TYPES.MANUAL_TRIGGER]: {
    summarize: (config) => (config.title as string) || i18n.t('activities:summaries.manualStart'),
  },
  [ACTIVITY_TYPES.SCHEDULE_TRIGGER]: {
    summarize: (config) => (config.cronExpression as string) || i18n.t('activities:summaries.scheduleNotSet'),
  },
  [ACTIVITY_TYPES.WEBHOOK_TRIGGER]: {
    summarize: (config) => {
      const base = `${(config.method as string) || 'POST'} ${(config.path as string) || '/api/webhooks/...'}`;
      const hmac = (config.signatureMode as string) === 'nodepilot-hmac-v2' ? ' · HMAC v2' : '';
      return `${base}${hmac}`;
    },
  },
  [ACTIVITY_TYPES.FILE_WATCHER_TRIGGER]: {
    summarize: (config) => `${(config.watchType as string) || 'created'}: ${(config.directory as string) || i18n.t('activities:summaries.fileWatchNoDir')}`,
  },
  [ACTIVITY_TYPES.DATABASE_TRIGGER]: {
    summarize: (config) => {
      const query = (config.query as string) || '';
      return query ? query.slice(0, 60) : i18n.t('activities:summaries.dbTriggerNoQuery');
    },
  },
  [ACTIVITY_TYPES.EVENT_LOG_TRIGGER]: {
    summarize: (config) => {
      const logName = (config.logName as string) || 'Application';
      return config.eventId ? `${logName} Event ID: ${config.eventId}` : logName;
    },
  },
  [ACTIVITY_TYPES.JUNCTION]: {
    summarize: (config) => {
      const mode = (config.mode as string) || 'waitAll';
      if (mode === 'waitAny') return i18n.t('activities:summaries.junctionWaitAny');
      if (mode === 'waitNofM') return i18n.t('activities:summaries.junctionWaitN', { count: (config.requiredCount as number) || 2 });
      return i18n.t('activities:summaries.junctionWaitAll');
    },
  },
};

export function checkRequiredActivityConfig(activityType: string, config: ActivityConfig): string | null {
  return ACTIVITY_CONFIG_FACTS[activityType]?.requiredConfig?.(config) ?? null;
}

export function summarizeActivityConfig(activityType: string, config: ActivityConfig): string {
  return ACTIVITY_CONFIG_FACTS[activityType]?.summarize?.(config) ?? '';
}

export function getActivitySmartDefaults(
  activityType: string,
  lastData: ActivityConfig,
  lastConfig: ActivityConfig,
): SmartDefaults {
  return ACTIVITY_CONFIG_FACTS[activityType]?.smartDefaults?.({ lastData, lastConfig }) ?? {};
}

/** Snapshot of which activity types have facts-entries registered. Used by the
 *  registry-drift test to detect orphan entries (a key here that no longer exists in
 *  ACTIVITY_CATALOG) — typically a leftover after renaming or removing an activity. */
export function getRegisteredActivityFactTypes(): readonly string[] {
  return Object.keys(ACTIVITY_CONFIG_FACTS);
}
