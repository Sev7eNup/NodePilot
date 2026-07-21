import { useTranslation } from 'react-i18next';
import { useDesignStore } from '../../../stores/designStore';
import { OVERLAY_DEFS } from './overlayDefs';

/**
 * Per-overlay ACTIVE tint (keyed by testId) — the classic toolbar kept a distinct hue per
 * overlay (machine/data-flow = primary, failure = red, coverage = amber, critical-path = orange)
 * so an enabled overlay reads at a glance. Inactive share the neutral transparent-on-tray fill.
 */
const ACTIVE_TINT: Record<string, string> = {
  'toggle-machine-coloring': 'bg-primary/15 text-primary',
  'toggle-failure-heatmap': 'bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300',
  'toggle-dataflow-overlay': 'bg-primary/15 text-primary',
  'toggle-coverage-heatmap': 'bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300',
  'toggle-critical-path': 'bg-orange-100 text-orange-800 dark:bg-orange-900/40 dark:text-orange-300',
};

/**
 * The five inspection overlays as individual inline toggle buttons — the classic-layout
 * counterpart to {@link OverlaysMenu}'s popover. Consumes the shared {@link OVERLAY_DEFS}, so
 * both layouts keep the same five `data-testid`s, i18n titles and store wiring; here each is a
 * `role="button"` with `aria-pressed` and its own active tint.
 */
export function ClassicOverlayToggles() {
  const { t } = useTranslation('editor');
  const state = useDesignStore();
  return (
    <>
      {OVERLAY_DEFS.map((d) => {
        const active = d.selectEnabled(state);
        const Icon = d.icon;
        return (
          <button
            key={d.key}
            type="button"
            onClick={d.selectToggle(state)}
            aria-pressed={active}
            aria-label={t(d.labelKey)}
            data-testid={d.testId}
            title={active ? t(d.onTitleKey) : t(d.offTitleKey)}
            className={`flex items-center justify-center rounded-md h-9 w-9 transition-colors ${
              active ? ACTIVE_TINT[d.testId] : 'bg-transparent hover:bg-surface-high text-on-surface-variant'
            }`}
          >
            <Icon size={16} />
          </button>
        );
      })}
    </>
  );
}
