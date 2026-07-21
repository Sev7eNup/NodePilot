import { Box, FlowConnection, Grid } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import {
  useDesignStore,
  NODE_SCALES,
  EDGE_WIDTHS,
  LABEL_FONT_OFFSETS,
  SNAP_GRID_SIZES,
  type EdgeRouting,
  type NodeStyle,
  type NodeIconStyle,
  type SnapGridSize,
} from '../../../stores/designStore';
import { SettingsCard, SettingRow, SettingSwitchRow, SegmentedControl, Stepper } from './menuPrimitives';

/** Node-scale label per NODE_SCALES index — kept beside NODE_SCALES so the two never drift. */
const NODE_SCALE_LABELS = ['XS', 'S', 'M', 'L', 'XL', '2XL', '3XL', '4XL'] as const;

/**
 * Compact quick settings for the designer canvas. The two-column card layout keeps every option
 * discoverable without turning the toolbar popover into a long settings page. Classic-only rows
 * (premium canvas, icon style, node scale, label font) stay hidden in card mode where they have
 * no effect.
 */
export function CanvasSettingsPanel() {
  const { t } = useTranslation('designer');
  const {
    nodeStyle, setNodeStyle, nodeIconStyle, setNodeIconStyle,
    labelFontOffsetIndex, labelFontInc, labelFontDec,
    edgesAnimated, toggleEdgesAnimated,
    edgeWidthIndex, edgeWidthInc, edgeWidthDec, edgeRouting, setEdgeRouting,
    flexiblePortsEnabled, toggleFlexiblePorts,
    autoHidePorts, toggleAutoHidePorts,
    nodeScaleIndex, zoomIn, zoomOut,
    snapToGrid, snapGridSize, setSnapToGrid, setSnapGridSize,
    premiumCanvas, togglePremiumCanvas,
  } = useDesignStore();
  const isClassic = nodeStyle === 'classic';
  const labelOffset = LABEL_FONT_OFFSETS[labelFontOffsetIndex];

  const routingOptions: ReadonlyArray<{ value: EdgeRouting; label: string; title?: string }> = [
    { value: 'smart', label: t('canvasSettings.routing.smart'), title: t('toolbar.routing.smart') },
    { value: 'curved', label: t('canvasSettings.routing.curved'), title: t('toolbar.routing.curved') },
    { value: 'straight', label: t('canvasSettings.routing.straight'), title: t('toolbar.routing.straight') },
  ];
  const nodeStyleOptions: ReadonlyArray<{ value: NodeStyle; label: string }> = [
    { value: 'classic', label: t('canvasSettings.nodeStyle.classic') },
    { value: 'card', label: t('canvasSettings.nodeStyle.card') },
  ];
  const iconStyleOptions: ReadonlyArray<{ value: NodeIconStyle; label: string }> = [
    { value: 'shape', label: t('canvasSettings.iconStyle.shape') },
    { value: 'glyph', label: t('canvasSettings.iconStyle.glyph') },
  ];
  // Snap: an "off" chip plus one chip per grid size. Value is 'off' or the size as a string.
  const snapValue = snapToGrid ? String(snapGridSize) : 'off';
  const snapOptions: ReadonlyArray<{ value: string; label: string }> = [
    { value: 'off', label: t('canvasSettings.snap.off') },
    ...SNAP_GRID_SIZES.map((size) => ({ value: String(size), label: String(size) })),
  ];
  const onSnapChange = (value: string) => {
    if (value === 'off') { setSnapToGrid(false); return; }
    setSnapToGrid(true);
    setSnapGridSize(Number(value) as SnapGridSize);
  };

  return (
    <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
      <SettingsCard icon={<Box size={16} />} title={t('canvasSettings.sections.nodes')}>
        <SettingRow
          title={t('canvasSettings.nodeStyle.name')}
          description={t('canvasSettings.nodeStyle.desc')}
          controlPlacement="below"
          control={
            <SegmentedControl
              options={nodeStyleOptions}
              value={nodeStyle}
              onChange={setNodeStyle}
              label={t('canvasSettings.nodeStyle.name')}
              description={t('canvasSettings.nodeStyle.desc')}
              testId="canvas-setting-node-style"
            />
          }
        />
        {isClassic && (
          <SettingRow
            title={t('canvasSettings.nodeScale.name')}
            description={t('canvasSettings.nodeScale.desc')}
            control={
              <Stepper
                onDec={zoomOut}
                onInc={zoomIn}
                decDisabled={nodeScaleIndex === 0}
                incDisabled={nodeScaleIndex === NODE_SCALES.length - 1}
                decLabel={t('toolbar.smallerNodes')}
                incLabel={t('toolbar.largerNodes')}
                description={t('canvasSettings.nodeScale.desc')}
                testId="canvas-setting-node-scale"
              >
                <span className="text-[10px] font-label font-semibold">{NODE_SCALE_LABELS[nodeScaleIndex]}</span>
              </Stepper>
            }
          />
        )}
        {isClassic && (
          <SettingRow
            title={t('canvasSettings.iconStyle.name')}
            description={t('canvasSettings.iconStyle.desc')}
            controlPlacement="below"
            control={
              <SegmentedControl
                options={iconStyleOptions}
                value={nodeIconStyle}
                onChange={setNodeIconStyle}
                label={t('canvasSettings.iconStyle.name')}
                description={t('canvasSettings.iconStyle.desc')}
                testId="canvas-setting-icon-style"
              />
            }
          />
        )}
        {isClassic && (
          <SettingRow
            title={t('canvasSettings.labelFont.name')}
            description={t('canvasSettings.labelFont.desc')}
            control={
              <Stepper
                onDec={labelFontDec}
                onInc={labelFontInc}
                decDisabled={labelFontOffsetIndex === 0}
                incDisabled={labelFontOffsetIndex === LABEL_FONT_OFFSETS.length - 1}
                decLabel={t('toolbar.smallerLabel')}
                incLabel={t('toolbar.largerLabel')}
                description={t('canvasSettings.labelFont.desc')}
                testId="canvas-setting-label-font"
              >
                <span className="flex items-baseline justify-center font-label font-semibold">
                  <span className="text-[9px] leading-none">A</span>
                  <span className="text-[13px] leading-none">A</span>
                  <span className="text-[9px] leading-none tabular-nums">{labelOffset > 0 ? `+${labelOffset}` : labelOffset}</span>
                </span>
              </Stepper>
            }
          />
        )}
      </SettingsCard>
      <SettingsCard icon={<FlowConnection size={16} />} title={t('canvasSettings.sections.connections')}>
        <SettingRow
          title={t('canvasSettings.routing.name')}
          description={t('canvasSettings.routing.desc')}
          controlPlacement="below"
          control={
            <SegmentedControl
              options={routingOptions}
              value={edgeRouting}
              onChange={setEdgeRouting}
              label={t('canvasSettings.routing.name')}
              description={t('canvasSettings.routing.desc')}
              testId="canvas-setting-edge-routing"
            />
          }
        />
        <SettingSwitchRow
          label={t('canvasSettings.animation.name')}
          description={t('canvasSettings.animation.desc')}
          checked={edgesAnimated}
          onToggle={toggleEdgesAnimated}
          testId="canvas-setting-edge-animation"
        />
        <SettingRow
          title={t('canvasSettings.thickness.name')}
          description={t('canvasSettings.thickness.desc')}
          control={
            <Stepper
              onDec={edgeWidthDec}
              onInc={edgeWidthInc}
              decDisabled={edgeWidthIndex === 0}
              incDisabled={edgeWidthIndex === EDGE_WIDTHS.length - 1}
              decLabel={t('toolbar.thinnerEdges')}
              incLabel={t('toolbar.thickerEdges')}
              description={t('canvasSettings.thickness.desc')}
              testId="canvas-setting-edge-thickness"
            >
              <svg width="16" height="12" viewBox="0 0 16 12" aria-hidden>
                <line x1="1" y1="6" x2="15" y2="6" stroke="currentColor" strokeWidth={EDGE_WIDTHS[edgeWidthIndex]} strokeLinecap="round" />
              </svg>
            </Stepper>
          }
        />
        <SettingSwitchRow
          label={t('canvasSettings.flexiblePorts.name')}
          description={t('canvasSettings.flexiblePorts.desc')}
          checked={flexiblePortsEnabled}
          onToggle={toggleFlexiblePorts}
          testId="canvas-setting-flexible-ports"
        />
        <SettingSwitchRow
          label={t('canvasSettings.autoHidePorts.name')}
          description={t('canvasSettings.autoHidePorts.desc')}
          checked={autoHidePorts}
          onToggle={toggleAutoHidePorts}
          testId="canvas-setting-auto-hide-ports"
        />
      </SettingsCard>
      <SettingsCard
        icon={<Grid size={16} />}
        title={t('canvasSettings.sections.canvas')}
        className="sm:col-span-2"
      >
        {isClassic && (
          <SettingSwitchRow
            label={t('canvasSettings.premium.name')}
            description={t('canvasSettings.premium.desc')}
            checked={premiumCanvas}
            onToggle={togglePremiumCanvas}
            testId="canvas-setting-premium-canvas"
          />
        )}
        <SettingRow
          title={t('canvasSettings.snap.name')}
          description={t('canvasSettings.snap.desc')}
          controlPlacement="below"
          control={
            <SegmentedControl
              options={snapOptions}
              value={snapValue}
              onChange={onSnapChange}
              label={t('canvasSettings.snap.name')}
              description={t('canvasSettings.snap.desc')}
              testId="canvas-setting-snap"
            />
          }
        />
      </SettingsCard>
    </div>
  );
}
