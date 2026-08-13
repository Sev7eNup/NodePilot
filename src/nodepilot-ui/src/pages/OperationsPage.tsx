import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { WarningAltFilled, Pause, Play } from '@carbon/icons-react';
import { STATUS_BADGE_CLASS } from '../lib/statusTokens';
import { OPS_WINDOW_MINUTES, type OpsWindowMinutes } from '../lib/opsTimeline';
import { formatTime } from '../lib/format';
import {
  getOperationsGraph, getOpsDashboardStats, cancelExecution,
  retryExecution, cancelAllForWorkflow, quarantineWorkflow,
} from '../api/operations';
import { confirmDialog } from '../stores/confirmStore';
import { toast } from '../stores/toastStore';
import { useOperationsFeed } from '../hooks/useOperationsFeed';
import { useOpsClock } from '../hooks/useOpsClock';
import { useOperationsStore } from '../stores/operationsStore';
import { OpsTimeline } from '../components/operations/OpsTimeline';
import { OpsDepartureBoard } from '../components/operations/OpsDepartureBoard';
import { OpsExecutionDrilldown } from '../components/operations/OpsExecutionDrilldown';
import { EmptyState } from '../components/common/EmptyState';

// Live-Ops Mission Control: the real-time execution timeline as the centerpiece (running +
// recently-finished bars, drill-down + cancel) and the next-fires departure board at the
// bottom. The snapshot poll (5 s) is the authoritative source; SignalR deltas make
// everything feel instant in between.

