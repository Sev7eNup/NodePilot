import { Box, Chip, Layers, Network_3 } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import {
  useSectionForm,
  Card,
  HotReloadHint,
  Toggle,
  NumberInput,
  ErrorsAndSave,
} from './SectionFormHelpers';

/**
 * Performance tuning tab. Four cards: Engine concurrency, Execution Dispatch queue,
 * ThreadPool pre-warming, Remote (combined RequireWinRmSsl + WinRm + Pool). Engine /
 * ExecutionDispatch / Remote are strict-startup — every save triggers the orange restart
 * banner. Threading is hot-reloadable (ThreadPoolTuningService re-applies on config reload).
 */
export function PerformanceSection() {
  return (
    <div className="space-y-4">
      <EngineCard />
      <ExecutionDispatchCard />
      <ThreadingCard />
      <RemoteCard />
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Engine
// ─────────────────────────────────────────────────────────────────────────────

type EngineDto = {
  debug: { maxPauseMinutes: number };
  maxConcurrentExecutions: { global: number; perUser: number };
  maxConcurrentSteps: number;
  runspace: { minRunspaces: number; maxRunspaces: number };
};

function EngineCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<EngineDto>('Engine', {
    debug: { maxPauseMinutes: 10 },
    maxConcurrentExecutions: { global: 5000, perUser: 2000 },
    maxConcurrentSteps: 600,
    runspace: { minRunspaces: 256, maxRunspaces: 768 },
  });
  if (ui.loading) return <Card icon={Chip} title={t('perf.engineCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;

  return (
    <Card icon={Chip} title={t('perf.engineCardTitle')}>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <NumberInput label={t('perf.debugMaxPause')} value={form.debug.maxPauseMinutes} min={1} max={1440}
          onChange={(v) => set({ ...form, debug: { maxPauseMinutes: v } })}
          configKey="Engine:Debug:MaxPauseMinutes" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('perf.maxConcurrentStepsGlobal')} value={form.maxConcurrentSteps} min={1} max={10000}
          onChange={(v) => set({ ...form, maxConcurrentSteps: v })}
          configKey="Engine:MaxConcurrentSteps" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          hint="Sollte ≈ ExecutionDispatch.WorkerCount sein. Deutlich höhere Werte produzieren SignalR-Event-Drops im Live-Tab (siehe docs/performance-improvements.md)." />
        <NumberInput label={t('perf.maxConcurrentExecutionsGlobal')} value={form.maxConcurrentExecutions.global} min={1} max={100000}
          onChange={(v) => set({ ...form, maxConcurrentExecutions: { ...form.maxConcurrentExecutions, global: v } })}
          configKey="Engine:MaxConcurrentExecutions:Global" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('perf.maxConcurrentExecutionsPerUser')} value={form.maxConcurrentExecutions.perUser} min={1} max={100000}
          onChange={(v) => set({ ...form, maxConcurrentExecutions: { ...form.maxConcurrentExecutions, perUser: v } })}
          configKey="Engine:MaxConcurrentExecutions:PerUser" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('perf.runspacesMin')} value={form.runspace.minRunspaces} min={1} max={10000}
          onChange={(v) => set({ ...form, runspace: { ...form.runspace, minRunspaces: v } })}
          configKey="Engine:Runspace:MinRunspaces" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          hint="Sweet-Spot ist 256. Werte > ~400 wurden getestet schlechter (OS-Thread-Overhead + Gen2-GC-Pressure > Cold-Start-Win). Pool wächst on-demand bis MaxRunspaces." />
        <NumberInput label={t('perf.runspacesMax')} value={form.runspace.maxRunspaces} min={1} max={10000}
          onChange={(v) => set({ ...form, runspace: { ...form.runspace, maxRunspaces: v } })}
          configKey="Engine:Runspace:MaxRunspaces" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          hint="Sweet-Spot ist 768 (validiert für 500 parallele Workflows). ~30 MB RAM pro Runspace — bei 768 voll ausgelastet ~15 GB Working Set." />
      </div>
      <ErrorsAndSave errors={errors} onSave={() => save({
        Debug: { MaxPauseMinutes: form.debug.maxPauseMinutes },
        MaxConcurrentExecutions: { Global: form.maxConcurrentExecutions.global, PerUser: form.maxConcurrentExecutions.perUser },
        MaxConcurrentSteps: form.maxConcurrentSteps,
        Runspace: { MinRunspaces: form.runspace.minRunspaces, MaxRunspaces: form.runspace.maxRunspaces },
      })} />
      {ui.dialog}
    </Card>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// ExecutionDispatch + Threading + Remote
// ─────────────────────────────────────────────────────────────────────────────

function ExecutionDispatchCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<{ capacity: number; workerCount: number }>('ExecutionDispatch', { capacity: 2048, workerCount: 600 });
  if (ui.loading) return <Card icon={Layers} title={t('perf.executionDispatchCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;
  return (
    <Card icon={Layers} title={t('perf.executionDispatchCardTitle')}>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <NumberInput label={t('perf.queueCapacity')} value={form.capacity} min={1} max={100000}
          onChange={(v) => set({ ...form, capacity: v })}
          configKey="ExecutionDispatch:Capacity" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('perf.workerCount')} value={form.workerCount} min={1} max={10000}
          onChange={(v) => set({ ...form, workerCount: v })}
          configKey="ExecutionDispatch:WorkerCount" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>
      <ErrorsAndSave errors={errors} onSave={() => save({ Capacity: form.capacity, WorkerCount: form.workerCount })} />
      {ui.dialog}
    </Card>
  );
}

function ThreadingCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<{ minWorkerThreads: number; minIoCompletionThreads: number }>('Threading', { minWorkerThreads: 768, minIoCompletionThreads: 768 });
  if (ui.loading) return <Card icon={Box} title={t('perf.threadingCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;
  return (
    <Card icon={Box} title={t('perf.threadingCardTitle')}>
      <HotReloadHint isHotReloadable={data.isHotReloadable} />
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <NumberInput label={t('perf.minWorkerThreads')} value={form.minWorkerThreads} min={1} max={10000}
          onChange={(v) => set({ ...form, minWorkerThreads: v })}
          configKey="Threading:MinWorkerThreads" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('perf.minIoCompletionThreads')} value={form.minIoCompletionThreads} min={1} max={10000}
          onChange={(v) => set({ ...form, minIoCompletionThreads: v })}
          configKey="Threading:MinIoCompletionThreads" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>
      <ErrorsAndSave errors={errors} onSave={() => save({ MinWorkerThreads: form.minWorkerThreads, MinIoCompletionThreads: form.minIoCompletionThreads })} />
      {ui.dialog}
    </Card>
  );
}

type RemoteDto = {
  requireWinRmSsl: boolean;
  winRm: { operationTimeoutSeconds: number; openTimeoutSeconds: number };
  pool: { enabled: boolean; maxConcurrentPerMachine: number; maxIdlePerKey: number; idleTtlSeconds: number };
};

function RemoteCard() {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<RemoteDto>('Remote', {
    requireWinRmSsl: true,
    winRm: { operationTimeoutSeconds: 300, openTimeoutSeconds: 30 },
    pool: { enabled: true, maxConcurrentPerMachine: 5, maxIdlePerKey: 5, idleTtlSeconds: 120 },
  });
  if (ui.loading) return <Card icon={Network_3} title={t('perf.remoteCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;

  return (
    <Card icon={Network_3} title={t('perf.remoteCardTitle')}>
      <Toggle label={t('perf.requireWinRmSsl')} checked={form.requireWinRmSsl}
        onChange={(v) => set({ ...form, requireWinRmSsl: v })}
        configKey="Remote:RequireWinRmSsl" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      <h4 className="font-medium text-sm mt-4 mb-2">{t('perf.winRmTimeouts')}</h4>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <NumberInput label={t('perf.operationTimeout')} value={form.winRm.operationTimeoutSeconds} min={1} max={3600}
          onChange={(v) => set({ ...form, winRm: { ...form.winRm, operationTimeoutSeconds: v } })}
          configKey="Remote:WinRm:OperationTimeoutSeconds" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('perf.openTimeout')} value={form.winRm.openTimeoutSeconds} min={1} max={600}
          onChange={(v) => set({ ...form, winRm: { ...form.winRm, openTimeoutSeconds: v } })}
          configKey="Remote:WinRm:OpenTimeoutSeconds" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>
      <h4 className="font-medium text-sm mt-4 mb-2">{t('perf.sessionPool')}</h4>
      <Toggle label={t('perf.poolEnabled')} checked={form.pool.enabled}
        onChange={(v) => set({ ...form, pool: { ...form.pool, enabled: v } })}
        configKey="Remote:Pool:Enabled" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      <div className="grid grid-cols-1 md:grid-cols-3 gap-3 mt-2">
        <NumberInput label={t('perf.maxConcurrentPerMachine')} value={form.pool.maxConcurrentPerMachine} min={1} max={1000}
          onChange={(v) => set({ ...form, pool: { ...form.pool, maxConcurrentPerMachine: v } })}
          configKey="Remote:Pool:MaxConcurrentPerMachine" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('perf.maxIdlePerKey')} value={form.pool.maxIdlePerKey} min={1} max={1000}
          onChange={(v) => set({ ...form, pool: { ...form.pool, maxIdlePerKey: v } })}
          configKey="Remote:Pool:MaxIdlePerKey" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('perf.idleTtl')} value={form.pool.idleTtlSeconds} min={1} max={3600}
          onChange={(v) => set({ ...form, pool: { ...form.pool, idleTtlSeconds: v } })}
          configKey="Remote:Pool:IdleTtlSeconds" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      </div>
      <ErrorsAndSave errors={errors} onSave={() => save({
        RequireWinRmSsl: form.requireWinRmSsl,
        WinRm: { OperationTimeoutSeconds: form.winRm.operationTimeoutSeconds, OpenTimeoutSeconds: form.winRm.openTimeoutSeconds },
        Pool: {
          Enabled: form.pool.enabled,
          MaxConcurrentPerMachine: form.pool.maxConcurrentPerMachine,
          MaxIdlePerKey: form.pool.maxIdlePerKey,
          IdleTtlSeconds: form.pool.idleTtlSeconds,
        },
      })} />
      {ui.dialog}
    </Card>
  );
}
