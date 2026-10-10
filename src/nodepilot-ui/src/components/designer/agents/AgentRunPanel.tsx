import { ChevronRight, Maximize } from '@carbon/icons-react';
import { createContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { useAgentEvents, useAgentRuns } from '../../../hooks/useAgentRuns';
import type { AgentRun, AgentRunEvent } from '../../../types/agents';
import {
  agentRunTrace, groupTrace, runMembers, traceDuration, traceMatches, traceOutcome, type TraceEntry, type TraceEvent,
} from '../../../lib/agentRunTrace';
import { downloadTextFile } from '../../../lib/chatExport';
import { AgentResultSummary } from './AgentResultSummary';
import { Initial } from '../properties/activities/AgentPanelParts';
import { formatTime } from '../../../lib/format';

export const AgentCanvasExecutionContext = createContext<{ executionId: string | null; active: boolean; scrubTimeMs: number | null }>({
  executionId: null, active: false, scrubTimeMs: null,
});

export function AgentRunPanel({ executionId, stepId, active = false, children }: { executionId: string; stepId: string; active?: boolean; children?: ReactNode }) {
  const { t } = useTranslation('agents');
  const { data: runs = [], error } = useAgentRuns(executionId, active);
  const matching = runs.filter(run => run.stepId === stepId);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const expanded = matching.find(run => run.id === expandedId);
  return <section className="space-y-3 text-xs" aria-label={t('history')}>
    {error && <p role="alert" className="text-error">{error.message}</p>}
    {!error && matching.length === 0 && <p>{t('noRuns')}</p>}
    {matching.length === 0 && children}
    {matching.map(run => <RunDetails key={run.id} run={run} onExpand={() => setExpandedId(run.id)}>{children}</RunDetails>)}
    {expanded && <TraceDialog run={expanded} onClose={() => setExpandedId(null)} />}
  </section>;
}

function TraceDialog({ run, onClose }: { run: AgentRun; onClose: () => void }) {
  const { t } = useTranslation('agents');
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current!;
    dialog.showModal();
    return () => dialog.close();
  }, []);
  return createPortal(<div className="np-shell"><dialog ref={ref} aria-label={t('history')}
    onCancel={e => { e.preventDefault(); onClose(); }}
    className="fixed inset-0 m-auto w-[min(960px,calc(100vw-2rem))] max-h-[90vh] overflow-y-auto rounded-xl bg-surface-lowest text-on-surface shadow-2xl ring-1 ring-outline-variant/20 p-4 sm:p-6 backdrop:bg-black/30">
    <div className="flex justify-between items-center gap-3 mb-4"><h2 className="font-semibold">{t('history')}</h2>
      <button type="button" className="text-primary" onClick={onClose}>{t('trace.close')}</button></div>
    <RunDetails run={run} />
  </dialog></div>, document.body);
}

