/**
 * Admin settings.
 *
 * The section payloads are the product's own shipped defaults — the same literals the section
 * components carry as their fallback — so the page shows a real default configuration rather
 * than invented values. Writing is refused: there is no server to write to.
 *
 * Serving these correctly is not optional. `useSectionForm` overwrites its fallback with
 * `data.payload` the moment a response arrives, and several cards read nested fields
 * (`payload.proxy.password`), so a partial envelope crashes the page rather than degrading it.
 */
import { route, type Route } from '../net/router';
import { json, notFound, notInDemo } from '../net/respond';

const MB = 1024 * 1024;

/** Section name → payload, mirroring each card's own fallback. */
const SECTIONS: Record<string, unknown> = {
  Authentication: {
    ldap: {
      enabled: false, server: null, endpoints: [], port: 636, useSsl: true, baseDn: null,
      upnSuffix: null, bindTimeoutSeconds: 10, serviceBindDn: null, servicePassword: null,
      allowedGroupSids: [], directorySyncIntervalMinutes: 60, directorySyncMaxConcurrency: 4,
      globalRoleMappings: [], jitUserDefaultRootRole: null,
    },
    windows: { enabled: false, allowNtlmFallback: false, ntlmDisabledByPolicy: false },
    oidc: {
      enabled: false, authority: null, clientId: null, clientSecret: null, displayName: 'SSO',
      nameClaimType: 'preferred_username', groupsClaimType: 'groups', scopes: ['openid', 'profile', 'email'],
      allowedGroupIds: [], globalRoleMappings: [],
    },
    scim: { enabled: false, bearerToken: null, previousBearerToken: null, authority: null },
    localLoginMode: 'BreakGlassOnly',
    sessionAbsoluteLifetimeHours: 8,
    maxAuthorizationStalenessMinutes: 15,
  },
  Smtp: { host: 'smtp.contoso.example', port: 587, username: null, password: null, from: 'nodepilot@contoso.example', enableSsl: true },
  Llm: {
    enabled: false,
    activeProfileId: '',
    profiles: [],
    proxy: { mode: 'Off', address: '', bypassList: [], username: null, password: null, useDefaultCredentials: false },
  },
  Retention: {
    executions: { enabled: true, maxAgeDays: 30, intervalMinutes: 60, batchSize: 500, archivePath: null },
    auditLog: { enabled: true, maxAgeDays: 365, intervalMinutes: 60, batchSize: 500, archivePath: null },
    workflowVersions: { enabled: true, maxVersionsPerWorkflow: 50, intervalMinutes: 60, batchSize: 500 },
  },
  AiKnowledge: {
    enabled: false, docsEnabled: true, operationalEnabled: true, sourceCodeEnabled: false, dbEnabled: false,
    docsRootPath: null, sourceCodeRootPath: null, docsMaxFileBytes: 262_144, docsMaxResults: 20,
    sourceCodeMaxFileBytes: 262_144, sourceCodeMaxResults: 20,
  },
  DbAdmin: { allowWriteQueries: false, queryTimeoutSeconds: 30, queryMaxRows: 10_000 },
  Logging: {
    format: 'text',
    logLevel: { default: 'Warning', aspNetCore: 'Warning', efCoreCommand: 'Warning', efCoreConnection: 'Warning', efCoreInfrastructure: 'Warning' },
    stepDetail: { enabled: false, maxOutputChars: 10_000 },
    file: { retainedFileCountLimit: 7, fileSizeLimitBytes: 100 * MB, async: true },
    redaction: { enabled: true },
    supportLog: { enabled: true, path: '', retainedFileCountLimit: 90, fileSizeLimitBytes: 10 * MB, dbProjectionEnabled: true },
  },
  OpenTelemetry: {
    enabled: false, serviceName: 'nodepilot-api', environment: 'demo', redactHostnames: true,
    metricExportIntervalSeconds: 30,
    otlp: { endpoint: 'http://localhost:4317', protocol: 'grpc', headers: '', browserEndpoint: '' },
    sampling: { mode: 'ParentBasedTraceIdRatio', ratio: 1.0 },
    exporters: { traces: true, metrics: true, logs: true, prometheusScrape: false, prometheusScrapeAllowAnonymous: false },
    traceUi: { urlTemplate: '', backendName: 'Tempo' },
    prometheus: { queryEndpoint: '', username: '', password: null, bearerToken: null, timeoutSeconds: 10 },
    grafanaBaseUrl: '',
  },
  Stats: { refreshIntervalMinutes: 5, windowDays: 7 },
  Performance: { manualTuning: false },
  Engine: {
    debug: { maxPauseMinutes: 10 },
    maxConcurrentExecutions: { global: 5000, perUser: 2000 },
    maxConcurrentSteps: 600,
    runspace: { minRunspaces: 256, maxRunspaces: 768 },
  },
  ExecutionDispatch: { workerCount: 600 },
  Threading: { minWorkerThreads: 768, minIoCompletionThreads: 768 },
  Remote: {
    requireWinRmSsl: true,
    winRm: { operationTimeoutSeconds: 300, openTimeoutSeconds: 30 },
    pool: { enabled: true, maxConcurrentPerMachine: 5, maxIdlePerKey: 5, idleTtlSeconds: 120 },
  },
  RestApi: {
    blockPrivateNetworks: true,
    allowedHosts: [],
    proxy: { enabled: false, address: '', bypassList: [], username: null, password: null },
  },
  WaitForCondition: { allowedHosts: ['localhost'] },
  FileSystemOperation: { rejectTraversal: true, allowedRoots: [] },
  SqlActivity: { requireConnectionRef: false },
  StartProgram: { disallowShellExecute: true },
  Webhook: { requireSecret: true },
  ExternalTrigger: { apiKey: null, allowedWorkflowIds: [] },
  Security: { strictAllowedHosts: false, allowedHosts: '*' },
};

