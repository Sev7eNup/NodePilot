import { Activity, CheckmarkFilled, CircleDash, ErrorFilled, Time } from '@carbon/icons-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { LiveExecution } from '../../../hooks/useSignalR';

interface Props {
  execution: LiveExecution;
  /** Optional median run duration from history, used for ETA. */
  medianRunDurationMs?: number;
}

export function StatsStrip({ execution, medianRunDurationMs }: Readonly<Props>) {
  const { t } = useTranslation('designer');
  const isRunning = execution.status === 'Running' || execution.status === 'Pending';
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!isRunning) return;
    const id = setInterval(() => setNow(Date.now()), 500);
    return () => clearInterval(id);
  }, [isRunning]);

  const startMs = new Date(execution.startedAt).getTime();
  const endMs = execution.completedAt ? new Date(execution.completedAt).getTime() : now;
  const elapsedMs = Math.max(0, endMs - startMs);

  // Skipped steps are control-flow decisions (false edge condition, disabled node) —
  // not executed work. The Live view only shows steps that actually ran or are running.
  const executedSteps = execution.steps.filter((s) => s.status !== 'Skipped');
  const total = executedSteps.length;
  const succeeded = executedSteps.filter((s) => s.status === 'Succeeded').length;
  const failed = executedSteps.filter((s) => s.status === 'Failed').length;
  const running = executedSteps.filter((s) => s.status === 'Running').length;
  const done = succeeded + failed;

  const eta = (() => {
    if (!isRunning || !medianRunDurationMs) return null;
    const remain = medianRunDurationMs - elapsedMs;
    return remain > 0 ? remain : null;
  })();

  return (
    <div className="flex items-center gap-3 px-3 py-1.5 bg-surface-low/40 border-b border-outline-variant/10 shrink-0 flex-wrap">
      <Stat icon={<Activity size={11} />} label={t('live.stats.steps')} value={`${done}/${total}`} tone="default" />
      {succeeded > 0 && (
        <Stat icon={<CheckmarkFilled size={11} />} value={String(succeeded)} tone="success" title={t('live.stats.succeeded', { count: succeeded })} />
      )}
      {failed > 0 && (
        <Stat icon={<ErrorFilled size={11} />} value={String(failed)} tone="error" title={t('live.stats.failed', { count: failed })} />
      )}
      {running > 0 && (
        <Stat icon={<CircleDash size={11} className="animate-spin" />} value={String(running)} tone="info" title={t('live.stats.running', { count: running })} />
      )}
      <div className="ml-auto flex items-center gap-3">
        <Stat icon={<Time size={11} />} label={t('live.stats.elapsed')} value={formatMs(elapsedMs)} tone="default" mono />
        {eta != null && (
          <Stat label={t('live.stats.eta')} value={`~${formatMs(eta)}`} tone="muted" mono title={t('live.stats.etaTooltip')} />
        )}
      </div>
    </div>
  );
}

function Stat({ icon, label, value, tone, mono, title }: Readonly<{
  icon?: React.ReactNode;
  label?: string;
  value: string;
  tone: 'default' | 'success' | 'error' | 'muted' | 'info';
  mono?: boolean;
  title?: string;
}>) {
  const toneClass =
    tone === 'success' ? 'text-green-700' :
    tone === 'error' ? 'text-error' :
    tone === 'info' ? 'text-primary' :
    tone === 'muted' ? 'text-on-surface-variant' :
    'text-on-surface';
  return (
    <div className={`flex items-center gap-1.5 text-[11px] font-label ${toneClass}`} title={title}>
      {icon}
      {label && <span className="text-on-surface-variant">{label}</span>}
      <span className={`font-semibold ${mono ? 'font-mono tabular-nums' : ''}`}>{value}</span>
    </div>
  );
}

function formatMs(ms: number): string {
  if (ms < 0) ms = 0;
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 2 : 1)}s`;
  const m = Math.floor(ms / 60_000);
  const s = Math.round((ms % 60_000) / 1000);
  return `${m}m ${s}s`;
}