function RunDetails({ run, onExpand, children }: { run: AgentRun; onExpand?: () => void; children?: ReactNode }) {
  const { t } = useTranslation('agents');
  const { data: events = [], error, isFetching } = useAgentEvents(run);
  const [visible, setVisible] = useState(100);
  const [member, setMember] = useState('');
  const [technical, setTechnical] = useState(false);
  const members = useMemo(() => runMembers(events), [events]);
  const trace = useMemo(() => agentRunTrace(events), [events]);
  const filtered = trace.filter(entry => traceMatches(entry, member));
  const items = useMemo(() => groupTrace(filtered.slice(-visible)), [filtered, visible]);
  const raw = events.filter(event => !member || event.memberId === member);
  const label = (id: string | null) => {
    const name = members.find(m => m.id === id)?.role;
    return name ? members.filter(m => m.role === name).length > 1 ? `${name} (${id})` : name : id ?? t('trace.host');
  };
  const exportRun = () => downloadTextFile(`nodepilot-agent-${run.id}.json`, JSON.stringify({
    schemaVersion: 1, exportedAt: new Date().toISOString(),
    complete: events.some(event => ['run_completed', 'run_succeeded', 'run_failed', 'run_cancelled'].includes(event.kind)),
    lastSequence: events.at(-1)?.sequence ?? 0, run, members, events,
  }, null, 2), 'application/json');
  return <div className="space-y-4 min-w-0" data-testid="agent-run-trace">
    <AgentResultSummary run={run} events={events} loading={isFetching} unavailable={!!error} />
    {run.result && <details className="rounded-lg border border-outline-variant/40 px-3 py-2"><summary className="cursor-pointer font-semibold">{t(run.status === 'Succeeded' ? 'trace.finalResult' : 'taskOutcome.draft')}</summary>
      {run.status !== 'Succeeded' && <p className="text-on-surface-variant">{t('taskOutcome.draftHint')}</p>}<Content value={run.result} /></details>}
    {children}

    <section className="space-y-3 rounded-lg border border-outline-variant/40 bg-surface-container/30 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2"><h4 className="text-sm font-semibold">{t('history')}</h4><RunStatus status={run.status} /></div>
        {onExpand && <button type="button" onClick={onExpand}
          className="inline-flex items-center gap-1.5 rounded-md border border-outline-variant px-2.5 py-1 font-semibold text-primary hover:bg-surface-high">
          <Maximize size={12} aria-hidden="true" />{t('trace.expand')}</button>}
      </div>
      <dl className="grid grid-cols-3 gap-2">
        <Stat label={t('modelCalls')} value={run.modelCalls} />
        <Stat label={t('toolCalls')} value={run.toolCalls} />
        <Stat label={t('delegations')} value={run.delegations} />
      </dl>
      <code className="block truncate text-[10px] text-on-surface-variant" title={run.id}>{run.id}</code>
      {members.length > 0 && <div className="space-y-1.5">
        <p className="font-label text-[10px] font-bold uppercase tracking-widest text-on-surface-variant">{t('trace.team')}</p>
        <div className="flex flex-wrap gap-1.5">{members.map(m => <span key={m.id} title={`${m.id}${m.model ? ` · ${m.model}` : ''}`}
          className="inline-flex items-center gap-1.5 rounded-full border border-outline-variant/40 bg-surface-low py-0.5 pl-0.5 pr-2.5">
          <Initial text={m.role} active={m.function === 'supervisor'} small />
          <span className="font-medium">{m.role}</span>
          {m.function && <span className="text-[10px] text-on-surface-variant">{t(m.function, { defaultValue: m.function })}</span>}
        </span>)}</div>
      </div>}
    </section>

    {run.error && <pre className="whitespace-pre-wrap rounded-lg border border-error/30 bg-error-container/10 p-3 text-error">{run.error}</pre>}
    {error && <p role="alert" className="text-error">{error.message}</p>}

    <div className="flex flex-wrap items-end gap-3 rounded-lg border border-outline-variant/40 p-2">
      <label className="min-w-40 flex-1 space-y-1 font-label font-semibold text-on-surface-variant">{t('trace.filter')}
        <select className="input-field font-normal" value={member} onChange={e => setMember(e.target.value)}>
          <option value="">{t('trace.allMembers')}</option>{members.map(m => <option key={m.id} value={m.id}>{m.role} ({m.id})</option>)}
        </select></label>
      <label className="flex items-center gap-2 py-2"><input type="checkbox" className="np-switch" checked={technical} onChange={e => setTechnical(e.target.checked)} />{t('trace.technical')}</label>
      <button type="button" className="rounded-md border border-outline-variant px-2.5 py-2 font-semibold text-primary hover:bg-surface-high disabled:opacity-50"
        disabled={isFetching || !!error || events.length === 0} onClick={exportRun}>{t('trace.export')}</button>
    </div>
    {isFetching && <p className="text-on-surface-variant" role="status">{t('trace.syncing')}</p>}
    {(technical ? raw : filtered).length > visible && <button type="button" className="text-primary" onClick={() => setVisible(v => v + 100)}>{t('loadMore')}</button>}
    <ol className="relative ml-1.5 max-h-[36rem] space-y-2.5 overflow-y-auto border-l border-outline-variant/40 pb-1 pl-5 pr-1" aria-label={t('events')}>
      {technical ? raw.slice(-visible).map(event => <li key={event.sequence} data-agent-sequence={event.sequence} className="relative">
        <Dot tone="neutral" />
        <details className="rounded-md border border-outline-variant/30 px-2 py-1.5">
          <summary className="cursor-pointer break-words"><EventTime event={event} /> {label(event.memberId)} · {event.kind}{event.toolName && ` · ${event.toolName}`}</summary>
          <Content value={event.content} />
        </details></li>)
        : items.map(item => item.kind === 'group'
          ? <li key={item.entries[0].start.sequence} data-agent-sequence={item.entries[0].start.sequence} className="relative">
            <Dot tone="neutral" /><EventGroup entries={item.entries} run={run} label={label} /></li>
          : <li key={item.entry.start.sequence} data-agent-sequence={item.entry.start.sequence} className="relative">
            <Dot tone={entryTone(item.entry, run)} /><TraceRow entry={item.entry} run={run} label={label} /></li>)}
    </ol>
    {events.length === 0 && <p>{t('noEvents')}</p>}
  </div>;
}

