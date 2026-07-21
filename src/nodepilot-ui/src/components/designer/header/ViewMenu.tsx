import { Settings } from '@carbon/icons-react';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CanvasSettingsPanel } from './CanvasSettingsPanel';
import { usePopover } from './menuPrimitives';

const TITLE_ID = 'canvas-settings-title';

/**
 * "Darstellung" (canvas-display) dialog — one popover holding every canvas-display preference
 * ({@link CanvasSettingsPanel}: node-style, icon-view, ports, edge animation/routing/width,
 * label size, grid-snap, …) as labeled, described rows. It's a settings *dialog*, not a menu:
 * `role="dialog"` + focus moves into the panel on open and back to the trigger on close. The
 * overlays and the activity-type filter keep their OWN standalone toolbar buttons
 * (see OverlaysMenu / ActivityTypeFilter).
 */
export function ViewMenu() {
  const { t } = useTranslation(['editor', 'designer']);
  const { open, setOpen, ref } = usePopover();
  const triggerRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const wasOpen = useRef(false);
  const panelShiftRef = useRef(0);
  const [panelShift, setPanelShift] = useState(0);

  // Move focus into the panel when it opens, and return it to the trigger when it closes
  // (Escape / outside-click / re-click all route through `open`).
  useEffect(() => {
    if (open) panelRef.current?.focus();
    else if (wasOpen.current) triggerRef.current?.focus();
    wasOpen.current = open;
  }, [open]);

  // The wider quick-settings panel is centered under the trigger. Clamp that centered position
  // back into the viewport on narrow editor windows and after a resize.
  useLayoutEffect(() => {
    if (!open) return;

    const clampToViewport = () => {
      const panel = panelRef.current;
      if (!panel) return;
      const rect = panel.getBoundingClientRect();
      const viewportInset = 8;
      const baseLeft = rect.left - panelShiftRef.current;
      const baseRight = rect.right - panelShiftRef.current;
      let nextShift = 0;
      if (baseLeft < viewportInset) nextShift = viewportInset - baseLeft;
      else if (baseRight > window.innerWidth - viewportInset) nextShift = window.innerWidth - viewportInset - baseRight;
      if (nextShift === panelShiftRef.current) return;
      panelShiftRef.current = nextShift;
      setPanelShift(nextShift);
    };

    clampToViewport();
    window.addEventListener('resize', clampToViewport);
    return () => window.removeEventListener('resize', clampToViewport);
  }, [open]);

  const title = t('designer:canvasSettings.title');

  return (
    <div ref={ref} className="relative">
      <button
        ref={triggerRef}
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-haspopup="dialog"
        aria-expanded={open}
        data-testid="canvas-settings-trigger"
        title={title}
        className={`flex items-center justify-center rounded-md h-9 w-9 transition-colors ${
          open ? 'bg-primary/15 text-primary' : 'bg-transparent hover:bg-surface-high text-on-surface-variant'
        }`}
      >
        <Settings size={16} />
      </button>
      {open && (
        <div
          ref={panelRef}
          role="dialog"
          aria-labelledby={TITLE_ID}
          tabIndex={-1}
          data-testid="canvas-settings-dialog"
          style={{ transform: `translateX(calc(-50% + ${panelShift}px))` }}
          className="np-anim-overlay absolute left-1/2 top-full mt-1.5 flex max-h-[70vh] w-[28rem] max-w-[calc(100vw-1rem)] flex-col rounded-xl border border-outline-variant/30 bg-surface-lowest shadow-[var(--np-elev-3)] z-[60] focus:outline-none"
        >
          <div className="border-b border-outline-variant/20 px-3.5 py-2.5">
            <h2 id={TITLE_ID} className="text-sm font-label font-semibold text-on-surface">
              {title}
            </h2>
          </div>
          <div data-testid="canvas-settings-scroll" className="overflow-y-auto p-2">
            <CanvasSettingsPanel />
          </div>
        </div>
      )}
    </div>
  );
}
