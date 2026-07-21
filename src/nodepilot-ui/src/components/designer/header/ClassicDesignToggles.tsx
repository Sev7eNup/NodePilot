import {
  Box,
  Categories,
  DotMark,
  FlashFilled,
  FlashOff,
  FlowConnection,
  Grid,
  Identification,
  MagicWandFilled,
} from '@carbon/icons-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  useDesignStore, NODE_SCALES, EDGE_WIDTHS, LABEL_FONT_OFFSETS, SNAP_GRID_SIZES,
  type EdgeRouting, type SnapGridSize,
} from '../../../stores/designStore';

/** Node-scale label per NODE_SCALES index — kept beside NODE_SCALES so the two never drift. */
const NODE_SCALE_LABELS = ['XS', 'S', 'M', 'L', 'XL', '2XL', '3XL', '4XL'] as const;

const ROUTING_OPTIONS: { value: EdgeRouting; titleKey: string }[] = [
  { value: 'smart', titleKey: 'toolbar.routing.smart' },
  { value: 'curved', titleKey: 'toolbar.routing.curved' },
  { value: 'straight', titleKey: 'toolbar.routing.straight' },
];

/**
 * The canvas-display controls as the classic inline row of icon buttons + steppers (edge
 * animation, routing, node-style, ports, sizes, grid-snap, …). This is the pre-redesign
 * `DesignToggle` cluster resurrected for the classic toolbar layout; the compact layout uses
 * the labeled {@link CanvasSettingsPanel} instead. Boolean toggles carry `aria-pressed`; all
 * tooltips reuse the existing `designer:toolbar.*` keys and semantic colour tokens.
 */