type Tone = 'neutral' | 'primary' | 'success' | 'warning' | 'error';
const toneDot: Record<Tone, string> = {
  neutral: 'bg-outline-variant', primary: 'bg-primary', success: 'bg-success', warning: 'bg-warning', error: 'bg-error',
};
const toneText: Record<Tone, string> = {
  neutral: 'text-on-surface-variant', primary: 'text-primary', success: 'text-success', warning: 'text-warning', error: 'text-error',
};

function outcomeTone(outcome: string): Tone {
  if (outcome === 'failed' || outcome === 'interrupted') return 'error';
  if (outcome === 'needs_input') return 'warning';
  if (outcome === 'completed') return 'success';
  return outcome === 'running' ? 'primary' : 'neutral';
}

function entryTone(entry: TraceEntry, run: AgentRun): Tone {
  const tone = outcomeTone(traceOutcome(entry, run));
  // Plain events stay quiet unless something went wrong; assignments carry their outcome colour.
  if (entry.type === 'delegation') return tone === 'neutral' ? 'primary' : tone;
  return tone === 'error' || tone === 'warning' ? tone : 'neutral';
}

/** Marker on the timeline rail, aligned with the first line of the row. */
function Dot({ tone }: { tone: Tone }) {
  return <span aria-hidden="true" className={`absolute -left-[26px] top-3 h-2.5 w-2.5 rounded-full ring-4 ring-surface-lowest ${toneDot[tone]}`} />;
}

function Stat({ label, value }: { label: string; value: number }) {
  return <div className="rounded-md bg-surface-low px-2.5 py-1.5">
    <dd className="text-base font-semibold tabular-nums text-on-surface">{value}</dd>
    <dt className="truncate text-[11px] text-on-surface-variant">{label}</dt>
  </div>;
}

function RunStatus({ status }: { status: string }) {
  const { t } = useTranslation('agents');
  const tone: Tone = status === 'Running' ? 'primary' : status === 'Succeeded' ? 'success' : status === 'Failed' ? 'error' : 'neutral';
  return <span className={`inline-flex items-center gap-1.5 rounded-full bg-surface-highest px-2 py-0.5 text-[11px] font-semibold ${toneText[tone]}`}>
    <span aria-hidden="true" className={`h-1.5 w-1.5 rounded-full ${toneDot[tone]} ${status === 'Running' ? 'animate-pulse' : ''}`} />
    {t(`status.${status}`, { defaultValue: status })}</span>;
}

