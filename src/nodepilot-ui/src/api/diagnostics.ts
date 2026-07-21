import { api, downloadFromApi } from './client';

export type SupportLogTailResponse = {
  file: string | null;
  lineCount: number;
  lines: string[];
};

export type SupportEventResponse = {
  id: string;
  timestamp: string;
  level: number;
  eventType: string;
  message: string;
  workflowId: string | null;
  workflowName: string | null;
  executionId: string | null;
  executionShort: string | null;
  stepId: string | null;
  stepLabel: string | null;
  activityType: string | null;
  userName: string | null;
  userId: string | null;
  traceId: string | null;
  spanId: string | null;
  propertiesJson: string | null;
};

export type SupportEventCursor = { afterTs: string; afterId: string };

export type SupportEventPageResponse = {
  items: SupportEventResponse[];
  nextCursor: SupportEventCursor | null;
  hasMore: boolean;
};

export type SupportEventQuery = {
  since?: string;
  until?: string;
  level?: number;
  eventType?: string;
  workflowId?: string;
  workflowName?: string;
  executionId?: string;
  stepId?: string;
  activityType?: string;
  username?: string;
  q?: string;
  sortBy?: string;
  sortDir?: 'asc' | 'desc';
  afterTs?: string;
  afterId?: string;
  take?: number;
};

function buildQuery(filter: SupportEventQuery): string {
  const params = new URLSearchParams();
  for (const [k, v] of Object.entries(filter)) {
    if (v !== undefined && v !== null && v !== '') params.set(k, String(v));
  }
  const qs = params.toString();
  return qs ? `?${qs}` : '';
}

export const diagnostics = {
  tailSupportLog: (lines: number) =>
    api.get<SupportLogTailResponse>(`/diagnostics/support-log?lines=${lines}`),
  downloadSupportLog: (date: string) =>
    downloadFromApi(`/diagnostics/support-log/download?date=${encodeURIComponent(date)}`,
      `nodepilot-support-${date}.log`),

  queryEvents: (filter: SupportEventQuery) =>
    api.get<SupportEventPageResponse>(`/diagnostics/support-events${buildQuery(filter)}`),
  exportEvents: (format: 'csv' | 'ndjson', filter: SupportEventQuery) => {
    const qs = buildQuery({ ...filter, take: undefined, afterTs: undefined, afterId: undefined });
    const sep = qs ? '&' : '?';
    const ts = new Date().toISOString().slice(0, 19).replaceAll(/[-:T]/g, '');
    return downloadFromApi(
      `/diagnostics/support-events/export${qs}${sep}format=${format}`,
      `nodepilot-support-events-${ts}.${format}`);
  },
};
