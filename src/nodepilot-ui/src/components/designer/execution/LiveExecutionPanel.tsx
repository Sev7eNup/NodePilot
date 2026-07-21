import {
  CheckmarkFilled,
  Chemistry,
  ChevronDown,
  ChevronRight,
  CircleDash,
  CircleStroke,
  Close,
  RadioButton,
  Time,
} from '@carbon/icons-react';
import { useState, useEffect, useMemo, useRef, memo } from 'react';
import { useTranslation } from 'react-i18next';
import { useVirtualizer } from '@tanstack/react-virtual';
import { EmptyState } from '../../common/EmptyState';
import { api } from '../../../api/client';
import type { WorkflowExecution } from '../../../types/api';
import type { LiveExecution } from '../../../hooks/useSignalR';
import type { SimulationSnapshot } from '../ExecutionPanel';
import { useResizable } from '../../../hooks/useResizable';
import { PausedVariablesInspector } from '../debug/PausedVariablesInspector';
import { ActivityTypeIcon, ExecutionStatusBadge, OutputBlock, StepInputBlock, StepOutputParametersBlock, StepStatusIcon, formatMs } from './ExecutionPanelParts';
import { LiveOverview } from '../live/LiveOverview';

export function LiveTab({ executionsLive, simulation, workflowId, executions, panelHeight, onJoinExecution, onLeaveExecution }: Readonly<{
  execution: LiveExecution | null;
  executionsLive: LiveExecution[];
  simulation: SimulationSnapshot | null;
  workflowId: string;
  executions: WorkflowExecution[];
  panelHeight: number;
  onJoinExecution?: (executionId: string, workflowId: string) => Promise<void>;
  onLeaveExecution?: (executionId: string) => Promise<void>;
}>) {
  const { t } = useTranslation('designer');
  const [expandedIds, setExpandedIds] = useState<Set<string>>(() => new Set());

  useEffect(() => {
    setExpandedIds((prev) => {
      if (prev.size === 0) return prev;
      // Don't short-circuit on an empty live list: when every run is evicted at once we still
      // have to leave the groups for whatever was expanded — otherwise the per-execution
      // SignalR subscriptions leak.
      const liveIds = new Set(executionsLive.map((execution) => execution.executionId));
      const next = new Set([...prev].filter((id) => liveIds.has(id)));
      if (next.size === prev.size) return prev;
      // Leave execution groups for runs that are no longer in the live list (evicted/navigated).
      for (const id of prev) {
        if (!next.has(id)) onLeaveExecution?.(id);
      }
      return next;
    });
  }, [executionsLive, onLeaveExecution]);

  // Leave any still-expanded groups when the panel unmounts (route change / tab switch).
  // The latest expandedIds + leave-callback are mirrored into refs from an effect (writing
  // ref.current during render trips react-hooks/refs) so the unmount cleanup — which runs
  // exactly once — sees current state.
  const expandedIdsRef = useRef(expandedIds);
  const onLeaveRef = useRef(onLeaveExecution);
  useEffect(() => {
    expandedIdsRef.current = expandedIds;
    onLeaveRef.current = onLeaveExecution;
  });
  useEffect(() => () => {
    for (const id of expandedIdsRef.current) onLeaveRef.current?.(id);
  }, []);

  if (simulation) return <SimulationTab simulation={simulation} />;

  if (executionsLive.length === 0) {
    return (
      <EmptyState
        icon={<RadioButton size={20} />}
        title={t('execution.live.noActiveExecution')}
        hint={t('execution.live.clickTestToRun')}
      />
    );
  }

  return (
    <VirtualizedLiveList
      executionsLive={executionsLive}
      expandedIds={expandedIds}
      panelHeight={panelHeight}
      workflowId={workflowId}
      executions={executions}
      onToggle={(run) => setExpandedIds((prev) => {
        const next = new Set(prev);
        if (next.has(run.executionId)) {
          next.delete(run.executionId);
          onLeaveExecution?.(run.executionId);
        } else {
          next.add(run.executionId);
          onJoinExecution?.(run.executionId, run.workflowId ?? workflowId);
        }
        return next;
      })}
    />
  );
}

