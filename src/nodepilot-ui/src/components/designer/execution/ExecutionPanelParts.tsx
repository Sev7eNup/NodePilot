import {
  Activity,
  ArrowDownLeft,
  CheckmarkFilled,
  CircleDash,
  CircleStroke,
  DataBase,
  ErrorFilled,
  SettingsAdjust,
  WarningAltFilled,
} from '@carbon/icons-react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../../../api/client';
import { parseOutputParametersJson } from '../../../lib/outputParameters';
import { TRIGGER_BADGE_META } from '../../../lib/triggerBadgeMeta';
import type { WorkflowExecution, Workflow, ManagedMachine, Credential } from '../../../types/api';
import { activityConfig } from '../nodes/activityConfig';
import { ACTIVITY_ICON_COMPONENTS, FALLBACK_ACTIVITY_ICON } from '../../../lib/activityIcons';
import { STATUS_BADGE_CLASS } from '../../../lib/statusTokens';

export function ActivityTypeIcon({ type }: Readonly<{ type: string }>) {
  const key = type.length > 0 ? type[0].toLowerCase() + type.slice(1) : type;
  const cfg = activityConfig[key] ?? activityConfig.runScript;
  const Icon = ACTIVITY_ICON_COMPONENTS[cfg.icon] ?? FALLBACK_ACTIVITY_ICON;
  return <Icon size={16} className="shrink-0" style={{ color: cfg.color }} aria-hidden="true" />;
}

/** HH:MM:SS.mmm in 24-hour format. Milliseconds are visibly necessary because many steps
 *  take <1s — without .mmm they'd all look identical. */
export function formatClock(ms: number): string {
  const d = new Date(ms);
  const hh = String(d.getHours()).padStart(2, '0');
  const mm = String(d.getMinutes()).padStart(2, '0');
  const ss = String(d.getSeconds()).padStart(2, '0');
  const mmm = String(d.getMilliseconds()).padStart(3, '0');
  return `${hh}:${mm}:${ss}.${mmm}`;
}

/** Inline bar giving a visual comparison of step durations against each other. Scaled to
 *  the longest step in the table (not an absolute scale), otherwise a 5ms step next to a
 *  10s step would be invisible — and the reverse would lose all visual contrast.
 *  Minimum width 2px, so even nominally 0ms steps leave a visible trace. */
export function DurationBar({ durationMs, maxMs, status }: Readonly<{ durationMs: number | null; maxMs: number; status: string }>) {
  if (durationMs == null) return <span className="text-outline text-[10px]">—</span>;
  const pct = Math.max(2, Math.min(100, (durationMs / maxMs) * 100));
  const color =
    status === 'Succeeded' ? 'bg-success/60' :
    status === 'Failed'    ? 'bg-error/60' :
    status === 'Running'   ? 'bg-running/60' :
    'bg-outline-variant/50';
  return (
    <div className="relative h-3 bg-surface-low rounded-sm overflow-hidden">
      <div className={`absolute left-0 top-0 bottom-0 ${color}`} style={{ width: `${pct}%` }} />
    </div>
  );
}

