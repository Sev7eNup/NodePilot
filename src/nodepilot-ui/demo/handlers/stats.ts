/**
 * Aggregate endpoints. Everything here is recomputed from the world on each request rather
 * than cached, so a run that just finished is reflected immediately — which matters because
 * the live feed invalidates these query keys the moment an execution changes state.
 */
import { route, type Route } from '../net/router';
import { json } from '../net/respond';
import { getWorld } from '../state/world';
import { buildDashboard, buildSidebarCounts } from '../seed/dashboard';

const HOUR = 60 * 60_000;

export const statsRoutes: Route[] = [
  route('GET', '/stats/dashboard', (ctx) => {
    const windowHours = Number(ctx.query.get('windowHours') ?? 24);
    return json(buildDashboard(getWorld(), windowHours, Date.now()));
  }),

  route('GET', '/stats/sidebar-counts', () => json(buildSidebarCounts(getWorld()))),

  route('GET', '/stats/duration-trend', (ctx) => {
    const windowHours = Number(ctx.query.get('windowHours') ?? 24);
    const workflowId = ctx.query.get('workflowId');
    const world = getWorld();
    const since = Date.now() - windowHours * HOUR;
    const runs = world.executions.filter(
      (e) => e.completedAt && Date.parse(e.startedAt) >= since && (!workflowId || e.workflowId === workflowId),
    );
    const bucketCount = Math.min(24, Math.max(6, windowHours));
    const width = (windowHours * HOUR) / bucketCount;
    const buckets = Array.from({ length: bucketCount }, (_, i) => {
      const from = since + i * width;
      const slice = runs.filter((e) => {
        const at = Date.parse(e.startedAt);
        return at >= from && at < from + width;
      });
      const durations = slice.map((e) => Date.parse(e.completedAt!) - Date.parse(e.startedAt)).sort((a, b) => a - b);
      const at = (fraction: number) =>
        durations.length === 0 ? null : durations[Math.min(durations.length - 1, Math.floor(fraction * durations.length))];
      return {
        startedAt: new Date(from).toISOString(),
        count: slice.length,
        medianMs: at(0.5),
        p95Ms: at(0.95),
      };
    });
    return json({
      buckets,
      workflows: world.workflows.map((w) => ({ id: w.id, name: w.name })),
    });
  }),

  route('GET', '/stats/failure-causes', (ctx) => {
    const windowHours = Number(ctx.query.get('windowHours') ?? 24);
    const since = Date.now() - windowHours * HOUR;
    const failed = getWorld().executions.filter((e) => e.status === 'Failed' && Date.parse(e.startedAt) >= since);

    // Grouped by message, each carrying its newest run: the card links to that execution and
    // renders its start time, so both fields have to be there.
    const byMessage = new Map<string, { message: string | null; count: number; latestExecutionId: string; latestStartedAt: string }>();
    for (const execution of failed) {
      const key = execution.errorMessage ?? 'Unknown error';
      const existing = byMessage.get(key);
      if (!existing) {
        byMessage.set(key, {
          message: execution.errorMessage,
          count: 1,
          latestExecutionId: execution.id,
          latestStartedAt: execution.startedAt,
        });
        continue;
      }
      existing.count += 1;
      if (Date.parse(execution.startedAt) > Date.parse(existing.latestStartedAt)) {
        existing.latestExecutionId = execution.id;
        existing.latestStartedAt = execution.startedAt;
      }
    }
    const ranked = [...byMessage.values()].sort((a, b) => b.count - a.count);
    const groups = ranked.slice(0, 5);
    return json({
      totalFailed: failed.length,
      groups,
      remainingCount: ranked.slice(5).reduce((sum, g) => sum + g.count, 0),
    });
  }),

  // Live-Ops snapshot. Every identifier here is keyed the way `OperationsGraph` declares it —
  // `workflowId` and `executionId`, not `id`. The timeline filters on those names, so a row
  // with the wrong key is silently dropped and the console looks empty.
  route('GET', '/operations/graph', (ctx) => {
    const requested = Number(ctx.query.get('windowMinutes') ?? 60);
    const windowMinutes = requested <= 30 ? 30 : 60;
    const world = getWorld();
    const sinceMs = Date.now() - windowMinutes * 60_000;
    const recentSinceUtc = new Date(sinceMs).toISOString();
    const inWindow = world.executions.filter((e) => Date.parse(e.startedAt) >= sinceMs);
    const settled = inWindow.filter((e) => e.completedAt);

    const stepsOfRun = (executionId: string) => world.steps.get(executionId) ?? [];

    return json({
      nodes: world.workflows.map((workflow) => {
        const runs = inWindow.filter((e) => e.workflowId === workflow.id);
        return {
          workflowId: workflow.id,
          name: workflow.name,
          folderId: workflow.folderId ?? '',
          folderPath: workflow.folderPath ?? '/',
          isEnabled: workflow.isEnabled,
          runningCount: runs.filter((e) => e.status === 'Running').length,
          lastStatus: runs[0]?.status ?? workflow.lastExecution?.status ?? null,
          callFrequency: null,
          canRun: true,
          canEdit: true,
        };
      }),
      edges: [],
      running: inWindow
        .filter((e) => e.status === 'Running' || e.status === 'Paused')
        .map((execution) => {
          const steps = stepsOfRun(execution.id);
          const done = steps.filter((s) => s.completedAt);
          const last = done.at(-1);
          return {
            executionId: execution.id,
            workflowId: execution.workflowId,
            status: execution.status,
            startedAt: execution.startedAt,
            parentExecutionId: execution.parentExecutionId ?? null,
            stepsFinished: done.length,
            lastCompletedStepName: last?.stepName ?? null,
            lastProgressAt: last?.completedAt ?? null,
            activeStepCount: steps.filter((s) => s.status === 'Running').length,
          };
        }),
      recent: settled.slice(0, 200).map((execution) => ({
        executionId: execution.id,
        workflowId: execution.workflowId,
        status: execution.status,
        startedAt: execution.startedAt,
        completedAt: execution.completedAt!,
        parentExecutionId: execution.parentExecutionId ?? null,
      })),
      // Nothing is truncated at this size, so there is no aggregate band to draw.
      density: [],
      meta: {
        overdueSeconds: 900,
        windowMinutes,
        recentSinceUtc,
        oldestReturnedCompletedAt: settled.at(-1)?.completedAt ?? null,
        recentTruncated: false,
        densityBucketSeconds: 0,
        densityCapped: false,
      },
    });
  }),
];