/**
 * Virtualizes the Live tab via @tanstack/react-virtual. With 200+ active runs, the previous
 * `executionsLive.map` approach rendered all 200 accordion items into the DOM synchronously —
 * under stress-test load (live hydration + frequent SignalR updates) this was the next UI
 * bottleneck after the hot-path optimizations.
 *
 * Strategy: collapsed items are ~26px tall, expanded items grow to
 * `Math.max(240, panelHeight - 80)` plus the accordion header. `measureElement` on the wrapper
 * div re-measures correctly whenever an item is expanded/collapsed.
 */
function VirtualizedLiveList({
  executionsLive,
  expandedIds,
  panelHeight,
  workflowId,
  executions,
  onToggle,
}: Readonly<{
  executionsLive: LiveExecution[];
  expandedIds: Set<string>;
  panelHeight: number;
  workflowId: string;
  executions: WorkflowExecution[];
  onToggle: (run: LiveExecution) => void;
}>) {
  const parentRef = useRef<HTMLDivElement>(null);
  const expandedHeight = Math.max(240, panelHeight - 80);

  const rowVirtualizer = useVirtualizer({
    count: executionsLive.length,
    getScrollElement: () => parentRef.current,
    // Initial estimate before measureElement kicks in. Collapsed = ~26px (accordion
    // header with py-0.5), expanded = header + detail pane.
    estimateSize: (index) => {
      const run = executionsLive[index];
      return expandedIds.has(run.executionId) ? expandedHeight + 28 : 26;
    },
    overscan: 6,
    getItemKey: (index) => executionsLive[index].executionId,
  });

  return (
    <div ref={parentRef} className="h-full overflow-y-auto bg-surface-low/20 p-1">
      <div
        style={{ height: rowVirtualizer.getTotalSize(), position: 'relative', width: '100%' }}
      >
        {rowVirtualizer.getVirtualItems().map((vRow) => {
          const run = executionsLive[vRow.index];
          const expanded = expandedIds.has(run.executionId);
          return (
            <div
              key={run.executionId}
              data-index={vRow.index}
              ref={rowVirtualizer.measureElement}
              style={{
                position: 'absolute',
                top: 0,
                left: 0,
                width: '100%',
                transform: `translateY(${vRow.start}px)`,
                paddingTop: 1,
              }}
            >
              <LiveExecutionAccordionItem
                execution={run}
                expanded={expanded}
                onToggle={() => onToggle(run)}
              >
                <div
                  className="border-t border-outline-variant/10"
                  style={{ height: expandedHeight }}
                >
                  <LiveExecutionDetail
                    execution={run}
                    workflowId={workflowId}
                    historyExecutions={executions}
                  />
                </div>
              </LiveExecutionAccordionItem>
            </div>
          );
        })}
      </div>
    </div>
  );
}