export function ClassicDesignToggles() {
  const { t } = useTranslation('designer');
  const {
    nodeStyle, toggleNodeStyle, nodeIconStyle, toggleNodeIconStyle,
    labelFontOffsetIndex, labelFontInc, labelFontDec,
    edgesAnimated, toggleEdgesAnimated,
    edgeWidthIndex, edgeWidthInc, edgeWidthDec, edgeRouting, setEdgeRouting,
    flexiblePortsEnabled, toggleFlexiblePorts,
    autoHidePorts, toggleAutoHidePorts,
    snapToGrid, snapGridSize, setSnapToGrid, setSnapGridSize,
    premiumCanvas, togglePremiumCanvas,
  } = useDesignStore();
  const labelOffset = LABEL_FONT_OFFSETS[labelFontOffsetIndex];
  const isClassic = nodeStyle === 'classic';

  return (
    <>
      <button
        type="button"
        onClick={toggleEdgesAnimated}
        aria-pressed={edgesAnimated}
        className="flex items-center justify-center rounded-md h-9 w-9 bg-transparent hover:bg-surface-high text-on-surface-variant transition-colors"
        title={edgesAnimated ? t('toolbar.stopEdgeAnimation') : t('toolbar.animateEdges')}
      >
        {edgesAnimated ? <FlashFilled size={16} /> : <FlashOff size={16} />}
      </button>
      <button
        type="button"
        onClick={() => {
          const idx = ROUTING_OPTIONS.findIndex((o) => o.value === edgeRouting);
          setEdgeRouting(ROUTING_OPTIONS[(idx + 1) % ROUTING_OPTIONS.length].value);
        }}
        className="flex items-center justify-center rounded-md h-9 w-9 bg-transparent hover:bg-surface-high text-on-surface-variant transition-colors"
        title={(() => { const o = ROUTING_OPTIONS.find((o) => o.value === edgeRouting); return o ? t(o.titleKey) : undefined; })()}
      >
        <FlowConnection size={16} />
      </button>
      <button
        type="button"
        onClick={toggleNodeStyle}
        className="flex items-center justify-center rounded-md h-9 w-9 bg-transparent hover:bg-surface-high text-on-surface-variant transition-colors"
        title={t('toolbar.switchToView', { view: isClassic ? t('toolbar.viewCard') : t('toolbar.viewClassic') })}
      >
        {isClassic ? <Identification size={16} /> : <Box size={16} />}
      </button>
      {isClassic && (
        <button
          type="button"
          onClick={togglePremiumCanvas}
          aria-pressed={premiumCanvas}
          className={`flex items-center justify-center rounded-md h-9 w-9 transition-colors ${
            premiumCanvas ? 'bg-primary/15 text-primary' : 'bg-transparent hover:bg-surface-high text-on-surface-variant'
          }`}
          title={premiumCanvas ? t('toolbar.premiumCanvasOn') : t('toolbar.premiumCanvasOff')}
        >
          <MagicWandFilled size={16} />
        </button>
      )}
      {isClassic && (
        <button
          type="button"
          onClick={toggleNodeIconStyle}
          aria-pressed={nodeIconStyle === 'glyph'}
          className={`flex items-center justify-center rounded-md h-9 w-9 transition-colors ${
            nodeIconStyle === 'glyph' ? 'bg-primary/15 text-primary' : 'bg-transparent hover:bg-surface-high text-on-surface-variant'
          }`}
          title={nodeIconStyle === 'glyph' ? t('toolbar.nodeIconsToShape') : t('toolbar.nodeIconsToGlyph')}
        >
          <Categories size={16} />
        </button>
      )}
      <button
        type="button"
        onClick={toggleFlexiblePorts}
        aria-pressed={flexiblePortsEnabled}
        title={flexiblePortsEnabled ? t('toolbar.flexiblePortsOn') : t('toolbar.flexiblePortsOff')}
        className={`flex items-center justify-center rounded-md h-9 w-9 transition-colors ${
          flexiblePortsEnabled ? 'bg-primary/15 text-primary' : 'bg-transparent hover:bg-surface-high text-on-surface-variant'
        }`}
      >
        <FlowConnection size={16} />
      </button>
      <button
        type="button"
        onClick={toggleAutoHidePorts}
        aria-pressed={autoHidePorts}
        title={autoHidePorts ? t('toolbar.portsAutoHideOn') : t('toolbar.portsAutoHideOff')}
        className={`flex items-center justify-center rounded-md h-9 w-9 transition-colors ${
          autoHidePorts ? 'bg-primary/15 text-primary' : 'bg-transparent hover:bg-surface-high text-on-surface-variant'
        }`}
      >
        <DotMark size={16} />
      </button>
      <div data-glow-unit className="flex items-center bg-surface-high rounded-md h-9 overflow-hidden" title={t('toolbar.edgeThickness')}>
        <button type="button" onClick={edgeWidthDec} disabled={edgeWidthIndex === 0}
          className="px-1 h-full text-on-surface-variant hover:bg-surface-highest disabled:opacity-30 transition-colors" title={t('toolbar.thinnerEdges')}>
          <span className="text-sm font-bold" aria-hidden>−</span>
        </button>
        <svg width="14" height="12" viewBox="0 0 14 12" className="text-on-surface-variant" aria-hidden>
          <line x1="1" y1="6" x2="13" y2="6" stroke="currentColor" strokeWidth={EDGE_WIDTHS[edgeWidthIndex]} strokeLinecap="round" />
        </svg>
        <button type="button" onClick={edgeWidthInc} disabled={edgeWidthIndex === EDGE_WIDTHS.length - 1}
          className="px-1 h-full text-on-surface-variant hover:bg-surface-highest disabled:opacity-30 transition-colors" title={t('toolbar.thickerEdges')}>
          <span className="text-sm font-bold" aria-hidden>+</span>
        </button>
      </div>
      {isClassic && <NodeScaleStepper />}
      {isClassic && (
        <div data-glow-unit className="flex items-center bg-surface-high rounded-md h-9 overflow-hidden" title={t('toolbar.labelFontSize')}>
          <button type="button" onClick={labelFontDec} disabled={labelFontOffsetIndex === 0}
            className="px-1 h-full text-on-surface-variant hover:bg-surface-highest disabled:opacity-30 transition-colors" title={t('toolbar.smallerLabel')}>
            <span className="text-sm font-bold" aria-hidden>−</span>
          </button>
          <span className="font-label font-semibold text-on-surface-variant w-8 text-center flex items-baseline justify-center">
            <span className="text-[9px] leading-none">A</span>
            <span className="text-[13px] leading-none">A</span>
            <span className="text-[9px] leading-none tabular-nums">{labelOffset > 0 ? `+${labelOffset}` : labelOffset}</span>
          </span>
          <button type="button" onClick={labelFontInc} disabled={labelFontOffsetIndex === LABEL_FONT_OFFSETS.length - 1}
            className="px-1 h-full text-on-surface-variant hover:bg-surface-highest disabled:opacity-30 transition-colors" title={t('toolbar.largerLabel')}>
            <span className="text-sm font-bold" aria-hidden>+</span>
          </button>
        </div>
      )}
      <GridSnapMenu snapToGrid={snapToGrid} snapGridSize={snapGridSize} setSnapToGrid={setSnapToGrid} setSnapGridSize={setSnapGridSize} />
    </>
  );
}

