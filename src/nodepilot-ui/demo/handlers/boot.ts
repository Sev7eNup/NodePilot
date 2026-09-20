/**
 * Everything the SPA asks for before it renders anything.
 *
 * Two of these are load-bearing. `/auth/me` answering with a user is what gets the demo past
 * the login screen — `authStore.initialize()` runs at bundle load, so no login happens at
 * all. And `/healthz/database` must answer 200 with a JSON content type, or the store parks
 * in the loading shell and the outage banner goes up app-wide.
 */
import type { AuthMethodsResponse, ObservabilityConfig } from '../../src/types/api';
import { route, type Route } from '../net/router';
import { json, noContent } from '../net/respond';
import { DEMO_HOST, DEMO_USER } from '../seed/entities';

const authMethods: AuthMethodsResponse = {
  local: true,
  ldap: false,
  windows: false,
  windowsEndpoint: null,
  oidc: false,
  oidcEndpoint: null,
  oidcDisplayName: null,
};

const observability: ObservabilityConfig = {
  enabled: false,
  traceUiUrlTemplate: null,
  traceBackendName: null,
  prometheusAvailable: false,
  browserOtlpEndpoint: null,
  serviceName: 'nodepilot-demo',
  environment: 'demo',
  grafanaBaseUrl: null,
};

/** AI is switched off in the demo: there is no model to call and a canned answer reads worse
 *  than no answer. `enabled: false` hides the AI entry points instead of breaking them. */
const knowledgeCapabilities = {
  enabled: false,
  llm: false,
  docs: false,
  operational: false,
  sourceCode: false,
  db: false,
};

export const bootRoutes: Route[] = [
  route('GET', '/healthz/database', () => json({ status: 'ok', sinceUtc: null, reason: null })),
  route('GET', '/healthz/ready', () => json({ status: 'ok' })),
  route('GET', '/healthz/live', () => json({ status: 'ok' })),

  route('GET', '/auth/me', () => json(DEMO_USER)),
  route('GET', '/auth/methods', () => json(authMethods)),
  route('POST', '/auth/login', () => json({ userId: DEMO_USER.id, username: DEMO_USER.username, role: DEMO_USER.role })),
  route('POST', '/auth/refresh', () => json({ userId: DEMO_USER.id, username: DEMO_USER.username, role: DEMO_USER.role })),
  route('POST', '/auth/logout', () => noContent()),

  route('GET', '/system/host-info', () => json(DEMO_HOST)),
  route('GET', '/observability/config', () => json(observability)),
  route('GET', '/observability/summary', () => json({ panels: [], tracesAvailable: false, metricsAvailable: false })),

  route('GET', '/ai/knowledge/capabilities', () => json(knowledgeCapabilities)),
];