const LiveExecutionAccordionItem = memo(function LiveExecutionAccordionItem({ execution, expanded, onToggle, children }: {
  execution: LiveExecution;
  expanded: boolean;
  onToggle: () => void;
  children: React.ReactNode;
}) {
  const { t } = useTranslation('designer');
  // Skipped steps are control-flow decisions, not executed work — hide them from
  // the Live accordion item so the counters reflect what actually ran.
  const executedSteps = execution.steps.filter((s) => s.status !== 'Skipped');
  const succeeded = executedSteps.filter((s) => s.status === 'Succeeded').length;
  const failed = executedSteps.filter((s) => s.status === 'Failed').length;
  const running = executedSteps.filter((s) => s.status === 'Running').length;
  const done = succeeded + failed;
  const started = new Date(execution.startedAt);
  const shortId = execution.executionId.slice(0, 8);
  const hasPaused = execution.steps.some((s) => s.status === 'Paused');

  return (
    <section className="rounded border border-outline-variant/15 bg-surface-lowest overflow-hidden">
      <button
        type="button"
        onClick={onToggle}
        className="w-full flex items-center gap-1.5 px-2 py-0.5 text-left hover:bg-surface-low transition-colors"
        aria-expanded={expanded}
      >
        {expanded ? <ChevronDown size={11} className="text-on-surface-variant shrink-0" /> : <ChevronRight size={11} className="text-on-surface-variant shrink-0" />}
        <ExecutionStatusBadge status={execution.status} />
        <div className="min-w-0 flex-1 flex items-center gap-1.5">
          <span className="font-mono text-[10px] text-on-surface-variant shrink-0">{shortId}</span>
          <span className="font-label text-[11px] text-on-surface-variant shrink-0 whitespace-nowrap">
            {started.toLocaleTimeString(undefined, { hour12: false })} · {done}/{executedSteps.length}
          </span>
          {hasPaused && (
            <span className="font-label text-[10px] font-semibold text-orange-700 dark:text-orange-400 bg-orange-100 dark:bg-orange-900/40 rounded px-1 shrink-0">{t('execution.live.paused')}</span>
          )}
        </div>
        <div className="hidden sm:flex items-center gap-2 font-label text-[10px] text-on-surface-variant">
          {running > 0 && <span className="text-blue-600 font-semibold">{running}↻</span>}
          {failed > 0 && <span className="text-error font-semibold">{failed}✗</span>}
        </div>
      </button>
      {expanded && children}
    </section>
  );
}, (prev, next) => {
  // Fast identity check for SignalR bursts (200+ items per batch). Without this memo,
  // React would reconcile all 200 accordion items synchronously even if only one changed.
  // The `execution` reference only changes when applyLiveEvents in useSignalR actually
  // replaced the entry (an in-place mutation wouldn't trigger this — applyLiveEvents swaps
  // entries via `next[key] = …`).
  //
  // `onToggle`/`children` are deliberately excluded from the comparison:
  // - onToggle is an inline closure from the parent, so it changes on every parent render.
  //   Since the execution reference would also have to change for the parent to pass a new
  //   one, the stale closure is semantically equivalent.
  // - children only become visible when `expanded === true`. In that case LiveExecutionDetail
  //   needs to update live (it has its own subscriptions on execution data), so we want to
  //   let the re-render through while expanded.
  if (prev.expanded !== next.expanded) return false;
  if (prev.execution !== next.execution) return false;
  // While expanded: let children re-render so LiveExecutionDetail stays current.
  if (next.expanded) return false;
  return true;
});