/** Sections the product reloads without a restart; the rest show the restart hint. */
const HOT_RELOADABLE = new Set([
  'Smtp', 'Llm', 'Retention', 'AiKnowledge', 'DbAdmin', 'Logging', 'Stats', 'RestApi',
  'WaitForCondition', 'FileSystemOperation', 'SqlActivity', 'StartProgram', 'Webhook',
]);

export const settingsRoutes: Route[] = [
  route('GET', '/admin/settings/status', () =>
    json({
      overridesPath: 'appsettings.runtime.json',
      restartRequired: false,
      restartRequiredSince: null,
      restartRequiredFor: [],
      lastSavedAt: null,
      lastSavedBy: null,
    })),

  route('GET', '/admin/settings/system-info', () =>
    json({
      appVersion: __NP_DEMO_VERSION__,
      framework: '.NET 10',
      os: 'Windows Server 2022',
      databaseProvider: 'PostgreSQL',
      clusterRole: null,
      deploymentMode: 'Server',
    })),

  route('GET', '/admin/settings/effective-sizing', () =>
    json({
      manualTuning: false,
      desiredManualTuning: false,
      processorCount: 8,
      usableMemoryBytes: 32 * 1024 * MB,
      isDesktop: false,
      values: [
        { key: 'Engine:MaxConcurrentSteps', value: 600, bound: 'Cpu' },
        { key: 'Engine:Runspace:MaxRunspaces', value: 768, bound: 'Ram' },
        { key: 'ExecutionDispatch:WorkerCount', value: 600, bound: 'Cpu' },
      ],
    })),

  route('POST', '/admin/settings/test/:kind', () => notInDemo('Testing an outbound connection')),
  route('PUT', '/admin/settings/:section', () => notInDemo('Changing server settings')),

  route('GET', '/admin/settings/:section', (ctx) => {
    const payload = SECTIONS[ctx.params.section];
    if (payload === undefined) return notFound(`Settings section '${ctx.params.section}'`);
    return json({
      sectionPath: ctx.params.section,
      payload,
      etag: `demo-${ctx.params.section}`,
      isHotReloadable: HOT_RELOADABLE.has(ctx.params.section),
      // Nothing is pinned by an environment variable here, but the key has to exist: the cards
      // index into it without an optional chain.
      effectiveSource: {},
    });
  }),
];

/** Section names the demo serves, for tests. */
export const demoSettingsSections = Object.keys(SECTIONS);