export function OperationsPage() {
  const { t } = useTranslation(['operations', 'executions', 'common']);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [selected, setSelected] = useState<string | null>(null);
  const [folderFilter, setFolderFilter] = useState<string | null>(null);
  // Window + freeze are view-local on purpose. A freeze that survived navigation would be a
  // footgun: come back an hour later and stare at an hour-old board believing it is live.
  const [windowMinutes, setWindowMinutes] = useState<OpsWindowMinutes>(OPS_WINDOW_MINUTES[0]);
  const [frozen, setFrozen] = useState(false);

  const liveNowMs = useOpsClock(1000, frozen);

  const { data: liveData, isLoading, isError } = useQuery({
    // Window is part of the key: switching creates a separate cache entry and the old query
    // loses its only observer, so it stops polling on its own.
    queryKey: ['operations-graph', windowMinutes],
    queryFn: () => getOperationsGraph(windowMinutes),
    refetchInterval: frozen ? false : 5_000,
    refetchOnWindowFocus: false,
  });

  // Slow-moving global stats: departure board, machines, heartbeats, queue counts.
  const { data: stats } = useQuery({
    queryKey: ['ops-dashboard'],
    queryFn: getOpsDashboardStats,
    refetchInterval: 30_000,
    refetchOnWindowFocus: false,
  });

  // NEVER conditional: the feed must keep writing terminal tombstones while frozen. Cutting it
  // would open a reconcile gap — a refetch after unfreezing could resurrect runs that finished
  // during the freeze, because the tombstone that prevents exactly that was never written.
  useOperationsFeed();
  const seedRunning = useOperationsStore((s) => s.seedRunning);
  const runningMap = useOperationsStore((s) => s.runningExecsByWorkflow);
  const liveLocallySettled = useOperationsStore((s) => s.locallySettled);

  // ---- Display freeze ------------------------------------------------------------------------
  // This freezes the RENDER INPUTS, not the data pipeline: the SignalR feed stays connected, the
  // store keeps reconciling, and background invalidations may still fire requests. Only what the
  // user looks at is held still — hence "view frozen", not "paused".
  const [frozenView, setFrozenView] = useState<
    { data: typeof liveData; locallySettled: typeof liveLocallySettled; nowMs: number } | null
  >(null);

  const toggleFreeze = () => {
    if (frozen) { setFrozenView(null); setFrozen(false); return; }
    setFrozenView({ data: liveData, locallySettled: liveLocallySettled, nowMs: liveNowMs });
    setFrozen(true);
  };

  const data = frozen && frozenView ? frozenView.data : liveData;
  const locallySettled = frozen && frozenView ? frozenView.locallySettled : liveLocallySettled;
  const nowMs = frozen && frozenView ? frozenView.nowMs : liveNowMs;

  // Seed the live store from the authoritative snapshot. `lastStatusByWf` drives the
  // race-safe reconcile of the terminal overlay; recent ids supersede the locally-settled
  // overlay entries (see operationsStore.seedRunning). Fed from liveData, never the frozen
  // copy — the store must stay current even while the view is held.
  useEffect(() => {
    if (!liveData) return;
    const lastStatusByWf: Record<string, string | null> = {};
    for (const n of liveData.nodes) lastStatusByWf[n.workflowId] = n.lastStatus;
    seedRunning(liveData.running, lastStatusByWf, new Set(liveData.recent.map((r) => r.executionId)));
  }, [liveData, seedRunning]);

  // Folder options derived from the snapshot (unique folderId → folderPath).
  const folderOptions = useMemo(() => {
    const map = new Map<string, string>();
    for (const n of data?.nodes ?? []) if (n.folderId) map.set(n.folderId, n.folderPath);
    return Array.from(map, ([folderId, folderPath]) => ({ folderId, folderPath }));
  }, [data]);

  // Auto-reset the folder filter if the chosen folder vanished from the snapshot (RBAC/scope).
  useEffect(() => {
    if (folderFilter && !folderOptions.some((f) => f.folderId === folderFilter)) setFolderFilter(null);
  }, [folderFilter, folderOptions]);

  // ---- Central scope rule: scopedNodes drives timeline, ticker, board and counts alike. ----
  const scopedNodes = useMemo(
    () => (folderFilter ? (data?.nodes ?? []).filter((n) => n.folderId === folderFilter) : (data?.nodes ?? [])),
    [data, folderFilter],
  );
  const scopedWorkflowIds = useMemo(
    () => new Set(scopedNodes.map((n) => n.workflowId)),
    [scopedNodes],
  );
  const nodesById = useMemo(
    () => new Map(scopedNodes.map((n) => [n.workflowId, n])),
    [scopedNodes],
  );

  const scopedRecent = useMemo(
    () => (data?.recent ?? []).filter((r) => scopedWorkflowIds.has(r.workflowId)),
    [data, scopedWorkflowIds],
  );
  const scopedTriggers = useMemo(
    () => (stats?.armedTriggers ?? []).filter((a) => scopedWorkflowIds.has(a.workflowId)),
    [stats, scopedWorkflowIds],
  );

  const nextStart = useMemo(() => {
    let best: { name: string; atMs: number } | null = null;
    for (const a of scopedTriggers) {
      if (!a.nextFireUtc) continue;
      const atMs = Date.parse(a.nextFireUtc);
      if (atMs < nowMs) continue;
      if (!best || atMs < best.atMs) best = { name: a.workflowName, atMs };
    }
    return best;
  }, [scopedTriggers, nowMs]);

  // ---- Incident actions --------------------------------------------------------------------
  // Cancel / Retry / Cancel-all / Quarantine. All four reuse existing endpoints; the gating
  // comes from the per-node canRun/canEdit flags in the snapshot, never from the global role.
  const invalidateGraph = () => queryClient.invalidateQueries({ queryKey: ['operations-graph'] }); // prefix match: all windows

  const cancel = useMutation({
    mutationFn: (executionId: string) => cancelExecution(executionId),
    onSuccess: invalidateGraph,
    onError: () => toast.error(t('operations:drilldown.actionFailed')),
  });

  const retry = useMutation({
    mutationFn: (executionId: string) => retryExecution(executionId),
    onSuccess: (_data, executionId) => {
      invalidateGraph();
      queryClient.invalidateQueries({ queryKey: ['ops-execution', executionId] });
      toast.success(t('operations:drilldown.retryStarted'));
    },
    onError: () => toast.error(t('operations:drilldown.actionFailed')),
  });

  const cancelAll = useMutation({
    mutationFn: (workflowId: string) => cancelAllForWorkflow(workflowId),
    onSuccess: (result) => {
      invalidateGraph();
      toast.success(t('operations:drilldown.cancelAllDone', { count: result.total }));
    },
    onError: () => toast.error(t('operations:drilldown.actionFailed')),
  });

  const quarantine = useMutation({
    mutationFn: (workflowId: string) => quarantineWorkflow(workflowId),
    onSuccess: (outcome) => {
      invalidateGraph();
      // The departure board is fed by /stats/dashboard, whose armedTriggers filter on
      // IsEnabled. Without this the board keeps promising a start for a workflow that was
      // just quarantined — for a full 30 s poll cycle.
      queryClient.invalidateQueries({ queryKey: ['ops-dashboard'] });
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      if (outcome.cancelled === null) {
        // Partial: the workflow is safely off but its runs are still going. Its own message,
        // because "failed" would be wrong and "done" would be a lie.
        toast.error(t('operations:drilldown.quarantinePartial'));
      } else {
        toast.success(t('operations:drilldown.quarantined', { count: outcome.cancelled.total }));
      }
    },
    onError: () => toast.error(t('operations:drilldown.actionFailed')),
  });

  const pendingAction = cancel.isPending ? 'cancel'
    : retry.isPending ? 'retry'
    : cancelAll.isPending ? 'cancelAll'
    : quarantine.isPending ? 'quarantine'
    : null;

  // ---- Drilldown context: resolve the selected execution from live store > recent list. ----
  // Deliberately not memoized: the page re-renders once per clock tick anyway and this is a
  // handful of map lookups.
  const selectedContext = (() => {
    if (!selected) return null;
    for (const [wfId, list] of Object.entries(runningMap)) {
      const live = list.find((e) => e.id === selected);
      if (live) {
        if (!scopedWorkflowIds.has(wfId)) return null;
        return { workflowId: wfId, status: live.status, startedAtMs: live.startedAtMs, completedAtMs: null };
      }
    }
    const settled = locallySettled[selected];
    if (settled && scopedWorkflowIds.has(settled.workflowId)) {
      return { workflowId: settled.workflowId, status: settled.status, startedAtMs: settled.startedAtMs, completedAtMs: settled.settledAtMs };
    }
    const recent = scopedRecent.find((r) => r.executionId === selected);
    if (recent) {
      return { workflowId: recent.workflowId, status: recent.status, startedAtMs: Date.parse(recent.startedAt), completedAtMs: Date.parse(recent.completedAt) };
    }
    return null;
  })();

  // Step activity of the selected run. Only the snapshot's `running` list carries it — the live
  // store holds no step data — so a run that has already settled resolves to null, which is
  // correct: activity is a live-run concept.
  const selectedActivity = useMemo(() => {
    if (!selected) return null;
    const row = (data?.running ?? []).find((r) => r.executionId === selected);
    if (!row) return null;
    return {
      stepsFinished: row.stepsFinished,
      lastCompletedStepName: row.lastCompletedStepName,
      lastProgressAtMs: row.lastProgressAt === null ? null : Date.parse(row.lastProgressAt),
    };
  }, [selected, data]);

  // Close the drilldown when the selected execution leaves the current scope/window.
  useEffect(() => {
    if (selected && !selectedContext) setSelected(null);
  }, [selected, selectedContext]);

  const selectedNode = selectedContext ? nodesById.get(selectedContext.workflowId) : undefined;

  // Static call topology for the drilldown: what the selected workflow's definition calls.
  // Resolved targets show their name; dynamic/unresolved refs show the raw reference string.
  const allNodesById = useMemo(
    () => new Map((data?.nodes ?? []).map((n) => [n.workflowId, n.name])),
    [data],
  );
  const selectedCallees = useMemo(() => {
    if (!selectedContext) return [];
    const names = new Set<string>();
    for (const e of data?.edges ?? []) {
      if (e.source !== selectedContext.workflowId) continue;
      if (e.target != null) {
        const name = allNodesById.get(e.target);
        if (name) names.add(name);
      } else if (e.rawRef) {
        names.add(e.rawRef);
      }
    }
    return [...names];
  }, [data, selectedContext, allNodesById]);

  return (
    <div className="np-ops flex h-[calc(100dvh-6rem)] flex-col gap-3">
      <header className="flex flex-wrap items-center justify-between gap-3 px-1">
        <div className="min-w-0">
          <h1 className="text-xl font-headline font-semibold text-on-surface">{t('operations:title')}</h1>
          <p className="text-sm text-on-surface-variant">{t('operations:subtitle')}</p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          {/* A frozen board must never pass for a dead system — loud badge, not a subtle hint. */}
          {frozen && (
            <span
              className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_BADGE_CLASS.warning}`}
              data-testid="ops-frozen-badge"
            >
              {t('operations:freeze.badge', {
                time: formatTime(nowMs, { hour: '2-digit', minute: '2-digit', second: '2-digit' }),
              })}
            </span>
          )}
          <button
            type="button"
            onClick={toggleFreeze}
            aria-pressed={frozen}
            className="flex items-center gap-1.5 rounded-lg border border-outline-variant px-2 py-1 text-xs text-on-surface hover:bg-surface-high"
          >
            {frozen ? <Play size={13} /> : <Pause size={13} />}
            {frozen ? t('operations:freeze.off') : t('operations:freeze.on')}
          </button>
          <label className="flex items-center gap-2 text-xs text-on-surface-variant">
            <span className="font-medium uppercase tracking-wide">{t('operations:window.label')}</span>
            <select
              value={windowMinutes}
              onChange={(e) => setWindowMinutes(Number(e.target.value) as OpsWindowMinutes)}
              aria-label={t('operations:window.label')}
              className="rounded-lg border border-outline-variant bg-surface-container px-2 py-1 text-xs text-on-surface focus:outline-none focus:ring-1 focus:ring-primary"
            >
              {OPS_WINDOW_MINUTES.map((m) => (
                <option key={m} value={m}>{t(`operations:window.option.${m}`)}</option>
              ))}
            </select>
          </label>
          <label className="flex items-center gap-2 text-xs text-on-surface-variant">
          <span className="font-medium uppercase tracking-wide">{t('operations:folderFilter.label')}</span>
          <select
            value={folderFilter ?? ''}
            onChange={(e) => setFolderFilter(e.target.value || null)}
            aria-label={t('operations:folderFilter.label')}
            className="max-w-[220px] rounded-lg border border-outline-variant bg-surface-container px-2 py-1 text-xs text-on-surface focus:outline-none focus:ring-1 focus:ring-primary"
          >
            <option value="">{t('operations:folderFilter.all')}</option>
            {folderOptions.map((f) => (
              <option key={f.folderId} value={f.folderId}>{f.folderPath}</option>
            ))}
          </select>
          </label>
        </div>
      </header>

      {/* Main stage: timeline + drilldown overlay */}
      <div className="relative min-h-0 flex-1 overflow-hidden rounded-2xl border border-outline-variant bg-surface p-3">
        {isLoading && (
          <div className="flex h-full items-center justify-center text-on-surface-variant">{t('common:loading')}</div>
        )}
        {isError && (
          <div className="flex h-full items-center justify-center text-error">{t('operations:error')}</div>
        )}
        {data && scopedNodes.length === 0 && (
          <EmptyState icon={<WarningAltFilled size={22} />} title={t('operations:empty')} />
        )}
        {data && scopedNodes.length > 0 && (
          <OpsTimeline
            nowMs={nowMs}
            running={data.running}
            recent={data.recent}
            density={data.density ?? []}
            locallySettled={locallySettled}
            scopedWorkflowIds={scopedWorkflowIds}
            nodesById={nodesById}
            selectedExecutionId={selected}
            nextStart={nextStart}
            overdueMs={(data.meta?.overdueSeconds ?? 600) * 1000}
            windowMs={windowMinutes * 60_000}
            historyFromMs={data.meta?.oldestReturnedCompletedAt ? Date.parse(data.meta.oldestReturnedCompletedAt) : null}
            recentSinceMs={data.meta?.recentSinceUtc ? Date.parse(data.meta.recentSinceUtc) : nowMs - windowMinutes * 60_000}
            densityBucketSeconds={data.meta?.densityBucketSeconds ?? 0}
            densityCapped={data.meta?.densityCapped ?? false}
            onSelect={setSelected}
          />
        )}

        {selected && selectedContext && selectedNode && (
          <OpsExecutionDrilldown
            executionId={selected}
            workflowName={selectedNode.name}
            folderPath={selectedNode.folderPath}
            callees={selectedCallees}
            status={selectedContext.status}
            startedAtMs={selectedContext.startedAtMs}
            completedAtMs={selectedContext.completedAtMs}
            nowMs={nowMs}
            canRun={selectedNode.canRun}
            canEdit={selectedNode.canEdit}
            workflowEnabled={selectedNode.isEnabled}
            runningCount={selectedNode.runningCount}
            activity={selectedActivity}
            pendingAction={pendingAction}
            onCancel={(id) => cancel.mutate(id)}
            onRetry={(id) => retry.mutate(id)}
            onCancelAll={async () => {
              if (await confirmDialog({
                message: t('operations:drilldown.cancelAllConfirm', { name: selectedNode.name, count: selectedNode.runningCount }),
                danger: true,
              })) cancelAll.mutate(selectedContext.workflowId);
            }}
            onQuarantine={async () => {
              if (await confirmDialog({
                message: t('operations:drilldown.quarantineConfirm', { name: selectedNode.name }),
                danger: true,
              })) quarantine.mutate(selectedContext.workflowId);
            }}
            onOpenEditor={() => navigate(`/workflows/${selectedContext.workflowId}`)}
            onSelectExecution={setSelected}
            onClose={() => setSelected(null)}
          />
        )}
      </div>

      <OpsDepartureBoard triggers={scopedTriggers} nowMs={nowMs} />
    </div>
  );
}
