import { createContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { useAgentEvents, useAgentRuns } from '../../../hooks/useAgentRuns';
import type { AgentRun, AgentRunEvent } from '../../../types/agents';
import { agentRunTrace, runMembers, traceDuration, traceMatches, traceOutcome, type TraceEntry, type TraceEvent } from '../../../lib/agentRunTrace';
import { downloadTextFile } from '../../../lib/chatExport';
import { AgentResultSummary } from './AgentResultSummary';
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
  return <div className="space-y-3 min-w-0" data-testid="agent-run-trace">
    <AgentResultSummary run={run} events={events} loading={isFetching} unavailable={!!error} />
    {run.result && <details className="border-b border-outline-variant pb-3"><summary className="cursor-pointer font-semibold">{t(run.status === 'Succeeded' ? 'trace.finalResult' : 'taskOutcome.draft')}</summary>
      {run.status !== 'Succeeded' && <p className="text-on-surface-variant">{t('taskOutcome.draftHint')}</p>}<Content value={run.result} /></details>}
    {children}
    <h4 className="font-semibold">{t('history')}</h4>
    <div>{t(`status.${run.status}`)} · {t('modelCalls')}: {run.modelCalls} · {t('toolCalls')}: {run.toolCalls} · {t('delegations')}: {run.delegations}</div>
    <code className="text-on-surface-variant break-all">{run.id}</code>
    {onExpand && <button type="button" className="text-primary block" onClick={onExpand}>{t('trace.expand')}</button>}
    <div className="flex flex-wrap gap-2">{members.map(m => <span key={m.id} className="rounded border border-outline-variant px-2 py-1" title={`${m.id}${m.model ? ` · ${m.model}` : ''}`}>
      {m.role}{m.function && ` · ${t(m.function, { defaultValue: m.function })}`}
    </span>)}</div>
    {run.error && <pre className="whitespace-pre-wrap text-error">{run.error}</pre>}
    {error && <p role="alert" className="text-error">{error.message}</p>}
    <div className="flex flex-wrap items-end gap-3">
      <label className="min-w-0 flex-1 space-y-1">{t('trace.filter')}<select className="input-field" value={member} onChange={e => setMember(e.target.value)}>
        <option value="">{t('trace.allMembers')}</option>{members.map(m => <option key={m.id} value={m.id}>{m.role} ({m.id})</option>)}
      </select></label>
      <button type="button" className="text-primary disabled:opacity-50" disabled={isFetching || !!error || events.length === 0} onClick={exportRun}>{t('trace.export')}</button>
    </div>
    <label className="flex items-center gap-2"><input type="checkbox" checked={technical} onChange={e => setTechnical(e.target.checked)} />{t('trace.technical')}</label>
    {isFetching && <p className="text-on-surface-variant" role="status">{t('trace.syncing')}</p>}
    {(technical ? raw : filtered).length > visible && <button type="button" className="text-primary" onClick={() => setVisible(v => v + 100)}>{t('loadMore')}</button>}
    <ol className="space-y-3 max-h-[36rem] overflow-y-auto pr-1" aria-label={t('events')}>
      {technical ? raw.slice(-visible).map(event => <li key={event.sequence} data-agent-sequence={event.sequence}><details className="border-l border-outline-variant pl-2">
        <summary className="cursor-pointer break-words"><EventTime event={event} /> {label(event.memberId)} · {event.kind}{event.toolName && ` · ${event.toolName}`}</summary>
        <Content value={event.content} />
      </details></li>) : filtered.slice(-visible).map(entry => <li key={entry.start.sequence} data-agent-sequence={entry.start.sequence}>
        <TraceRow entry={entry} run={run} label={label} />
      </li>)}
    </ol>
    {events.length === 0 && <p>{t('noEvents')}</p>}
  </div>;
}