function NodeScaleStepper() {
  const { t } = useTranslation('designer');
  const nodeScaleIndex = useDesignStore((s) => s.nodeScaleIndex);
  const zoomIn = useDesignStore((s) => s.zoomIn);
  const zoomOut = useDesignStore((s) => s.zoomOut);
  return (
    <div data-glow-unit className="flex items-center bg-surface-high rounded-md h-9 overflow-hidden">
      <button type="button" onClick={zoomOut} disabled={nodeScaleIndex === 0}
        className="px-1 h-full text-on-surface-variant hover:bg-surface-highest disabled:opacity-30 transition-colors" title={t('toolbar.smallerNodes')}>
        <span className="text-sm font-bold" aria-hidden>−</span>
      </button>
      <span className="text-[10px] font-label font-semibold text-on-surface-variant w-6 text-center">
        {NODE_SCALE_LABELS[nodeScaleIndex]}
      </span>
      <button type="button" onClick={zoomIn} disabled={nodeScaleIndex === NODE_SCALES.length - 1}
        className="px-1 h-full text-on-surface-variant hover:bg-surface-highest disabled:opacity-30 transition-colors" title={t('toolbar.largerNodes')}>
        <span className="text-sm font-bold" aria-hidden>+</span>
      </button>
    </div>
  );
}

function GridSnapMenu({ snapToGrid, snapGridSize, setSnapToGrid, setSnapGridSize }: Readonly<{
  snapToGrid: boolean;
  snapGridSize: SnapGridSize;
  setSnapToGrid: (v: boolean) => void;
  setSnapGridSize: (v: SnapGridSize) => void;
}>) {
  const { t } = useTranslation('designer');
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as globalThis.Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', onPointerDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onPointerDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-haspopup="menu"
        aria-expanded={open}
        title={snapToGrid ? t('toolbar.snap.to', { size: snapGridSize }) : t('toolbar.snap.off')}
        className={`flex items-center justify-center rounded-md h-9 w-9 transition-colors ${
          snapToGrid ? 'bg-primary/15 text-primary' : 'bg-transparent hover:bg-surface-high text-on-surface-variant'
        }`}
      >
        <Grid size={16} />
      </button>
      {open && (
        <div role="menu" className="absolute top-full right-0 mt-1 z-50 min-w-[88px] rounded-md border border-outline-variant/30 bg-surface-high shadow-lg py-1">
          <button
            type="button"
            onClick={() => { setSnapToGrid(false); setOpen(false); }}
            className={`block w-full px-3 py-1.5 text-left text-xs font-label font-semibold transition-colors ${
              !snapToGrid ? 'bg-primary/20 text-primary' : 'text-on-surface-variant hover:bg-surface-highest'
            }`}
          >
            {t('toolbar.snap.offLabel')}
          </button>
          <div className="h-px bg-outline-variant/30 my-1" />
          {(SNAP_GRID_SIZES as readonly SnapGridSize[]).map((size) => (
            <button
              key={size}
              type="button"
              onClick={() => { setSnapToGrid(true); setSnapGridSize(size); setOpen(false); }}
              className={`block w-full px-3 py-1.5 text-left text-xs font-label font-semibold transition-colors ${
                snapToGrid && snapGridSize === size ? 'bg-primary/20 text-primary' : 'text-on-surface-variant hover:bg-surface-highest'
              }`}
            >
              {size}px
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
