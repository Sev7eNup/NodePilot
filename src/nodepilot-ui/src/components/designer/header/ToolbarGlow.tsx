import { useCallback, useEffect, useMemo, useRef, type ReactNode } from 'react';
import { ToolbarGlowContext } from './toolbarGlowContext';
import { activeSectionGlow, bloomGeometry, DEFAULT_GLOW_REACH, type GlowRect } from './glowMath';

/** Standalone interactive controls the bloom can hug. */
const CONTROL_SELECTOR = 'button, a, [role="button"]';
/** Multi-button groups (e.g. a −/value/+ stepper) marked as a single glow unit. */
const UNIT_SELECTOR = '[data-glow-unit]';
/** Section bloom overhang (fraction of section width) in the wide "approach" state. */
const BLOOM_OVERHANG = 0.04;
/** Extra px each side of a hugged control in the collapsed state. */
const BLOOM_PAD = 4;

function rectToGlow(el: Element): GlowRect {
  const r = el.getBoundingClientRect();
  return { left: r.left, right: r.right, top: r.top, bottom: r.bottom };
}

/**
 * The control "unit" the bloom should hug within a section. A multi-button group (e.g. a
 * −/value/+ stepper) carries `data-glow-unit` so the whole group counts as one; otherwise a
 * standalone control is its own unit. Fast path: the unit directly under the cursor. Else
 * (cursor in a gap at row level): the horizontally-nearest unit — so sliding control→control
 * never momentarily expands back to the full-section bloom.
 */
function nearestUnitRect(section: Element, target: Element | null, cx: number): GlowRect | null {
  if (target && section.contains(target)) {
    const direct = target.closest(UNIT_SELECTOR) ?? target.closest(CONTROL_SELECTOR);
    if (direct && section.contains(direct)) return rectToGlow(direct);
  }
  const groups = Array.from(section.querySelectorAll(UNIT_SELECTOR));
  const loose = Array.from(section.querySelectorAll(CONTROL_SELECTOR)).filter(
    (el) => !el.closest(UNIT_SELECTOR),
  );
  let best: Element | null = null;
  let bestDist = Infinity;
  for (const unit of groups.concat(loose)) {
    const r = unit.getBoundingClientRect();
    const dist = Math.max(r.left - cx, 0, cx - r.right);
    if (dist < bestDist) {
      bestDist = dist;
      best = unit;
    }
  }
  return best ? rectToGlow(best) : null;
}

function prefersReducedMotion(): boolean {
  return (
    typeof window !== 'undefined' &&
    typeof window.matchMedia === 'function' &&
    window.matchMedia('(prefers-reduced-motion: reduce)').matches
  );
}

/**
 * Provider for the toolbar proximity glow. Renders the toolbar-root flex container and tracks
 * the cursor on `document` so the glow reacts as the mouse APPROACHES the bar from below (out
 * of the canvas) — not only once it's over the buttons. Exactly ONE section glows at a time:
 * the section whose horizontal column the cursor is in (or the nearest in the gaps); nothing
 * glows when the cursor is left of the bar (over the logo / workflow-name) or far from it.
 *
 * Performance:
 *   • NO React re-renders — the handler writes `--np-glow` straight to the DOM via setProperty.
 *   • rAF-throttled, a single frame in flight → at most ~one update per animation frame.
 *   • A cached vertical band gates the work: while the cursor is well above/below the toolbar
 *     (the hot path when panning/dragging the canvas), the rAF does a single coordinate compare
 *     and bails — NO getBoundingClientRect, no writes. Per-section layout reads happen only
 *     while the cursor is within `REACH` of the bar's vertical band. The toolbar's Y is stable,
 *     so the cached band survives the workflow-name input changing the bar's horizontal width.
 *   • Only `opacity` animates in CSS (constant blur) → kept cheap and compositor-friendly.
 *   • Touch points are ignored (a finger hovering isn't a meaningful "approach").
 *   • Under prefers-reduced-motion the effect is never wired up.
 */
