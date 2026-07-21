import {
  Chemistry,
  ChevronDown,
  ChevronUp,
  CircleDash,
  DataBase,
  History,
  RadioButton,
  View,
} from '@carbon/icons-react';
import { useState, useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import type { Node } from '@xyflow/react';
import { api } from '../../api/client';
import type { WorkflowExecution, Workflow } from '../../types/api';
import type { LiveExecution } from '../../hooks/useSignalR';
import { KinskiEasterEgg } from '../easter-eggs/KinskiEasterEgg';
import { OutputTab } from './execution/OutputTab';
import { WatchTab } from './execution/WatchTab';
import { HistoryTab } from './execution/HistoryTab';
import { LiveTab } from './execution/LiveExecutionPanel';
import { useDesignStore } from '../../stores/designStore';

/** A lightweight simulation snapshot the editor passes down to the bottom panel, so it
 *  lists the dry-run result there instead of "No active execution". Not persisted.
 *  `revealIndex` mirrors the running animation — rows with index < revealIndex are already
 *  "checked off", index == revealIndex is the step currently animating in. */
export interface SimulationSnapshot {
  order: string[];
  skipped: string[];
  nodeLabels: Record<string, string>;
  nodeTypes: Record<string, string>;
  revealIndex: number;
}

interface Props {
  workflowId: string;
  liveExecution: LiveExecution | null;
  liveExecutions?: LiveExecution[];
  liveActiveCount?: number;
  connected: boolean;
  height?: number;
  simulation?: SimulationSnapshot | null;
  onReplay?: (executionId: string) => void;
  activeReplayId?: string | null;
  onScrubTime?: (t: number | null) => void;
  onJoinExecution?: (executionId: string, workflowId: string) => Promise<void>;
  onLeaveExecution?: (executionId: string) => Promise<void>;
  /** Nodes of the current workflow definition. Needed by the Watch tab so the
   *  right-click variable picker can offer all step outputs even without an active execution. */
  nodes?: Node[];
}

export function ExecutionPanel({ workflowId, liveExecution, liveExecutions, liveActiveCount, connected, height, simulation, onReplay, activeReplayId, onScrubTime, onJoinExecution, onLeaveExecution, nodes }: Readonly<Props>) {
  const { t } = useTranslation('designer');
  const expertMode = useDesignStore((s) => s.designerMode === 'expert');
  const [isCollapsed, setIsCollapsed] = useState(false);
  const [activeTab, setActiveTab] = useState<'live' | 'history' | 'output' | 'watch'>('live');
  const [kinskiClickCount, setKinskiClickCount] = useState(0);
  const [showKinski, setShowKinski] = useState(false);
  const outputTabRef = useRef<HTMLButtonElement>(null);

  // The Kinski easter egg needs 10 *consecutive* clicks on the Output tab. As soon as
  // the user clicks anywhere else (canvas, another tab, properties, …), the counter must be
  // reset — otherwise 10 clicks scattered across a session would still trigger the egg.
  // The effect depends on a boolean (counter > 0) so the listener attaches only once on the
  // 0→1 transition and detaches again on reset — no re-attaching on every increment, and no
  // global click handler at all while the user isn't counting. We recognize output-tab clicks
  // via the ref and ignore them here (the increment itself happens in TabButton's onClick).
  const kinskiCounting = kinskiClickCount > 0;
  useEffect(() => {
    if (!kinskiCounting) return;
    const handler = (e: MouseEvent) => {
      if (outputTabRef.current?.contains(e.target as globalThis.Node)) return;
      setKinskiClickCount(0);
    };
    document.addEventListener('click', handler);
    return () => document.removeEventListener('click', handler);
  }, [kinskiCounting]);

  // When the user just started a simulation, they should see the result right away —
  // not miss it behind a collapsed panel or the History tab. We expand the panel and switch to
  // the Live tab as soon as `simulation` becomes non-null. useEffect depends on `simulation`
  // so subsequent re-clicks reopen the panel too.
  useEffect(() => {
    if (simulation) {
      setIsCollapsed(false);
      setActiveTab('live');
    }
  }, [simulation]);

  // Same auto-expand when a step pauses at a breakpoint — the user shouldn't miss the
  // debug UX. The tab is forced to Live so the inspector actually takes over (otherwise
  // SimulationTab would shadow the debug UI — see LiveTab's precedence order).
  const displayExecutions = liveExecutions?.length ? liveExecutions : (liveExecution ? [liveExecution] : []);
  const anyStepPaused = displayExecutions.some((execution) => execution.steps.some((s) => s.status === 'Paused'));
  const runningExecutionCount = liveActiveCount ?? displayExecutions.filter((execution) => execution.status === 'Running' || execution.status === 'Pending').length;
  const databusEntryCount = Object.keys(liveExecution?.databus ?? {}).length;
  useEffect(() => {
    if (anyStepPaused) {
      setIsCollapsed(false);
      setActiveTab('live');
    }
  }, [anyStepPaused]);
  const [historyScope, setHistoryScope] = useState<'current' | 'all'>('current');
  const [expandedHistoryId, setExpandedHistoryId] = useState<string | null>(null);

  useEffect(() => {
    if (expertMode) return;
    if (activeTab === 'watch') setActiveTab('live');
    if (historyScope === 'all') setHistoryScope('current');
  }, [expertMode, activeTab, historyScope]);

  // The History tab shows only terminal runs. Active runs are rendered via the Live
  // tab through SignalR instead — this avoids duplication and makes the behaviour clearer
  // ("Live = what's running", "History = what happened"). `terminalOnly=true` filters
  // server-side. No refetchInterval: useSignalR invalidates this cache via
  // scheduleQueryInvalidate(workflowId) on every ExecutionStatusChanged event — exactly when
  // new terminal runs can appear in the list.
  const { data: executions } = useQuery({
    queryKey: ['workflow-executions', workflowId, historyScope, 'terminalOnly'],
    queryFn: () => api.get<WorkflowExecution[]>(
      historyScope === 'all'
        ? '/executions?terminalOnly=true'
        : `/executions?workflowId=${workflowId}&terminalOnly=true`),
  });

  const { data: allWorkflows } = useQuery({
    queryKey: ['workflows'],
    queryFn: () => api.get<Workflow[]>('/workflows'),
    enabled: historyScope === 'all',
  });
  const workflowNames = new Map((allWorkflows ?? []).map(w => [w.id, w.name]));

  if (isCollapsed) {
    return (
      <div
        className="np-execution-panel bg-surface-lowest border-t border-outline-variant/20 px-5 py-2 flex items-center justify-between cursor-pointer hover:bg-surface-low transition-colors"
        onClick={() => setIsCollapsed(false)}
        onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && setIsCollapsed(false)}
        role="button"
        tabIndex={0}
      >
        <div className="flex items-center gap-3">
          <ChevronUp size={14} className="text-on-surface-variant" />
          <span className="font-label text-xs font-semibold text-on-surface-variant uppercase tracking-wide">{t('execution.title')}</span>
          {runningExecutionCount > 0 && (
            <div className="flex items-center gap-1.5 px-2 py-0.5 bg-blue-50 dark:bg-blue-950/50 rounded-full">
              <CircleDash size={10} className="text-blue-600 dark:text-blue-400 animate-spin" />
              <span className="text-[10px] font-label font-semibold text-blue-600 dark:text-blue-400">
                {runningExecutionCount === 1 ? t('execution.running') : t('execution.runningCount', { count: runningExecutionCount })}
              </span>
            </div>
          )}
          {simulation && (
            <div className="flex items-center gap-1.5 px-2 py-0.5 bg-primary text-on-primary rounded-full">
              <Chemistry size={10} />
              <span className="text-[10px] font-label font-semibold">
                {t('execution.dryRunBadge', { willRun: simulation.order.length, skipped: simulation.skipped.length })}
              </span>
            </div>
          )}
          {anyStepPaused && (
            <div className="flex items-center gap-1.5 px-2 py-0.5 bg-orange-600 text-white rounded-full animate-pulse">
              <span className="text-[10px] font-label font-semibold">
                {t('execution.pausedBreakpoint')}
              </span>
            </div>
          )}
          {connected && <div className="w-1.5 h-1.5 rounded-full bg-green-500" title={t('execution.connected')} />}
        </div>
        <span className="font-label text-[10px] text-outline">{t('execution.runs', { count: executions?.length ?? 0 })}</span>
      </div>
    );
  }

  return (
    <div className="np-execution-panel bg-surface-lowest border-t border-outline-variant/20 flex flex-col isolate" style={{ height: height ?? 280 }}>
      {/* Tab bar */}
      <div className="flex items-center justify-between px-4 py-0 border-b border-outline-variant/10 shrink-0 bg-surface-low">
        <div className="flex items-center">
          <TabButton active={activeTab === 'live'} onClick={() => setActiveTab('live')}>
            <RadioButton size={13} />
            {t('execution.tabs.live')}
            {runningExecutionCount > 0 && (
              <div className="w-1.5 h-1.5 rounded-full bg-blue-500 animate-pulse" />
            )}
            <span className="text-[10px] bg-surface-highest rounded-full px-1.5 py-0 font-semibold">
              {runningExecutionCount}
            </span>
          </TabButton>
          <TabButton active={activeTab === 'history'} onClick={() => setActiveTab('history')}>
            <History size={13} />
            {t('execution.tabs.history')}
            {(executions?.length ?? 0) > 0 && (
              <span className="text-[10px] bg-surface-highest rounded-full px-1.5 py-0 font-semibold">
                {executions?.length}
              </span>
            )}
          </TabButton>
          <TabButton ref={outputTabRef} active={activeTab === 'output'} onClick={() => {
            setActiveTab('output');
            const next = kinskiClickCount + 1;
            if (next >= 10) { setShowKinski(true); setKinskiClickCount(0); }
            else setKinskiClickCount(next);
          }}>
            <DataBase size={13} />
            {t('execution.tabs.output')}
            {databusEntryCount > 0 && (
              <span className="text-[10px] bg-surface-highest rounded-full px-1.5 py-0 font-semibold">
                {databusEntryCount}
              </span>
            )}
          </TabButton>
          {expertMode && <TabButton active={activeTab === 'watch'} onClick={() => setActiveTab('watch')}>
            <View size={13} />
            {t('execution.tabs.watch')}
          </TabButton>}
        </div>
        <div className="flex items-center gap-2 pr-1">
          {connected && <div className="w-1.5 h-1.5 rounded-full bg-green-500" title={t('execution.signalrConnected')} />}
          <button
            onClick={() => setIsCollapsed(true)}
            title={t('execution.collapsePanel')}
            aria-label={t('execution.collapsePanel')}
            className="text-on-surface-variant hover:text-on-surface p-1 hover:bg-surface-highest rounded transition-colors"
          >
            <ChevronDown size={14} />
          </button>
        </div>
      </div>
      {/* History scope toggle */}
      {expertMode && activeTab === 'history' && (
        <div className="flex items-center justify-between gap-2 px-4 py-1.5 border-b border-outline-variant/10 bg-surface-low/40 shrink-0">
          <div className="flex items-center gap-0.5 text-[10px]">
            <button
              onClick={() => setHistoryScope('current')}
              className={`px-2 py-0.5 rounded ${historyScope === 'current' ? 'bg-primary text-on-primary' : 'bg-surface-container text-on-surface-variant hover:bg-surface-high'}`}
            >{t('execution.historyScope.thisWorkflow')}</button>
            <button
              onClick={() => setHistoryScope('all')}
              className={`px-2 py-0.5 rounded ${historyScope === 'all' ? 'bg-primary text-on-primary' : 'bg-surface-container text-on-surface-variant hover:bg-surface-high'}`}
            >{t('execution.historyScope.allWorkflows')}</button>
          </div>
          <Link to="/executions" className="text-[10px] text-primary hover:underline font-label">
            {t('execution.historyScope.openFullList')}
          </Link>
        </div>
      )}
      {showKinski && createPortal(<KinskiEasterEgg onClose={() => setShowKinski(false)} />, document.body)}
      <div className="flex-1 overflow-hidden">
        {activeTab === 'live' ? (
          <LiveTab
            execution={liveExecution}
            executionsLive={displayExecutions}
            simulation={simulation ?? null}
            workflowId={workflowId}
            executions={executions ?? []}
            panelHeight={height ?? 280}
            onJoinExecution={onJoinExecution}
            onLeaveExecution={onLeaveExecution}
          />
        ) : activeTab === 'output' ? (
          <OutputTab databus={liveExecution?.databus ?? {}} steps={liveExecution?.steps ?? []} />
        ) : activeTab === 'watch' ? (
          <WatchTab workflowId={workflowId} databus={liveExecution?.databus ?? {}} nodes={nodes ?? []} />
        ) : (
          <HistoryTab
            executions={executions ?? []}
            scope={historyScope}
            workflowNames={workflowNames}
            expandedId={expandedHistoryId}
            onToggle={(id) => setExpandedHistoryId(expandedHistoryId === id ? null : id)}
            onReplay={onReplay}
            activeReplayId={activeReplayId}
            onScrubTime={onScrubTime}
          />
        )}
      </div>
    </div>
  );
}

/* ---- Tab Button ---- */

function TabButton({ active, onClick, children, ref }: Readonly<{ active: boolean; onClick: () => void; children: React.ReactNode; ref?: React.Ref<HTMLButtonElement> }>) {
  return (
    <button
      ref={ref}
      onClick={onClick}
      className={`flex items-center gap-1.5 px-4 py-2.5 text-xs font-label font-semibold border-b-2 transition-colors ${
        active
          ? 'border-primary text-primary'
          : 'border-transparent text-on-surface-variant hover:text-on-surface hover:border-outline-variant/30'
      }`}
    >
      {children}
    </button>
  );
}