function Content({ value }: { value: string }) {
  let formatted = value;
  try { formatted = JSON.stringify(JSON.parse(value), null, 2); } catch { /* Plain text is an ordinary event payload. */ }
  return <pre className="whitespace-pre-wrap break-words [overflow-wrap:anywhere] rounded-md bg-surface-low p-2 font-mono max-h-80 overflow-y-auto">{formatted}</pre>;
}

function EventTime({ event }: { event: AgentRunEvent }) {
  useTranslation();
  const date = new Date(event.timestamp);
  return <span className="font-mono text-on-surface-variant">#{event.sequence} · <time dateTime={event.timestamp} title={event.timestamp}>
    {Number.isNaN(date.getTime()) ? event.timestamp : formatTime(date, { hour: '2-digit', minute: '2-digit', second: '2-digit' })}
  </time></span>;
}

function Outcome({ entry, run }: { entry: TraceEntry; run: AgentRun }) {
  const { t } = useTranslation('agents');
  const outcome = traceOutcome(entry, run);
  const verdict = entry.type === 'delegation' && entry.reviewer
    ? outcome === 'completed' ? t('trace.reviewApproved') : outcome === 'needs_input' ? t('trace.reviewNeedsWork') : null : null;
  const seconds = traceDuration(entry.start.timestamp, entry.end?.timestamp ?? (outcome === 'interrupted' ? run.completedAt : undefined));
  return <span className={`${toneText[outcomeTone(outcome)]} ${entry.type === 'delegation' ? 'font-semibold' : ''}`}>
    {verdict ?? (outcome && t(`trace.outcomes.${outcome}`, { defaultValue: outcome }))}{seconds !== undefined && ` · ${seconds.toFixed(1)} s`}
  </span>;
}

/** Disclosure row inside a card: a full-width, clearly clickable line with a chevron. */
const disclosureSummary = 'flex cursor-pointer list-none items-center gap-2 rounded-md px-2 py-1.5 text-primary hover:bg-surface-high [&::-webkit-details-marker]:hidden';

function Chevron() {
  return <ChevronRight size={12} aria-hidden="true" className="shrink-0 transition-transform group-open:rotate-90" />;
}

function TraceRow({ entry, run, label }: { entry: TraceEntry; run: AgentRun; label: (id: string | null) => string }) {
  const { t } = useTranslation('agents');
  if (entry.type === 'event') return <ActivityRow entry={entry} run={run} label={label} />;
  return <article className="space-y-3 rounded-lg border border-outline-variant/40 bg-surface-low p-3" aria-label={`${label(entry.from)} → ${label(entry.to)}`}>
    <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1"><EventTime event={entry.start} /><Outcome entry={entry} run={run} /></div>
    <div className="flex flex-wrap items-center gap-2">
      <h5 className="break-words text-sm font-semibold">{label(entry.from)} <span className="text-primary">→</span> {label(entry.to)}</h5>
      {(entry.batchSize ?? 0) > 1 && <span className="rounded-full bg-surface-highest px-2 py-0.5 text-[11px] text-on-surface-variant">{t('trace.parallel', { count: entry.batchSize })}</span>}
    </div>
    {entry.reason && <blockquote className="border-l-2 border-primary/40 pl-3">
      <p className="font-label text-[10px] font-bold uppercase tracking-widest text-on-surface-variant">{t('trace.reason')}</p>
      <p className="whitespace-pre-wrap break-words">{entry.reason}</p>
    </blockquote>}
    <div className="space-y-0.5 border-t border-outline-variant/30 pt-2">
      <details className="group"><summary className={disclosureSummary}><Chevron />{t('trace.assignment')}</summary><Content value={entry.task} /></details>
      <details className="group"><summary className={disclosureSummary}><Chevron />{t('trace.activities', { count: entry.activities.length })}</summary>
        <ol className="ml-2 mt-1 space-y-1.5 border-l border-outline-variant/40 pl-3">{entry.activities.map(activity => <li key={activity.start.sequence} data-agent-sequence={activity.start.sequence}>
          <ActivityRow entry={activity} run={run} label={label} />
        </li>)}</ol>
      </details>
      {entry.end && <details className="group"><summary className={`${disclosureSummary} font-semibold`}>
        <Chevron />
        {label(entry.to)} → {label(entry.from)} · {t(entry.status === 'needs_input' ? 'trace.question' : 'trace.answer')}
      </summary>
        <p className="px-2 py-1"><EventTime event={entry.end} />{entry.objectionKind && ` · ${t(`trace.objections.${entry.objectionKind}`, { defaultValue: entry.objectionKind })}`}</p>
        <Content value={entry.response ?? ''} />
      </details>}
    </div>
  </article>;
}

