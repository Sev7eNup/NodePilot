import { Box, Chip, Layers, Meter, Network_3 } from '@carbon/icons-react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { adminSettings, type EffectiveSizing } from '../../api/adminSettings';
import {
  useSectionForm,
  Card,
  HotReloadHint,
  Toggle,
  NumberInput,
  ErrorsAndSave,
} from './SectionFormHelpers';

/**
 * Performance tuning tab. The mode card on top decides whether the cards below apply at all:
 * with automatic sizing (the default) NodePilot derives the numbers from the detected CPU and
 * memory, and the configured values sit inert as a preset. Everything here is strict-startup —
 * the runspace pool and the dispatch queue are built once at boot — so a save shows the restart
 * banner. Threading alone re-applies live, but only while manual tuning is on.
 */
export function PerformanceSection() {
  const { data: sizing } = useQuery({
    queryKey: ['admin-settings', 'effective-sizing'],
    queryFn: () => adminSettings.getEffectiveSizing(),
  });
  // Until the plan is known, treat the cards as editable rather than flashing them disabled.
  const manual = sizing?.manualTuning ?? true;

  return (
    <div className="space-y-4">
      <PerformanceModeCard sizing={sizing} />
      <EngineCard manual={manual} sizing={sizing} />
      <ExecutionDispatchCard manual={manual} sizing={sizing} />
      <ThreadingCard manual={manual} sizing={sizing} />
      <RemoteCard />
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Sizing mode
// ─────────────────────────────────────────────────────────────────────────────

function PerformanceModeCard({ sizing }: Readonly<{ sizing?: EffectiveSizing }>) {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<{ manualTuning: boolean }>('Performance', { manualTuning: false });
  if (ui.loading) return <Card icon={Meter} title={t('perf.modeCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;

  const pendingRestart = sizing !== undefined && sizing.manualTuning !== sizing.desiredManualTuning;

  return (
    <Card icon={Meter} title={t('perf.modeCardTitle')}>
      <p className="text-sm text-on-surface-variant mb-2">{t('perf.modeExplainer')}</p>
      <Toggle label={t('perf.manualTuning')} checked={form.manualTuning}
        onChange={(v) => set({ manualTuning: v })}
        configKey="Performance:ManualTuning" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
      {pendingRestart && (
        <p className="text-xs mt-2 text-amber-600 dark:text-amber-400">{t('perf.modePendingRestart')}</p>
      )}
      {sizing && <DetectedHardware sizing={sizing} />}
      <ErrorsAndSave errors={errors} onSave={() => save({ ManualTuning: form.manualTuning })} />
      {ui.dialog}
    </Card>
  );
}

function DetectedHardware({ sizing }: Readonly<{ sizing: EffectiveSizing }>) {
  const { t } = useTranslation('adminSettings');
  const memory = sizing.usableMemoryBytes === null
    ? t('perf.memoryUnknown')
    : `${(sizing.usableMemoryBytes / 1024 ** 3).toFixed(1)} GB`;

  return (
    <p className="text-xs text-on-surface-variant mt-3">
      {t('perf.detected', {
        cores: sizing.processorCount,
        memory,
        posture: sizing.isDesktop ? 'Desktop' : 'Server',
      })}
    </p>
  );
}

/**
 * Under automatic sizing the stored numbers are not what the process runs on, so showing them
 * unannotated would be a lie. The effective value and the constraint that produced it are
 * appended to each field's hint instead.
 */
function effectiveHint(
  sizing: EffectiveSizing | undefined,
  configKey: string,
  t: (k: string, o?: Record<string, unknown>) => string,
  base?: string,
): string | undefined {
  if (!sizing || sizing.manualTuning) return base;
  const entry = sizing.values.find((v) => v.key === configKey);
  if (!entry) return base;
  const active = t('perf.activeValue', { value: entry.value, bound: t(`perf.bound.${entry.bound}`) });
  return base ? `${active} — ${base}` : active;
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

function EngineCard({ manual, sizing }: Readonly<{ manual: boolean; sizing?: EffectiveSizing }>) {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<EngineDto>('Engine', {
    debug: { maxPauseMinutes: 10 },
    maxConcurrentExecutions: { global: 5000, perUser: 2000 },
    maxConcurrentSteps: 600,
    runspace: { minRunspaces: 256, maxRunspaces: 768 },
  });
  if (ui.loading) return <Card icon={Chip} title={t('perf.engineCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, save, errors } = ui;
  // Reuse the env-lock path to grey out the fields the sizing plan governs: same disabled
  // styling, and under automatic sizing those values genuinely are not in force. Two fields are
  // exempt because the plan does not cover them — the debug pause, and MaxConcurrentExecutions,
  // which is a safety cap against trigger loops rather than a performance knob and therefore
  // stays configuration-driven in both modes.
  const planGoverns = (k: string) =>
    k.startsWith('Engine:Runspace:') || k === 'Engine:MaxConcurrentSteps';
  const isEnvLocked = (k: string) => ui.isEnvLocked(k) || (!manual && planGoverns(k));

  return (
    <Card icon={Chip} title={t('perf.engineCardTitle')}>
      {!manual && <p className="text-xs text-on-surface-variant mb-2">{t('perf.inertUnderAuto')}</p>}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <NumberInput label={t('perf.debugMaxPause')} value={form.debug.maxPauseMinutes} min={1} max={1440}
          onChange={(v) => set({ ...form, debug: { maxPauseMinutes: v } })}
          configKey="Engine:Debug:MaxPauseMinutes" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <NumberInput label={t('perf.maxConcurrentStepsGlobal')} value={form.maxConcurrentSteps} min={1} max={10000}
          onChange={(v) => set({ ...form, maxConcurrentSteps: v })}
          configKey="Engine:MaxConcurrentSteps" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          hint={effectiveHint(sizing, 'Engine:MaxConcurrentSteps', t,
            'Sollte ≈ ExecutionDispatch.WorkerCount sein. Deutlich höhere Werte produzieren SignalR-Event-Drops im Live-Tab (siehe docs/performance-improvements.md).')} />
        <NumberInput label={t('perf.maxConcurrentExecutionsGlobal')} value={form.maxConcurrentExecutions.global} min={1} max={100000}
          onChange={(v) => set({ ...form, maxConcurrentExecutions: { ...form.maxConcurrentExecutions, global: v } })}
          configKey="Engine:MaxConcurrentExecutions:Global" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          hint={t('perf.safetyCapHint')} />
        <NumberInput label={t('perf.maxConcurrentExecutionsPerUser')} value={form.maxConcurrentExecutions.perUser} min={1} max={100000}
          onChange={(v) => set({ ...form, maxConcurrentExecutions: { ...form.maxConcurrentExecutions, perUser: v } })}
          configKey="Engine:MaxConcurrentExecutions:PerUser" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          hint={t('perf.safetyCapHint')} />
        <NumberInput label={t('perf.runspacesMin')} value={form.runspace.minRunspaces} min={1} max={10000}
          onChange={(v) => set({ ...form, runspace: { ...form.runspace, minRunspaces: v } })}
          configKey="Engine:Runspace:MinRunspaces" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          hint={effectiveHint(sizing, 'Engine:Runspace:MinRunspaces', t,
            'Sweet-Spot für das 500-Workflow-Profil ist 256. Der Pool wächst on-demand bis MaxRunspaces; ein hoher Min-Wert bringt nichts (Eager-Pre-Warm mit 768 maß 28 % Regression).')} />
        <NumberInput label={t('perf.runspacesMax')} value={form.runspace.maxRunspaces} min={1} max={10000}
          onChange={(v) => set({ ...form, runspace: { ...form.runspace, maxRunspaces: v } })}
          configKey="Engine:Runspace:MaxRunspaces" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          hint={effectiveHint(sizing, 'Engine:Runspace:MaxRunspaces', t,
            'Sweet-Spot ist 768 (validiert für 500 parallele Workflows).')} />
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

function ExecutionDispatchCard({ manual, sizing }: Readonly<{ manual: boolean; sizing?: EffectiveSizing }>) {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<{ capacity: number; workerCount: number }>('ExecutionDispatch', { capacity: 2048, workerCount: 600 });
  if (ui.loading) return <Card icon={Layers} title={t('perf.executionDispatchCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, save, errors } = ui;
  const isEnvLocked = (k: string) => ui.isEnvLocked(k) || !manual;
  return (
    <Card icon={Layers} title={t('perf.executionDispatchCardTitle')}>
      {!manual && <p className="text-xs text-on-surface-variant mb-2">{t('perf.inertUnderAuto')}</p>}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <NumberInput label={t('perf.queueCapacity')} value={form.capacity} min={1} max={100000}
          onChange={(v) => set({ ...form, capacity: v })}
          configKey="ExecutionDispatch:Capacity" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          hint={effectiveHint(sizing, 'ExecutionDispatch:Capacity', t)} />
        <NumberInput label={t('perf.workerCount')} value={form.workerCount} min={1} max={10000}
          onChange={(v) => set({ ...form, workerCount: v })}
          configKey="ExecutionDispatch:WorkerCount" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          hint={effectiveHint(sizing, 'ExecutionDispatch:WorkerCount', t)} />
      </div>
      <ErrorsAndSave errors={errors} onSave={() => save({ Capacity: form.capacity, WorkerCount: form.workerCount })} />
      {ui.dialog}
    </Card>
  );
}

function ThreadingCard({ manual, sizing }: Readonly<{ manual: boolean; sizing?: EffectiveSizing }>) {
  const { t } = useTranslation('adminSettings');
  const ui = useSectionForm<{ minWorkerThreads: number; minIoCompletionThreads: number }>('Threading', { minWorkerThreads: 768, minIoCompletionThreads: 768 });
  if (ui.loading) return <Card icon={Box} title={t('perf.threadingCardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, save, errors } = ui;
  const isEnvLocked = (k: string) => ui.isEnvLocked(k) || !manual;
  return (
    <Card icon={Box} title={t('perf.threadingCardTitle')}>
      {/* The ThreadPool floor is the one sizing knob that can be re-applied without a restart —
          but only while manual tuning is on, because under automatic sizing the live values come
          from the boot plan and a reload must not drag the pool into a different mode. */}
      <HotReloadHint isHotReloadable={data.isHotReloadable && manual} />
      {!manual && <p className="text-xs text-on-surface-variant mb-2">{t('perf.inertUnderAuto')}</p>}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <NumberInput label={t('perf.minWorkerThreads')} value={form.minWorkerThreads} min={1} max={10000}
          onChange={(v) => set({ ...form, minWorkerThreads: v })}
          configKey="Threading:MinWorkerThreads" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          hint={effectiveHint(sizing, 'Threading:MinWorkerThreads', t)} />
        <NumberInput label={t('perf.minIoCompletionThreads')} value={form.minIoCompletionThreads} min={1} max={10000}
          onChange={(v) => set({ ...form, minIoCompletionThreads: v })}
          configKey="Threading:MinIoCompletionThreads" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
          hint={effectiveHint(sizing, 'Threading:MinIoCompletionThreads', t)} />
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
