import { api } from './client';

/** Matches NotificationRuleStore.UnchangedSecret on the backend — a route echoing this keeps its stored secret. */
export const UNCHANGED_SECRET = '__unchanged__';

export interface NotificationRouteDto {
  id: string | null;
  channel: string;
  target: string;
  /** On read: UNCHANGED_SECRET when a secret is stored (never the cipher), else null. On write: echo to keep, new value to replace, null/'' to clear. */
  secret: string | null;
  order: number;
  conditionExpressionJson?: string | null;
}

export interface NotificationRuleTargetDto {
  targetKind: string; // 'Folder' | 'Workflow'
  targetId: string;
}

export interface NotificationRule {
  id: string;
  name: string;
  description: string | null;
  isEnabled: boolean;
  eventTypes: string[];
  filterExpressionJson: string | null;
  scopeKind: string; // 'Global' | 'Folders' | 'Workflows'
  cooldownMinutes: number;
  minOccurrences: number;
  occurrenceWindowMinutes: number;
  routes: NotificationRouteDto[];
  targets: NotificationRuleTargetDto[];
  createdAt: string;
  updatedAt: string;
  updatedBy: string | null;
  dedupKeyTemplate?: string | null;
}

export interface SaveNotificationRuleRequest {
  name: string;
  description: string | null;
  isEnabled: boolean;
  eventTypes: string[];
  filterExpressionJson: string | null;
  scopeKind: string;
  cooldownMinutes: number;
  minOccurrences: number;
  occurrenceWindowMinutes: number;
  routes: NotificationRouteDto[];
  targets: NotificationRuleTargetDto[];
  dedupKeyTemplate: string | null;
}

export interface TestFireRouteResult {
  channel: string;
  target: string;
  success: boolean;
  error: string | null;
}

export interface TestFireResponse {
  allSucceeded: boolean;
  results: TestFireRouteResult[];
}

export interface PreviewFilterRequest {
  filterExpressionJson: string | null;
  eventFields: Record<string, string>;
}

export interface PreviewFilterResponse {
  matches: boolean;
  error: string | null;
}

export interface AlertingCatalogEventType {
  name: string;
  category: 'execution' | 'gauge';
  scopeable: boolean;
}

export interface AlertingCatalogField {
  name: string;
  applies: 'execution' | 'gauge' | 'both';
  type: 'string' | 'number' | 'boolean' | 'enum';
  values?: string[] | null;
}

export interface AlertingCatalog {
  eventTypes: AlertingCatalogEventType[];
  eventFields: AlertingCatalogField[];
  channels: string[];
  dedupTemplateFields: string[];
}

export interface PreviewRuleRequest {
  eventTypes: string[];
  filterExpressionJson: string | null;
  scopeKind: string;
  routes: NotificationRouteDto[];
  targets: NotificationRuleTargetDto[];
  dedupKeyTemplate: string | null;
  eventFields: Record<string, string>;
}

export interface PreviewRouteResult {
  channel: string;
  target: string;
  matches: boolean;
}

export interface PreviewRuleResponse {
  matchesRule: boolean;
  dedupKey: string | null;
  routes: PreviewRouteResult[];
  reasons: string[];
}

export interface NotificationDelivery {
  id: string;
  ruleId: string;
  ruleName: string | null;
  routeId: string;
  channel: string | null;
  target: string | null;
  eventKey: string;
  status: string; // 'Pending' | 'Sent' | 'Failed'
  attempt: number;
  createdAt: string;
  sentAt: string | null;
  error: string | null;
  isTest: boolean;
  summary: string | null;
}

const base = '/alerting';

export const alertingApi = {
  list: () => api.get<NotificationRule[]>(`${base}/rules`),
  catalog: () => api.get<AlertingCatalog>(`${base}/catalog`),
  get: (id: string) => api.get<NotificationRule>(`${base}/rules/${id}`),
  create: (body: SaveNotificationRuleRequest) => api.post<NotificationRule>(`${base}/rules`, body),
  update: (id: string, body: SaveNotificationRuleRequest) => api.put<void>(`${base}/rules/${id}`, body),
  delete: (id: string) => api.delete<void>(`${base}/rules/${id}`),
  enable: (id: string) => api.post<void>(`${base}/rules/${id}/enable`),
  disable: (id: string) => api.post<void>(`${base}/rules/${id}/disable`),
  testFire: (id: string) => api.post<TestFireResponse>(`${base}/rules/${id}/test-fire`),
  previewFilter: (body: PreviewFilterRequest) => api.post<PreviewFilterResponse>(`${base}/preview-filter`, body),
  previewRule: (body: PreviewRuleRequest) => api.post<PreviewRuleResponse>(`${base}/preview-rule`, body),
  deliveries: (params?: { ruleId?: string; status?: string; limit?: number }) => {
    const q = new URLSearchParams();
    if (params?.ruleId) q.set('ruleId', params.ruleId);
    if (params?.status) q.set('status', params.status);
    if (params?.limit) q.set('limit', String(params.limit));
    const qs = q.toString();
    return api.get<NotificationDelivery[]>(`${base}/deliveries${qs ? `?${qs}` : ''}`);
  },
};