export function ToolbarGlow({
  className,
  children,
}: Readonly<{ className?: string; children: ReactNode }>) {
  const rootRef = useRef<HTMLDivElement>(null);
  const sectionsRef = useRef<Set<HTMLElement>>(new Set());
  const rafRef = useRef<number | null>(null);
  const pointerRef = useRef<{ x: number; y: number; target: Element | null } | null>(null);
  const bandRef = useRef<{ top: number; bottom: number } | null>(null);
  const litRef = useRef(false);

  const register = useCallback((el: HTMLElement) => {
    sectionsRef.current.add(el);
    bandRef.current = null; // section set changed → cached band stale
    return () => {
      sectionsRef.current.delete(el);
      bandRef.current = null;
      el.style.removeProperty('--np-glow');
    };
  }, []);

  const ctxValue = useMemo(() => ({ register }), [register]);

  useEffect(() => {
    if (prefersReducedMotion()) return; // no tracking → zero runtime cost
    const root = rootRef.current;
    if (!root) return;

    const REACH = DEFAULT_GLOW_REACH;

    const zeroAll = () => {
      for (const el of sectionsRef.current) el.style.setProperty('--np-glow', '0');
      litRef.current = false;
    };

    const computeAndApply = (px: number, py: number, target: Element | null) => {
      const els = Array.from(sectionsRef.current);
      if (els.length === 0) {
        bandRef.current = null;
        return;
      }
      const rects: GlowRect[] = new Array(els.length);
      let top = Infinity;
      let bottom = -Infinity;
      for (let i = 0; i < els.length; i++) {
        const r = els[i].getBoundingClientRect();
        rects[i] = { left: r.left, right: r.right, top: r.top, bottom: r.bottom };
        if (r.top < top) top = r.top;
        if (r.bottom > bottom) bottom = r.bottom;
      }
      bandRef.current = { top, bottom }; // refresh the cheap vertical gate
      const active = activeSectionGlow(rects, px, py, REACH);
      for (let i = 0; i < els.length; i++) {
        els[i].style.setProperty(
          '--np-glow',
          active && active.index === i ? active.intensity.toFixed(3) : '0',
        );
      }
      litRef.current = !!active && active.intensity > 0;

      // Once the cursor is at the section's row level, hug a single control — the one under the
      // cursor, or (in a gap between controls) the nearest one, so moving control→control slides
      // the bloom instead of flashing the full-section width. While still approaching from below
      // (cursor under the bar), keep the wide full-section bloom.
      if (active && active.intensity > 0) {
        const el = els[active.index];
        const sRect = rects[active.index];
        const atRow = py >= sRect.top && py <= sRect.bottom;
        const unit = atRow ? nearestUnitRect(el, target, px) : null;
        const geo = bloomGeometry(sRect, unit, BLOOM_OVERHANG, BLOOM_PAD);
        el.style.setProperty('--np-bloom-left', `${geo.left.toFixed(1)}px`);
        el.style.setProperty('--np-bloom-width', `${geo.width.toFixed(1)}px`);
      }
    };

    const apply = () => {
      rafRef.current = null;
      const p = pointerRef.current;
      if (!p) return;
      const band = bandRef.current;
      if (band && (p.y < band.top - REACH || p.y > band.bottom + REACH)) {
        if (litRef.current) zeroAll(); // cursor far from the bar → ensure nothing lingers lit
        return;
      }
      computeAndApply(p.x, p.y, p.target);
    };

    const schedule = () => {
      if (rafRef.current == null) rafRef.current = requestAnimationFrame(apply);
    };

    const onMove = (e: PointerEvent) => {
      if (e.pointerType === 'touch') return; // a finger hovering isn't a meaningful approach
      pointerRef.current = {
        x: e.clientX,
        y: e.clientY,
        target: e.target instanceof Element ? e.target : null,
      };
      schedule();
    };
    const onLeaveWindow = () => {
      pointerRef.current = null;
      if (rafRef.current != null) {
        cancelAnimationFrame(rafRef.current);
        rafRef.current = null;
      }
      zeroAll();
    };
    const invalidateBand = () => {
      bandRef.current = null;
    };

    document.addEventListener('pointermove', onMove, { passive: true });
    document.addEventListener('pointerleave', onLeaveWindow, { passive: true });
    window.addEventListener('blur', onLeaveWindow);
    window.addEventListener('resize', invalidateBand);
    window.addEventListener('scroll', invalidateBand, { passive: true, capture: true });
    const ro = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(invalidateBand) : null;
    ro?.observe(root);

    return () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerleave', onLeaveWindow);
      window.removeEventListener('blur', onLeaveWindow);
      window.removeEventListener('resize', invalidateBand);
      window.removeEventListener('scroll', invalidateBand, { capture: true } as EventListenerOptions);
      ro?.disconnect();
      if (rafRef.current != null) cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
      // Intentionally read the LIVE section set at teardown (stable ref, Set identity never
      // changes) so late-registered sections are cleared too.
      // eslint-disable-next-line react-hooks/exhaustive-deps
      for (const el of sectionsRef.current) el.style.removeProperty('--np-glow');
    };
  }, []);

  return (
    <ToolbarGlowContext.Provider value={ctxValue}>
      <div ref={rootRef} className={className}>
        {children}
      </div>
    </ToolbarGlowContext.Provider>
  );
}
