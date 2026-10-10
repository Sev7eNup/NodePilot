import { useTranslation } from 'react-i18next';
import { agentRunCoverage } from '../../../lib/agentRunSummary';
import type { AgentRun, AgentRunEvent } from '../../../types/agents';

export function AgentResultSummary({ run, events, loading, unavailable }: {
  run: AgentRun; events: AgentRunEvent[]; loading: boolean; unavailable: boolean;
}) {
  const { t } = useTranslation('agents');
  const running = run.status === 'Running';
  const assessed = run.status === 'Succeeded' && !!run.outcome && run.outcome !== 'unassessed';
  const outcome = assessed ? run.outcome : 'unassessed';
  const coverage = assessed && !unavailable ? agentRunCoverage(events) : null;
  return <section aria-label={t('summary.title')} data-testid="agent-result-summary"
    className="rounded-lg border border-outline-variant bg-surface-low p-3 space-y-3 min-w-0">
    <p data-testid="agent-execution-status"><span className="font-semibold">{t('summary.executionStatus')}: </span>{t(`status.${run.status}`)}</p>
    <div data-testid="agent-task-outcome" className="space-y-1">
      <p className="text-on-surface-variant">{t('taskOutcome.label')}</p>
      <h4 className="font-semibold text-sm">{running ? t('summary.running') : t(`taskOutcome.${outcome}`)}</h4>
      {assessed && run.outcomeReason && <p className="whitespace-pre-wrap break-words [overflow-wrap:anywhere]">{run.outcomeReason}</p>}
      <p className="text-on-surface-variant">{t(running ? 'summary.runningHint' : assessed ? 'taskOutcome.hint' : 'summary.unassessedHint')}</p>
    </div>
    {!assessed && run.result && <p className="text-on-surface-variant">{t('summary.checkpointAvailable')}</p>}
    {coverage ? <div className="space-y-3 max-h-64 overflow-y-auto">
      {(['unresolved', 'fulfilled'] as const).map(status => {
        const items = coverage.filter(item => item.status === status);
        return items.length > 0 && <div key={status}>
          <h5 className="font-semibold">{t(`summary.${status}`)} ({items.length})</h5>
          <ul className="mt-2 space-y-2">
            {items.map((item, index) => <li key={index} className="flex gap-2">
              <span aria-hidden="true" className="shrink-0 text-on-surface-variant">{status === 'fulfilled' ? '✓' : '•'}</span>
              <div className="min-w-0 break-words [overflow-wrap:anywhere]">
                <p>{item.requirement}</p>
                <p className="text-on-surface-variant whitespace-pre-wrap">{item.basis}</p>
              </div>
            </li>)}
          </ul>
        </div>;
      })}
    </div> : assessed && <p className="text-on-surface-variant" role="status">
      {t(unavailable ? 'summary.unavailable' : loading ? 'summary.loading' : 'summary.noCoverage')}
    </p>}
  </section>;
}
