import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { WarningAltFilled } from '@carbon/icons-react';
import { getOperationsGraph, getOpsDashboardStats, cancelExecution } from '../api/operations';
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
  const nowMs = useOpsClock();

  const { data, isLoading, isError } = useQuery({
    queryKey: ['operations-graph'],
    queryFn: getOperationsGraph,
    refetchInterval: 5_000,
    refetchOnWindowFocus: false,
  });

  // Slow-moving global stats: departure board, machines, heartbeats, queue counts.
  const { data: stats } = useQuery({
    queryKey: ['ops-dashboard'],
    queryFn: getOpsDashboardStats,
    refetchInterval: 30_000,
    refetchOnWindowFocus: false,
  });

  useOperationsFeed();
  const seedRunning = useOperationsStore((s) => s.seedRunning);
  const runningMap = useOperationsStore((s) => s.runningExecsByWorkflow);
  const locallySettled = useOperationsStore((s) => s.locallySettled);

  // Seed the live store from the authoritative snapshot. `lastStatusByWf` drives the
  // race-safe reconcile of the terminal overlay; recent ids supersede the locally-settled
  // overlay entries (see operationsStore.seedRunning).
  useEffect(() => {
    if (!data) return;
    const lastStatusByWf: Record<string, string | null> = {};
    for (const n of data.nodes) lastStatusByWf[n.workflowId] = n.lastStatus;
    seedRunning(data.running, lastStatusByWf, new Set(data.recent.map((r) => r.executionId)));
  }, [data, seedRunning]);

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

  const cancel = useMutation({
    mutationFn: (executionId: string) => cancelExecution(executionId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['operations-graph'] }),
  });

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
        <label className="flex items-center gap-2 text-xs text-on-surface-variant">
          <span className="font-medium uppercase tracking-wide">{t('operations:folderFilter.label')}</span>
          <select
            value={folderFilter ?? ''}
            onChange={(e) => setFolderFilter(e.target.value || null)}
            className="max-w-[220px] rounded-lg border border-outline-variant bg-surface-container px-2 py-1 text-xs text-on-surface focus:outline-none focus:ring-1 focus:ring-primary"
          >
            <option value="">{t('operations:folderFilter.all')}</option>
            {folderOptions.map((f) => (
              <option key={f.folderId} value={f.folderId}>{f.folderPath}</option>
            ))}
          </select>
        </label>
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
            locallySettled={locallySettled}
            scopedWorkflowIds={scopedWorkflowIds}
            nodesById={nodesById}
            selectedExecutionId={selected}
            nextStart={nextStart}
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
            canCancel={data?.capabilities.canCancel ?? false}
            cancelPending={cancel.isPending}
            onCancel={(id) => cancel.mutate(id)}
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