/** Human-friendly ms format. <1s → ms, <60s → s with 1 decimal, otherwise m+s. */
export function formatMs(ms: number): string {
  if (ms < 0) ms = 0;
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 2 : 1)}s`;
  const m = Math.floor(ms / 60_000);
  const s = Math.round((ms % 60_000) / 1000);
  return `${m}m ${s}s`;
}

/* ---- Shared components ---- */

/**
 * Static input block: reads the workflow definition, finds the node by stepId, and
 * renders its compiled config, target machine, credential override + output variable.
 * Deliberately read from the DEFINITION — not the execution row — so the execution record
 * stays lean. Templates like `{{step.param.x}}` are shown as-is; during a debug run the
 * PausedVariablesInspector shows the resolved values.
 */
export function StepInputBlock({ workflowId, stepId }: Readonly<{ workflowId: string; stepId: string }>) {
  const { t } = useTranslation('designer');
  const { data: workflow } = useQuery({
    queryKey: ['workflow', workflowId],
    queryFn: () => api.get<Workflow>(`/workflows/${workflowId}`),
    staleTime: 60_000,
  });
  const { data: machines } = useQuery({
    queryKey: ['machines'],
    queryFn: () => api.get<ManagedMachine[]>('/machines'),
    staleTime: 60_000,
  });
  const { data: credentials } = useQuery({
    queryKey: ['credentials'],
    queryFn: () => api.get<Credential[]>('/credentials'),
    staleTime: 60_000,
  });

  const node = (() => {
    if (!workflow?.definitionJson) return null;
    try {
      const def = JSON.parse(workflow.definitionJson);
      const nodes: Array<{ id: string; data?: Record<string, unknown> }> = def?.nodes ?? [];
      return nodes.find((n) => n.id === stepId) ?? null;
    } catch {
      return null;
    }
  })();

  if (!node?.data) return null;

  const data = node.data as {
    config?: unknown;
    targetMachineId?: string;
    credentialId?: string | null;
    outputVariable?: string;
  };
  const machineName = data.targetMachineId
    ? machines?.find((m) => m.id === data.targetMachineId)?.name ?? data.targetMachineId
    : null;
  const credentialName = data.credentialId
    ? credentials?.find((c) => c.id === data.credentialId)?.name ?? data.credentialId
    : null;
  const configIsObject = data.config && typeof data.config === 'object';
  const configJson = configIsObject ? JSON.stringify(data.config, null, 2) : null;
  const configEmpty = !configIsObject || Object.keys(data.config as object).length === 0;

  if (configEmpty && !machineName && !credentialName && !data.outputVariable) return null;

  return (
    <div>
      <div className="flex items-center gap-1.5 mb-1">
        <SettingsAdjust size={11} className="text-indigo-600" />
        <span className="font-label text-[10px] font-semibold text-on-surface-variant uppercase tracking-wide">
          {t('execution.inspector.input')}
        </span>
      </div>
      <div className="rounded-md border border-outline-variant/10 bg-surface-high/60 divide-y divide-outline-variant/10">
        {(machineName || credentialName || data.outputVariable) && (
          <div className="px-3 py-2 grid grid-cols-[110px_1fr] gap-x-3 gap-y-1 text-[11px]">
            {machineName && (
              <>
                <span className="font-label text-on-surface-variant">{t('execution.inspector.targetMachine')}</span>
                <span className="font-mono text-on-surface truncate" title={data.targetMachineId}>{machineName}</span>
              </>
            )}
            {credentialName && (
              <>
                <span className="font-label text-on-surface-variant">{t('execution.inspector.credential')}</span>
                <span className="font-mono text-on-surface truncate" title={data.credentialId ?? ''}>{credentialName}</span>
              </>
            )}
            {data.outputVariable && (
              <>
                <span className="font-label text-on-surface-variant">{t('execution.inspector.outputVar')}</span>
                <span className="font-mono text-on-surface truncate">{data.outputVariable}</span>
              </>
            )}
          </div>
        )}
        {!configEmpty && configJson && (
          <pre className="text-[11px] font-mono p-3 whitespace-pre-wrap leading-relaxed text-on-surface max-h-64 overflow-y-auto">
            {configJson}
          </pre>
        )}
      </div>
    </div>
  );
}

export function StepOutputParametersBlock({ outputParametersJson, parameters }: Readonly<{
  outputParametersJson?: string | null;
  parameters?: Record<string, string> | null;
}>) {
  const { t } = useTranslation('designer');
  const resolvedParameters = parameters ?? parseOutputParametersJson(outputParametersJson);
  const json = resolvedParameters ? JSON.stringify(resolvedParameters, null, 2) : null;

  if (!json) return null;

  return (
    <div>
      <div className="flex items-center gap-1.5 mb-1">
        <DataBase size={11} className="text-indigo-600" />
        <span className="font-label text-[10px] font-semibold text-on-surface-variant uppercase tracking-wide">
          {t('execution.timeline.outputParameters')}
        </span>
      </div>
      <pre className="text-[11px] font-mono rounded-md border border-outline-variant/10 bg-surface-high p-3 whitespace-pre-wrap leading-relaxed text-on-surface">
        {json}
      </pre>
    </div>
  );
}

export function OutputBlock({ label, variant, children }: Readonly<{ label: string; variant: 'default' | 'error'; children: string }>) {
  const styles = variant === 'error'
    ? 'bg-error-container/10 border-error/10 text-error'
    : 'bg-surface-high border-outline-variant/10 text-on-surface';

  return (
    <div>
      <div className="flex items-center gap-1.5 mb-1">
        {variant === 'error' ? <WarningAltFilled size={11} className="text-error" /> : null}
        <span className="font-label text-[10px] font-semibold text-on-surface-variant uppercase tracking-wide">{label}</span>
      </div>
      {/* No inner max-height cap: the block grows to fit its content so short outputs
          don't leave half the panel empty. Long outputs are absorbed by the surrounding
          `flex-1 overflow-y-auto` detail-view — one scrollbar for the whole detail,
          instead of one per block. */}
      <pre className={`text-[11px] font-mono rounded-md border p-3 whitespace-pre-wrap leading-relaxed ${styles}`}>
        {children}
      </pre>
    </div>
  );
}

export function StepStatusIcon({ status, size = 14 }: Readonly<{ status: string; size?: number }>) {
  switch (status) {
    case 'Running': return <CircleDash size={size} className="text-running animate-spin" />;
    case 'Succeeded': return <CheckmarkFilled size={size} className="text-success" />;
    case 'Failed': return <ErrorFilled size={size} className="text-error" />;
    case 'Skipped': return <CircleStroke size={size} className="text-outline" strokeDasharray="2 2" />;
    default: return <CircleStroke size={size} className="text-outline-variant" />;
  }
}

export function ExecutionStatusBadge({ status }: Readonly<{ status: string }>) {
  const { t } = useTranslation('designer');
  // Tonale Status-Chips aus den semantischen Tokens (statusTokens.ts) statt
  // handverdrahteter light/dark-Palettenpaare je Status.
  const styles: Record<string, string> = {
    Succeeded: `${STATUS_BADGE_CLASS.success} border-success/30`,
    Failed: `${STATUS_BADGE_CLASS.failed} border-error/30`,
    Running: `${STATUS_BADGE_CLASS.running} border-running/30`,
    Paused: `${STATUS_BADGE_CLASS.paused} border-paused/30`,
    Pending: 'bg-surface-high text-on-surface-variant border-outline-variant/30',
    Cancelled: `${STATUS_BADGE_CLASS.cancelled} border-outline-variant/40`,
    Skipped: 'bg-surface-container text-on-surface-variant border-outline-variant',
  };

  return (
    <span className={`inline-flex shrink-0 items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-label font-semibold border whitespace-nowrap ${styles[status] ?? styles.Pending}`}>
      <StepStatusIcon status={status} size={10} />
      {t(`execution.status.${status}`, { defaultValue: status })}
    </span>
  );
}

export function timeDiff(start: string, end: string): string {
  const ms = new Date(end).getTime() - new Date(start).getTime();
  if (ms < 1000) return `${ms}ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
  return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`;
}

export function firstLine(s: string): string {
  const trimmed = s.trim();
  const nl = trimmed.indexOf('\n');
  return nl < 0 ? trimmed : trimmed.slice(0, nl);
}

/**
 * Human-friendly label for the `triggeredBy` column. The raw API value can be anything
 * from `manual` (UI click) to `scheduleTrigger` / `webhookTrigger` / `api` (external key)
 * / `retry:<guid>` (re-run) / `startWorkflow:<stepId>` (sub-workflow parent). We colour +
 * rename them so the history column isn't a wall of camel-case.
 */
export function TriggerCell({ triggeredBy }: Readonly<{ triggeredBy: string | null }>) {
  const { t } = useTranslation('designer');
  if (!triggeredBy) return <span className="text-outline">—</span>;

  const raw = triggeredBy;
  const meta = (() => {
    if (raw === 'manual') return { label: t('parts.trigger.manual'), cls: 'bg-surface-container text-on-surface' };
    if (raw === 'api')    return { label: t('parts.trigger.api'),    cls: 'bg-sky-100 dark:bg-sky-900/40 text-sky-700 dark:text-sky-400' };
    if (raw.startsWith('retry:'))         return { label: t('parts.trigger.retry'),    cls: 'bg-indigo-100 dark:bg-indigo-900/40 text-indigo-700 dark:text-indigo-400' };
    if (raw.startsWith('startWorkflow:')) return { label: t('parts.trigger.subWf'),   cls: 'bg-violet-100 dark:bg-violet-900/40 text-violet-700 dark:text-violet-400' };
    if (TRIGGER_BADGE_META[raw]) {
      return { label: TRIGGER_BADGE_META[raw].label, cls: TRIGGER_BADGE_META[raw].className };
    }
    if (raw === 'manualTrigger')     return { label: t('parts.trigger.manual'),    cls: 'bg-surface-container text-on-surface' };
    return { label: raw, cls: 'bg-surface-high text-on-surface-variant' };
  })();
  return (
    <span className={`inline-flex shrink-0 items-center px-1.5 py-0.5 rounded text-[10px] font-semibold whitespace-nowrap ${meta.cls}`}
          title={raw}>
      {meta.label}
    </span>
  );
}

/**
 * Tiny icon row that tells-at-a-glance whether an execution carries params, returned data,
 * or is linked to a distributed trace. Each icon hovers a tooltip with either the content
 * or a short explanation so the user doesn't have to expand the row to peek.
 */
export function ExtrasCell({ execution }: Readonly<{ execution: WorkflowExecution }>) {
  const { t } = useTranslation('designer');
  const hasParams = !!execution.inputParametersJson;
  const hasReturn = !!execution.returnData;
  const hasTrace  = !!execution.traceId;
  if (!hasParams && !hasReturn && !hasTrace) return <span className="text-outline">—</span>;

  return (
    <div className="flex items-center gap-2">
      {hasParams && (
        <span title={t('parts.extras.inputParameters', { value: execution.inputParametersJson })} className="text-indigo-500">
          <SettingsAdjust size={13} />
        </span>
      )}
      {hasReturn && (
        <span title={t('parts.extras.returnData', { value: execution.returnData })} className="text-emerald-600">
          <ArrowDownLeft size={13} />
        </span>
      )}
      {hasTrace && (
        <span title={t('parts.extras.traceId', { value: execution.traceId })} className="text-amber-600">
          <Activity size={13} />
        </span>
      )}
    </div>
  );
}