function Content({ value }: { value: string }) {
  let formatted = value;
  try { formatted = JSON.stringify(JSON.parse(value), null, 2); } catch { /* Plain text is an ordinary event payload. */ }
  return <pre className="whitespace-pre-wrap break-words [overflow-wrap:anywhere] p-2 font-mono max-h-80 overflow-y-auto">{formatted}</pre>;
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
  return <span className={['failed', 'interrupted'].includes(outcome) ? 'text-error' : 'text-on-surface-variant'}>
    {verdict ?? (outcome && t(`trace.outcomes.${outcome}`, { defaultValue: outcome }))}{seconds !== undefined && ` · ${seconds.toFixed(1)} s`}
  </span>;
}

function TraceRow({ entry, run, label }: { entry: TraceEntry; run: AgentRun; label: (id: string | null) => string }) {
  const { t } = useTranslation('agents');
  if (entry.type === 'event') return <ActivityRow entry={entry} run={run} label={label} />;
  return <article className="rounded border border-outline-variant p-3 space-y-2" aria-label={`${label(entry.from)} → ${label(entry.to)}`}>
    <div className="flex flex-wrap justify-between gap-1"><EventTime event={entry.start} /><Outcome entry={entry} run={run} /></div>
    <h5 className="font-semibold break-words">{label(entry.from)} <span className="text-primary">→</span> {label(entry.to)}</h5>
    {(entry.batchSize ?? 0) > 1 && <span className="inline-block rounded border border-outline-variant px-2 py-1 text-on-surface-variant">{t('trace.parallel', { count: entry.batchSize })}</span>}
    {entry.reason && <p className="whitespace-pre-wrap break-words"><span className="text-on-surface-variant">{t('trace.reason')}: </span>{entry.reason}</p>}
    <details><summary className="cursor-pointer text-primary">{t('trace.assignment')}</summary><Content value={entry.task} /></details>
    <details><summary className="cursor-pointer text-primary">{t('trace.activities', { count: entry.activities.length })}</summary>
      <ol className="space-y-2 border-l border-outline-variant ml-1 pl-2 pt-2">{entry.activities.map(activity => <li key={activity.start.sequence} data-agent-sequence={activity.start.sequence}>
        <ActivityRow entry={activity} run={run} label={label} />
      </li>)}</ol>
    </details>
    {entry.end && <details className="border-t border-outline-variant pt-2"><summary className="cursor-pointer font-semibold">
      {label(entry.to)} → {label(entry.from)} · {t(entry.status === 'needs_input' ? 'trace.question' : 'trace.answer')}
    </summary>
      <p className="pt-2"><EventTime event={entry.end} />{entry.objectionKind && ` · ${t(`trace.objections.${entry.objectionKind}`, { defaultValue: entry.objectionKind })}`}</p>
      <Content value={entry.response ?? ''} />
    </details>}
  </article>;
}

function ActivityRow({ entry, run, label }: { entry: TraceEvent; run: AgentRun; label: (id: string | null) => string }) {
  const { t } = useTranslation('agents');
  const [open, setOpen] = useState(false);
  const event = entry.start;
  const title = event.toolName ? t(`toolNames.${event.toolName}`, { defaultValue: event.toolName })
    : t(`trace.events.${event.kind}`, { defaultValue: event.kind });
  return <details className="border-l border-outline-variant pl-2" onToggle={e => setOpen(e.currentTarget.open)}>
    <summary className="cursor-pointer break-words space-y-1"><span className="block"><EventTime event={event} /> · <Outcome entry={entry} run={run} /></span>
      <span className="font-medium">{label(event.memberId)} · {title}</span>
    </summary>
    {open && <div className="pt-2">{event.toolName && <p className="text-on-surface-variant">{t('trace.arguments')}</p>}<Content value={event.content} />
      {entry.end && <><p className="text-on-surface-variant">{t(entry.end.kind.endsWith('_failed') ? 'trace.error' : 'trace.result')}</p><Content value={entry.end.content} /></>}
    </div>}
  </details>;
}
