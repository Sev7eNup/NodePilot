import { View } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { useDesignStore } from '../../../stores/designStore';
import { usePopover, OverlaySwitchRow } from './menuPrimitives';
import { OVERLAY_DEFS } from './overlayDefs';

/**
 * "Overlays" menu — its own toolbar button (Eye) holding the five inspection overlays
 * (machine-coloring / failure-heatmap / data-flow / coverage / critical-path) as switch
 * rows. Kept as a standalone button (not merged into the display/tools menus) per product
 * preference. An active-count badge on the trigger surfaces how many overlays are on. The
 * overlay list is shared with the classic inline layout via {@link OVERLAY_DEFS}, so the
 * `view-overlays-trigger` + per-overlay `toggle-*` testids stay identical across both layouts.
 */
export function OverlaysMenu() {
  const { t } = useTranslation('editor');
  const { open, setOpen, ref } = usePopover();
  const state = useDesignStore();
  const activeOverlayCount = OVERLAY_DEFS.filter((d) => d.selectEnabled(state)).length;

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-haspopup="menu"
        aria-expanded={open}
        data-testid="view-overlays-trigger"
        title={t('overlays.title')}
        className={`relative flex items-center justify-center rounded-md h-9 w-9 transition-colors ${
          activeOverlayCount > 0 || open ? 'bg-primary/15 text-primary' : 'bg-transparent hover:bg-surface-high text-on-surface-variant'
        }`}
      >
        <View size={16} />
        {activeOverlayCount > 0 && (
          <span className="absolute -top-1 -right-1 flex items-center justify-center min-w-4 h-4 px-0.5 rounded-full bg-primary text-on-primary text-[9px] font-label font-bold leading-none">
            {activeOverlayCount}
          </span>
        )}
      </button>
      {open && (
        <div role="menu" className="np-anim-overlay absolute right-0 top-full mt-1.5 w-64 rounded-xl border border-outline-variant/30 bg-surface-lowest shadow-[var(--np-elev-3)] p-1.5 z-[60] flex flex-col gap-0.5">
          {OVERLAY_DEFS.map((d) => {
            const checked = d.selectEnabled(state);
            const Icon = d.icon;
            return (
              <OverlaySwitchRow
                key={d.key}
                label={t(d.labelKey)}
                title={checked ? t(d.onTitleKey) : t(d.offTitleKey)}
                icon={<Icon size={15} />}
                checked={checked}
                onToggle={d.selectToggle(state)}
                testId={d.testId}
              />
            );
          })}
        </div>
      )}
    </div>
  );
}
