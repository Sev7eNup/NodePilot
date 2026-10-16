import { useTranslation } from 'react-i18next';
import { Chemistry, Close } from '@carbon/icons-react';
import type { SimulationResult } from '../../../hooks/useWorkflowSimulation';

/** Progress pill of the dry-run simulation, centred over the canvas. */
export function SimulationBanner({ simulation, revealIndex, onClear }: Readonly<{
  simulation: SimulationResult;
  revealIndex: number;
  onClear: () => void;
}>) {
  const { t } = useTranslation(['editor']);
  const total = simulation.order.length;
  const done = Math.min(revealIndex, total);
  const playing = done < total;
  return (
    <div className="absolute top-4 left-1/2 -translate-x-1/2 z-30 flex items-center gap-3 px-4 py-2 rounded-full bg-indigo-600 text-white shadow-xl border border-indigo-400/50">
      <Chemistry size={14} className={`shrink-0 ${playing ? 'animate-pulse' : ''}`} />
      <span className="font-label text-sm font-semibold">
        {playing ? t('editor:simulation.simulating') : t('editor:simulation.complete')}
      </span>
      {playing ? (
        <>
          <span className="font-mono text-xs tabular-nums bg-white/20 rounded-full px-2 py-0.5">
            {t('editor:simulation.step', { n: done + 1, total })}
          </span>
          <div className="w-32 h-1.5 rounded-full bg-white/20 overflow-hidden">
            <div
              className="h-full bg-amber-300 transition-all duration-200"
              style={{ width: `${((done + 1) / total) * 100}%` }}
            />
          </div>
        </>
      ) : (
        <span className="font-mono text-xs tabular-nums bg-white/20 rounded-full px-2 py-0.5">
          {t('editor:simulation.willRun', { count: simulation.reachable.size })} · {t('editor:simulation.skipped', { count: simulation.skipped.size })}
        </span>
      )}
      <button
        onClick={onClear}
        className="ml-1 p-1 rounded-full hover:bg-white/20 transition-colors"
        title={t('editor:simulationClear')}
      >
        <Close size={14} />
      </button>
    </div>
  );
}
