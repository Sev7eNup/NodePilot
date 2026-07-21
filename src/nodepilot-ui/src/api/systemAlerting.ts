import { api } from './client';
import type { NotificationRouteDto, NotificationRuleTargetDto, TestFireResponse } from './alerting';

// ---- Catalog (server-owned; part of the system alert policies feature, ADR 0008) ----

export interface SystemAlertField {
  name: string;
  type: 'String' | 'Number' | 'Boolean' | 'Enum' | 'Duration';
  operators: string[];
  unit: string | null;
  enumValues: string[] | null;
}

export interface SystemAlertParameter {
  name: string;
  type: 'String' | 'Number' | 'Boolean' | 'Enum' | 'Duration';
  default: unknown;
  required: boolean;
  unit: string | null;
  min: number | null;
  max: number | null;
}

export interface SystemAlertPreset {
  presetId: string;
  severity: string;
  sustainForSeconds: number;
  conditionJson: string | null;
  parameters: Record<string, unknown> | null;
}

export interface SystemAlertSource {
  sourceId: string;
  category: string; // Execution | Queue | Health | Schedule | Credential
  scopeCapability: 'GlobalOnly' | 'WorkflowScoped';
  defaultSeverity: string;
  fields: SystemAlertField[];
  parameters: SystemAlertParameter[];
  presets: SystemAlertPreset[];
  available: boolean;
}

export interface SystemAlertCatalog {
  sources: SystemAlertSource[];
}

// ---- Policies ----

export interface SystemAlertPolicy {
  id: string;
  name: string;
  description: string | null;
  isEnabled: boolean;
  sourceId: string;
  presetId: string | null;
  sourceParameters: Record<string, unknown> | null;
  conditionJson: string | null;
  sustainForSeconds: number;
  severityOverride: string | null;
  scopeKind: string;
  targets: NotificationRuleTargetDto[];
  routes: NotificationRouteDto[];
  cooldownMinutes: number;
  minOccurrences: number;
  occurrenceWindowMinutes: number;
  createdAt: string;
  updatedAt: string;
  updatedBy: string | null;
  activatedAt: string | null;
}

export interface SaveSystemAlertPolicyRequest {
  name: string;
  description: string | null;
  isEnabled: boolean;
  sourceId: string;
  presetId: string | null;
  sourceParameters: Record<string, unknown> | null;
  conditionJson: string | null;
  sustainForSeconds: number;
  severityOverride: string | null;
  scopeKind: string;
  targets: NotificationRuleTargetDto[];
  routes: NotificationRouteDto[];
  cooldownMinutes: number;
  minOccurrences: number;
  occurrenceWindowMinutes: number;
}

export interface SystemAlertPreviewRequest {
  sourceId: string;
  sourceParameters: Record<string, unknown> | null;
  conditionJson: string | null;
}

export interface SystemAlertPreviewMatch {
  instanceKey: string;
  title: string | null;
  summary: string | null;
  fields: Record<string, string>;
  matched: boolean;
}

export interface SystemAlertPreviewResponse {
  available: boolean;
  matches: SystemAlertPreviewMatch[];
}

const base = '/alerting/system';

export const systemAlertingApi = {
  catalog: () => api.get<SystemAlertCatalog>(`${base}/catalog`),
  list: () => api.get<SystemAlertPolicy[]>(`${base}/policies`),
  get: (id: string) => api.get<SystemAlertPolicy>(`${base}/policies/${id}`),
  create: (body: SaveSystemAlertPolicyRequest) => api.post<SystemAlertPolicy>(`${base}/policies`, body),
  update: (id: string, body: SaveSystemAlertPolicyRequest) => api.put<void>(`${base}/policies/${id}`, body),
  delete: (id: string) => api.delete<void>(`${base}/policies/${id}`),
  enable: (id: string) => api.post<void>(`${base}/policies/${id}/enable`),
  disable: (id: string) => api.post<void>(`${base}/policies/${id}/disable`),
  preview: (body: SystemAlertPreviewRequest) => api.post<SystemAlertPreviewResponse>(`${base}/preview`, body),
  testFire: (id: string) => api.post<TestFireResponse>(`${base}/policies/${id}/test-fire`),
};