function LiveExecutionDetail({ execution, workflowId, historyExecutions }: Readonly<{
  execution: LiveExecution;
  workflowId: string;
  historyExecutions: WorkflowExecution[];
}>) {
  const { t } = useTranslation('designer');
  const [selectedStep, setSelectedStep] = useState<string | null>(null);
  // Mouse-resizable step-list column. Initial 256 px matches the previous fixed `w-64`;
  // range 180–640 px covers "tight sidebar" → "half the panel". Double-click the handle
  // resets to the initial width (see useResizable).
  const stepList = useResizable({ initialSize: 300, minSize: 180, maxSize: 640, direction: 'horizontal' });

  // Mouse-resizable inspector panel (left, when step selected). Initial 550px (very wide), min 250px, max 800px.
  const inspectorPanel = useResizable({ initialSize: 550, minSize: 250, maxSize: 800, direction: 'horizontal' });

  // Median duration of past terminal runs of THIS workflow — used as ETA estimate by the
  // stats strip. MUST be declared before any conditional early-return below: hook order
  // has to stay stable across renders ("Rendered more hooks than during the previous render").
  const currentExecutionId = execution.executionId;
  const medianRunDurationMs = useMemo(() => {
    const durations = historyExecutions
      .filter((e) => e.id !== currentExecutionId && e.startedAt && e.completedAt)
      .filter((e) => e.status === 'Succeeded' || e.status === 'Failed')
      .map((e) => new Date(e.completedAt!).getTime() - new Date(e.startedAt).getTime())
      .filter((d) => d > 0)
      .sort((a, b) => a - b);
    if (durations.length === 0) return undefined;
    const mid = Math.floor(durations.length / 2);
    return durations.length % 2 ? durations[mid] : Math.round((durations[mid - 1] + durations[mid]) / 2);
  }, [historyExecutions, currentExecutionId]);

  // Output parameters of the currently selected step (from the databus, not from
  // outputParametersJson — the live view keeps the databus current via SignalR). MUST be
  // declared before the conditional early-return below: hook order has to stay stable across
  // all renders (otherwise "Rendered more hooks than during the previous render").
  const selected = execution.steps.find((s) => s.stepId === selectedStep);
  const selectedOutputParameters = useMemo(() => {
    if (!selected) return null;
    const prefix = `${selected.stepId}.param.`;
    const entries = Object.entries(execution.databus)
      .filter(([key, entry]) => key.startsWith(prefix) && entry.kind === 'param')
      .map(([key, entry]) => [entry.paramKey ?? key.slice(prefix.length), entry.value] as const);
    return entries.length > 0 ? Object.fromEntries(entries) : null;
  }, [execution.databus, selected]);

  // A debugger pause has top priority: if a step is currently stopped at a breakpoint,
  // we replace the entire live view with the inspector. Otherwise the user would have to find
  // the paused step in the sidebar first just to get to the resume buttons.
  const pausedStep = execution?.steps.find((s) => s.status === 'Paused');
  if (pausedStep && execution) {
    return (
      <PausedVariablesInspector
        stepName={pausedStep.stepName || pausedStep.stepId}
        stepId={pausedStep.stepId}
        pausedAt={pausedStep.pausedAt!}
        reason={pausedStep.pausedReason ?? 'breakpoint'}
        variables={pausedStep.pausedVariables ?? {}}
        onResume={async (mode, overrides) => {
          await api.post(`/executions/${execution.executionId}/resume`, {
            stepId: pausedStep.stepId,
            mode,
            overrides: Object.keys(overrides).length > 0 ? overrides : undefined,
          });
        }}
      />
    );
  }

  // A dry-run takes priority over the normal live view: if a simulation is active, we
  // show its result instead of the live execution list. This way the Live tab still serves
  // its purpose ("what's happening right now?") even in design mode — the user clicks
  // Simulate and immediately sees the step order + skipped branches here, without having to
  // wait for a test run.
  return (
    <div className="flex h-full">
      {/* Step timeline */}
      <div
        className="border-r border-outline-variant/10 overflow-y-auto bg-surface-low/50 shrink-0"
        style={{ width: stepList.size }}
      >
        <div className="p-1.5">
          {execution.steps.map((step, i) => {
            const execStartMs = new Date(execution.startedAt).getTime();
            const startMs = step.startedAt ? new Date(step.startedAt).getTime() : null;
            const endMs = step.completedAt ? new Date(step.completedAt).getTime() : null;
            const offsetMs = startMs != null ? startMs - execStartMs : null;
            const durationMs = startMs != null && endMs != null ? endMs - startMs : null;
            return (
              <button
                key={step.stepId}
                onClick={() => setSelectedStep(step.stepId)}
                className={`w-full flex items-center gap-2.5 px-3 py-0.5 rounded-md text-left transition-all ${
                  selectedStep === step.stepId
                    ? 'bg-primary-fixed shadow-sm'
                    : 'hover:bg-surface-high'
                }`}
              >
                <div className="relative">
                  <StepStatusIcon status={step.status} size={16} />
                  {i < execution.steps.length - 1 && (
                    <div className="absolute left-1/2 top-full w-px h-0.5 bg-outline-variant/30 -translate-x-1/2" />
                  )}
                </div>
                <div className="flex-1 min-w-0">
                  <div className="font-label text-xs font-semibold text-on-surface truncate leading-tight">
                    {step.stepName || step.stepId}
                  </div>
                  <div className="flex items-center gap-1.5 font-label text-[10px] text-on-surface-variant leading-tight">
                    <span className="truncate">{step.stepType}</span>
                    {offsetMs != null && (
                      <>
                        <span className="text-outline">·</span>
                        <span className="font-mono tabular-nums">+{formatMs(offsetMs)}</span>
                      </>
                    )}
                    {durationMs != null && (
                      <>
                        <span className="text-outline">·</span>
                        <span className="font-mono tabular-nums text-on-surface/70">{formatMs(durationMs)}</span>
                      </>
                    )}
                  </div>
                </div>
                {step.status === 'Running' && (
                  <CircleDash size={12} className="text-blue-500 animate-spin shrink-0" />
                )}
              </button>
            );
          })}
        </div>
      </div>
      {/* Drag handle — 4 px hit target, shows a highlight stripe on hover. Double-click
          resets to the default width via useResizable's onDoubleClick. */}
      <div
        {...stepList.handleProps}
        className="w-1 shrink-0 cursor-col-resize bg-transparent hover:bg-primary/30 transition-colors"
        title={t('execution.resize.stepList')}
      />
      {/* Detail view — Horizontal layout when step selected: Inspector (left) + Timeline (right).
          Inspector only visible when step is selected. Timeline is always visible and takes
          remaining space on the right. */}
      <div className="flex-1 overflow-hidden min-w-0 flex">
        {/* Left: Step Inspector (INPUT/OUTPUT) — only when step selected */}
        {selected && (
          <>
            <div className="shrink-0 overflow-y-auto p-4 space-y-3 border-r border-outline-variant/20 bg-surface-low/30" style={{ width: inspectorPanel.size }}>
              {/* Header — Step-Name + Type + Close-X */}
              <div className="flex items-start gap-2.5">
                <StepStatusIcon status={selected.status} size={18} />
                <div className="flex-1 min-w-0">
                  <span className="font-headline text-sm font-bold text-on-surface">
                    {selected.stepName || selected.stepId}
                  </span>
                  <span className="font-label text-xs text-on-surface-variant ml-2">{selected.stepType}</span>
                </div>
                <button
                  type="button"
                  onClick={() => setSelectedStep(null)}
                  className="text-on-surface-variant hover:text-on-surface transition-colors p-1 -m-1 rounded shrink-0"
                  aria-label={t('execution.inspector.closeStepInspector')}
                  title={t('execution.inspector.closeDetails')}
                >
                  <Close size={14} />
                </button>
              </div>

              {/* Input config */}
              <StepInputBlock workflowId={workflowId} stepId={selected.stepId} />

              {/* Output parameters */}
              <StepOutputParametersBlock parameters={selectedOutputParameters} />

              {/* Output */}
              {selected.output && (
                <OutputBlock label={t('execution.inspector.output')} variant="default">
                  {selected.output}
                </OutputBlock>
              )}

              {/* Error */}
              {selected.errorOutput && (
                <OutputBlock label={t('execution.inspector.error')} variant="error">
                  {selected.errorOutput}
                </OutputBlock>
              )}

              {/* Transcript */}
              {selected.traceOutput && (
                <OutputBlock label={t('execution.inspector.transcript')} variant="default">
                  {selected.traceOutput}
                </OutputBlock>
              )}

              {/* Timing */}
              {selected.startedAt && (
                <div className="flex items-center gap-4 text-[10px] font-label text-outline flex-wrap">
                  <span className="flex items-center gap-1" title={new Date(selected.startedAt).toISOString()}>
                    <Time size={10} /> {t('execution.inspector.started', { time: `${new Date(selected.startedAt).toLocaleTimeString(undefined, { hour12: false })}.${String(new Date(selected.startedAt).getMilliseconds()).padStart(3, '0')}` })}
                  </span>
                  {selected.completedAt && (
                    <span className="flex items-center gap-1" title={new Date(selected.completedAt).toISOString()}>
                      <Time size={10} /> {t('execution.inspector.ended', { time: `${new Date(selected.completedAt).toLocaleTimeString(undefined, { hour12: false })}.${String(new Date(selected.completedAt).getMilliseconds()).padStart(3, '0')}` })}
                    </span>
                  )}
                  {execution && (
                    <span className="font-mono tabular-nums">
                      {t('execution.inspector.fromRunStart', { offset: formatMs(new Date(selected.startedAt).getTime() - new Date(execution.startedAt).getTime()) })}
                    </span>
                  )}
                  {selected.completedAt && (
                    <span className="font-mono tabular-nums font-semibold text-on-surface">
                      {t('execution.inspector.duration', { duration: formatMs(new Date(selected.completedAt).getTime() - new Date(selected.startedAt).getTime()) })}
                    </span>
                  )}
                </div>
              )}
            </div>

            {/* Drag handle — resize inspector width */}
            <div
              {...inspectorPanel.handleProps}
              className="w-1 shrink-0 cursor-col-resize bg-transparent hover:bg-primary/30 transition-colors"
              title={t('execution.resize.panel')}
            />
          </>
        )}

        {/* Right: Timeline (takes remaining space) */}
        <div className="flex-1 overflow-hidden">
          <LiveOverview
            execution={execution}
            onSelectStep={setSelectedStep}
            medianRunDurationMs={medianRunDurationMs}
          />
        </div>
      </div>
    </div>
  );
}