/** Consecutive events of one member folded into a single line; opens to the individual rows. */
function EventGroup({ entries, run, label }: { entries: TraceEvent[]; run: AgentRun; label: (id: string | null) => string }) {
  const { t } = useTranslation('agents');
  const failed = entries.filter(e => traceOutcome(e, run) === 'failed' || traceOutcome(e, run) === 'interrupted').length;
  const models = entries.filter(e => e.start.kind.startsWith('model_')).length;
  const tools = entries.filter(e => e.start.kind.startsWith('tool_')).length;
  const seconds = traceDuration(entries[0].start.timestamp, (entries.at(-1)!.end ?? entries.at(-1)!.start).timestamp);
  return <details className="rounded-lg border border-outline-variant/30 px-3 py-2">
    <summary className="cursor-pointer break-words">
      <span className="font-medium">{label(entries[0].start.memberId)}</span>
      <span className="text-on-surface-variant"> · {t('trace.stepsGroup', { count: entries.length })}
        {(models > 0 || tools > 0) && ` (${t('trace.stepsBreakdown', { models, tools })})`}</span>
      {failed > 0 && <span className="text-error"> · {t('trace.outcomes.failed')}: {failed}</span>}
      <span className="block text-[11px]"><EventTime event={entries[0].start} />{seconds !== undefined && <span className="text-on-surface-variant"> · {seconds.toFixed(1)} s</span>}</span>
    </summary>
    <ol className="mt-2 space-y-1.5 border-l border-outline-variant/40 pl-3">{entries.map(entry => <li key={entry.start.sequence} data-agent-sequence={entry.start.sequence}>
      <ActivityRow entry={entry} run={run} label={label} />
    </li>)}</ol>
  </details>;
}

function ActivityRow({ entry, run, label }: { entry: TraceEvent; run: AgentRun; label: (id: string | null) => string }) {
  const { t } = useTranslation('agents');
  const [open, setOpen] = useState(false);
  const event = entry.start;
  const title = event.toolName ? t(`toolNames.${event.toolName}`, { defaultValue: event.toolName })
    : t(`trace.events.${event.kind}`, { defaultValue: event.kind });
  return <details className="rounded-md border border-outline-variant/30 px-2 py-1.5" onToggle={e => setOpen(e.currentTarget.open)}>
    <summary className="cursor-pointer break-words space-y-0.5">
      <span className="flex flex-wrap items-center justify-between gap-x-3"><EventTime event={event} /><Outcome entry={entry} run={run} /></span>
      <span className="block font-medium">{label(event.memberId)} · {title}</span>
    </summary>
    {open && <div className="space-y-1 pt-2">{event.toolName && <p className="text-on-surface-variant">{t('trace.arguments')}</p>}<Content value={event.content} />
      {entry.end && <><p className="text-on-surface-variant">{t(entry.end.kind.endsWith('_failed') ? 'trace.error' : 'trace.result')}</p><Content value={entry.end.content} /></>}
    </div>}
  </details>;
}