/* ---- Simulation Tab ---- */

function SimulationTab({ simulation }: Readonly<{ simulation: SimulationSnapshot }>) {
  const { t } = useTranslation('designer');
  const total = simulation.order.length;
  const done = Math.min(simulation.revealIndex, total);
  const playing = done < total;

  return (
    <div className="h-full overflow-y-auto">
      <div className="px-4 py-2.5 bg-primary-fixed/25 border-b border-primary/25 flex items-center gap-2">
        <Chemistry size={14} className={`text-primary ${playing ? 'animate-pulse' : ''}`} />
        <span className="font-label text-sm font-semibold text-primary">
          {playing ? t('execution.simulation.simulating') : t('execution.simulation.simulatedPath')}
        </span>
        <span className="font-mono text-[10px] tabular-nums text-primary bg-surface-lowest/60 rounded-full px-2 py-0.5">
          {playing ? t('execution.simulation.stepProgress', { current: done + 1, total }) : t('execution.simulation.willRunSkipped', { willRun: total, skipped: simulation.skipped.length })}
        </span>
      </div>
      <div className="p-3 space-y-3">
        <div>
          <h3 className="font-label text-[10px] font-bold uppercase tracking-widest text-primary mb-1.5">
            {t('execution.simulation.executionOrder')}
          </h3>
          <ol className="space-y-0.5">
            {simulation.order.map((id, i) => {
              const state: 'ran' | 'running' | 'pending' =
                i < done ? 'ran' : i === done ? 'running' : 'pending';
              return (
                <li
                  key={id}
                  className={`flex items-center gap-2.5 px-2 py-1.5 rounded border transition-all ${
                    state === 'running' ? 'bg-amber-100 dark:bg-amber-900/30 border-amber-300 dark:border-amber-700/50 shadow-sm scale-[1.01]'
                    : state === 'ran' ? 'bg-primary/10 border-primary/20'
                    : 'bg-surface-low border-outline-variant/20 opacity-50'
                  }`}
                >
                  <span className={`font-mono text-[10px] tabular-nums w-5 text-right ${
                    state === 'running' ? 'text-amber-700 dark:text-amber-400 font-bold'
                    : state === 'ran' ? 'text-primary/80'
                    : 'text-outline'
                  }`}>{i + 1}</span>
                  {state === 'running' ? (
                    <CircleDash size={12} className="text-amber-600 dark:text-amber-400 animate-spin shrink-0" />
                  ) : state === 'ran' ? (
                    <CheckmarkFilled size={12} className="text-primary shrink-0" />
                  ) : (
                    <CircleStroke size={12} className="text-outline-variant shrink-0" />
                  )}
                  <ActivityTypeIcon type={simulation.nodeTypes[id] ?? 'runScript'} />
                  <span className="font-label text-xs font-semibold text-on-surface truncate flex-1">
                    {simulation.nodeLabels[id] ?? id}
                  </span>
                  <span className="font-mono text-[10px] text-on-surface-variant">{simulation.nodeTypes[id]}</span>
                </li>
              );
            })}
          </ol>
        </div>
        {!playing && simulation.skipped.length > 0 && (
          <div>
            <h3 className="font-label text-[10px] font-bold uppercase tracking-widest text-on-surface-variant mb-1.5">
              {t('execution.simulation.skipped', { count: simulation.skipped.length })}
            </h3>
            <ul className="space-y-0.5">
              {simulation.skipped.map((id) => (
                <li key={id} className="flex items-center gap-2.5 px-2 py-1.5 rounded bg-surface-low opacity-70">
                  <CircleStroke size={12} strokeDasharray="2 2" className="text-outline shrink-0" />
                  <span className="font-label text-xs text-on-surface line-through truncate flex-1">
                    {simulation.nodeLabels[id] ?? id}
                  </span>
                  <span className="font-mono text-[10px] text-outline">{simulation.nodeTypes[id]}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
        <p className="font-label text-[10px] text-on-surface-variant italic pt-1">
          {t('execution.simulation.note')}
        </p>
      </div>
    </div>
  );
}
