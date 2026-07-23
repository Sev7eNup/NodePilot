import { Chat, FolderTree, PauseFilled, PlayFilledAlt, Time, Timer, ViewOff } from '@carbon/icons-react';
import { Handle, type NodeProps } from '@xyflow/react';
import { useState, useRef, memo, createContext, useContext, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { createPortal } from 'react-dom';
import { useDesignStore, NODE_SCALES, LABEL_FONT_OFFSETS, MACHINE_COLORS } from '../../../stores/designStore';
import { useThemeStore } from '../../../stores/themeStore';
import { summarizeActivityConfig } from '../../../lib/activityConfigFacts';
import { TRIGGER_ACTIVITY_TYPES } from '../../../lib/activityCatalog.generated';
import { ACTIVITY_ICON_COMPONENTS, FALLBACK_ACTIVITY_ICON } from '../../../lib/activityIcons';
import { previewSchedule, relativeFromNow } from '../../../lib/cronPreview';
import { EDGE_PORT_SIDES, portToPosition, type EdgePortSide } from '../../../lib/edgePorts';
import { getNodeShape, getNodeSizeMultiplier, getIconScaleMultiplier, getBadgePositions, getHandleInset, getIconOffsetX, getIconOffsetY, isControlFlowShape, SHAPE_CLIP_PATHS, type NodeShape, type BadgePosition } from './shapes';
import { NodeScaleOverrideContext } from '../nodeScaleContext';

import { getActivityVisual } from './activityConfig';
import { usePointerFlowPosition } from '../../../stores/pointerFlowPositionStore';

/**
 * Context plumbing for the sub-workflow inline-preview button on startWorkflow nodes.
 * Same pattern as EdgeInsertContext (LabeledEdge.tsx) — keeps the callback off `data` so
 * memo() can stay reference-compare clean. The default no-op means nodes outside the
 * editor (e.g. the SubWorkflowPreviewModal's own ReactFlow instance) silently drop clicks.
 */
export const SubWorkflowPreviewContext = createContext<{
  onPreviewSubWorkflow: (nameOrId: string) => void;
}>({ onPreviewSubWorkflow: () => {} });

/**
 * Per-step colours for a pinned run's live path-highlight, riding the semantic status
 * tokens. The fill is a color-mix of the status hue onto surface-lowest: on white that
 * yields a bright pastel, on the near-black dark canvas a deep low-luminance fill — the
 * old hand-picked light/dark hex pairs, now derived from ONE formula per status. The
 * status signal is carried by the coloured border + the activity icon, not by a neon
 * fill. Running (amber) vs Paused (orange) stay distinguishable in both themes.
 */
const LIVE_STATUS_STYLE = {
  Running:   { bg: 'color-mix(in srgb, var(--color-running) 16%, var(--color-surface-lowest))', border: 'var(--color-running)', pulse: true },
  Succeeded: { bg: 'color-mix(in srgb, var(--color-success) 14%, var(--color-surface-lowest))', border: 'var(--color-success)' },
  Failed:    { bg: 'color-mix(in srgb, var(--color-error) 14%, var(--color-surface-lowest))',   border: 'var(--color-error)' },
  Skipped:   { bg: 'color-mix(in srgb, var(--color-skipped) 10%, var(--color-surface-lowest))', border: 'var(--color-skipped)', dashed: true },
  Paused:    { bg: 'color-mix(in srgb, var(--color-paused) 18%, var(--color-surface-lowest))',  border: 'var(--color-paused)', pulse: true },
} as const;

function resolveLiveStyle(status: string | undefined):
  { bgColor: string; borderColor: string; pulse: boolean; dashed: boolean } | null {
  if (!status) return null;
  const s = LIVE_STATUS_STYLE[status as keyof typeof LIVE_STATUS_STYLE];
  if (!s) return null;
  return {
    bgColor: s.bg,
    borderColor: s.border,
    pulse: 'pulse' in s ? s.pulse : false,
    dashed: 'dashed' in s ? s.dashed : false,
  };
}

/**
 * Dark-mode-only soft red halo for a Failed step. Asymmetric on purpose: on a large graph the eye
 * should jump straight to what broke, while Succeeded/Running stay calm (fill + border only). Same
 * `0 0 14px` radius the heatmap / critical-path glows already use, so it reads as the same family.
 */
const FAILED_GLOW_SHADOW = '0 0 14px color-mix(in srgb, var(--color-error) 55%, transparent)';

function ActivityNodeImpl({ data, selected, isConnectable, positionAbsoluteX, positionAbsoluteY, width, height }: NodeProps) {
  const { t } = useTranslation('designer');
  const nodeStyle = useDesignStore((s) => s.nodeStyle);
  const nodeIconStyle = useDesignStore((s) => s.nodeIconStyle);
  const autoHidePorts = useDesignStore((s) => s.autoHidePorts);
  const flexiblePortsEnabled = useDesignStore((s) => s.flexiblePortsEnabled);
  // A non-null override (e.g. from the mobile read-only graph) wins over the persisted store
  // scale, so the phone view can render larger nodes without changing the desktop preference.
  const scaleOverride = useContext(NodeScaleOverrideContext);
  const storeScaleIndex = useDesignStore((s) => s.nodeScaleIndex);
  const scaleIndex = scaleOverride ?? storeScaleIndex;
  const labelOffset = LABEL_FONT_OFFSETS[useDesignStore((s) => s.labelFontOffsetIndex)];
  const scale = NODE_SCALES[scaleIndex];
  const isDark = useThemeStore((s) => s.resolvedTheme === 'dark');
  const premiumCanvas = useDesignStore((s) => s.premiumCanvas);
  const effectiveLabelFont = Math.max(6, scale.labelFont + labelOffset);
  const activityType = (data.activityType as string) || 'runScript';
  const label = (data.label as string) || activityType;
  const config = (data.config as Record<string, unknown>) || {};
  const ac = getActivityVisual(activityType);
  const summary = summarizeActivityConfig(activityType, config);
  const isEntryTrigger = TRIGGER_ACTIVITY_TYPES.has(activityType);

  // Shape system: trigger nodes become left-pointing pentagons (a mirrored bookend to
  // returnData), control-flow nodes become diamonds, and returnData becomes a right-pointing
  // pentagon. Each shape's `size` is area-compensated so every silhouette reads optically equal,
  // and `iconScale` makes the inside-icon match the square node's icon (see shapes.ts). Square
  // (the default) leaves the existing render path untouched.
  const shape: NodeShape = getNodeShape(activityType);
  const sizeMultiplier = getNodeSizeMultiplier(shape);
  const iconScale = getIconScaleMultiplier(shape);
  const iconOffsetY = getIconOffsetY(shape);
  const iconOffsetX = getIconOffsetX(shape);
  /** Builds the transform style for the activity icon from the per-shape X/Y offsets, scaled
   *  to the relevant icon box (Classic: effectiveIconBox, Card: 26). Empty → no transform, so
   *  shapes with zero offset render unchanged. */
  const iconTransformFor = (box: number): React.CSSProperties => {
    const tx = iconOffsetX ? Math.round(iconOffsetX * box) : 0;
    const ty = iconOffsetY ? Math.round(iconOffsetY * box) : 0;
    return (tx || ty) ? { transform: `translate(${tx}px, ${ty}px)` } : {};
  };
  const effectiveIconBox = Math.round(scale.iconBox * sizeMultiplier);
  const effectiveLabelWidth = Math.round(scale.labelWidth * Math.max(1, sizeMultiplier * 0.92));
  const badgePos = getBadgePositions(shape);
  const isShaped = shape !== 'square';
  const shapeClipPath = SHAPE_CLIP_PATHS[shape];
  // Classic "icon view": render the bare palette glyph (large, accent-coloured, no silhouette)
  // instead of the clip-path shape. Toggle lives in the toolbar; card mode is unaffected.
  const useGlyphIcon = nodeStyle === 'classic' && nodeIconStyle === 'glyph';
  // Icon-view sizing: `glyphFs` is the rendered icon size AND the node footprint (the wrapper is
  // sized to glyphFs). It is derived from the raw `iconBox` WITHOUT the per-shape `sizeMultiplier`
  // so every bare-glyph node is the exact same size regardless of its silhouette form — optical
  // equality across all activities in icon view. Carbon icons are SVGs centered in a square
  // viewBox, so no ink measurement / centering translate is needed.
  const glyphFs = Math.round(scale.iconBox * 0.82);
  const IconComp = ACTIVITY_ICON_COMPONENTS[ac.icon] ?? FALLBACK_ACTIVITY_ICON;
  // Shaped (silhouette) icon: `iconScale` is now the DIRECT size factor relative to `iconFont`
  // (the same value the square path uses), so an `iconScale` of 1.0 renders the inside-icon at
  // exactly the same px as a square node's icon — equal across all activities. Only the few
  // sparse/pointed silhouettes that can't hold a full-size icon at a calm footprint keep an
  // `iconScale < 1.0` (see SHAPE_DEFS). Positioned by the per-shape X/Y offset only (the SVG is
  // already centered — no ink translate).
  const shapedIconFs = Math.max(10, Math.round(scale.iconFont * iconScale));
  const shapedIconTx = Math.round(iconOffsetX * effectiveIconBox);
  const shapedIconTy = Math.round(iconOffsetY * effectiveIconBox);
  const shapedIconStyle: React.CSSProperties = {
    transform: (shapedIconTx || shapedIconTy) ? `translate(${shapedIconTx}px, ${shapedIconTy}px)` : undefined,
  };
  // Bookend shapes (trigger pennant + returnData flag) keep their exact prior rendering; the
  // per-activity overlays (critical-path glow) are gated off them. Control-flow shapes are NOT
  // bookends — they now get the overlays and the shared control-group frame.
  const isBookendShape = shape === 'pennant' || shape === 'flag';
  // Control-flow group: a shared indigo double-outline frame (an extra silhouette layer at the
  // edge + the node's own border pushed inward) marks decision/junction/forEach/startWorkflow as a
  // recognizable group, distinct from normal activities. Lives AT the edge, not in the outer-halo
  // channel, so it composes with selection/live/sim rings. See --np-controlflow-accent.
  const isControl = isControlFlowShape(shape);
  // Port handles anchor at the bbox edge-midpoints; for silhouettes whose vertex isn't at a
  // midpoint, `handleInset` (fraction of the box) pulls that side's handle inward onto the shape.
  const handleInset = getHandleInset(shape);
  const handleAdjustPx: Partial<Record<EdgePortSide, number>> = {
    top: Math.round((handleInset.top ?? 0) * effectiveIconBox),
    right: Math.round((handleInset.right ?? 0) * effectiveIconBox),
    bottom: Math.round((handleInset.bottom ?? 0) * effectiveIconBox),
    left: Math.round((handleInset.left ?? 0) * effectiveIconBox),
  };

  // Schedule trigger inline preview — first upcoming fire as a relative + absolute string.
  // Live, computed client-side via cron-parser. Re-renders on every node update so the value
  // stays sane while the user types in the cron field. Slightly stale at idle (~1 minute);
  // not worth adding a setInterval on every node when the editor canvas is already busy.
  // When the workflow is disabled (__workflowEnabled === false), show "⏸ Paused" instead of
  // the countdown — the Quartz job is actually deleted (see TriggerOrchestrator). The old
  // "still running" display was just a client-side cron calculation with no real status check.
  const workflowDisabled = (data as Record<string, unknown>).__workflowEnabled === false;
  const schedulePreview: { paused: true } | { paused: false; relative: string; absolute: string } | null
    = activityType === 'scheduleTrigger'
      ? (workflowDisabled
          ? { paused: true }
          : (() => {
              const cronStr = (config.cronExpression as string) || '';
              if (!cronStr.trim()) return null;
              const p = previewSchedule(cronStr, 1);
              if (p.error || p.fireTimes.length === 0) return null;
              const next = p.fireTimes[0];
              return { paused: false as const, relative: relativeFromNow(next), absolute: next.toLocaleString(undefined, { hour12: false }) };
            })())
      : null;
  // Author-disabled: the engine treats this node as "skipped" (see WorkflowEngine.ExecuteAsync
  // → disabledNodeIds). We signal that visually with dimmed opacity, a dashed border, and an
  // EyeOff badge; the node stays selectable and editable so the author can re-enable it later.
  const isDisabled = (data.disabled as boolean) === true;
  // Control-group frame thickness (px). 0 when not control or disabled → border/fill fall back to
  // the normal insets so a disabled control node doesn't leave a transparent frame gap.
  const controlFramePx = isControl && !isDisabled ? 2.5 : 0;
  const showEntryMarker = isEntryTrigger && !isDisabled;
  const entryFramePx = showEntryMarker ? 3 : 0;
  const outerFramePx = controlFramePx + entryFramePx;
  const entryAccentColor = 'var(--color-success)';
  // Debugger breakpoint: set by the author in the Properties panel, only takes effect during a
  // debug run. Rendered as a red circle (like an IDE breakpoint) top-left on the node, positioned
  // so it doesn't collide with the simulation label or the fan-in badge.
  const hasBreakpoint = (data.breakpoint as boolean) === true;
  const hasBreakpointCondition = hasBreakpoint && !!(data.breakpointCondition as string);

  // Dry-run simulation: `__simulated` is 'reachable' (indigo outline + ▶ badge), 'revealing'
  // (the step currently animating in — gets an extra pulsing yellow ring), or 'skipped'
  // (dimmed + ⊘ badge). Independent of `__liveStatus`, which reflects an actual run.
  const simulated = data.__simulated as 'reachable' | 'revealing' | 'skipped' | undefined;
  const isSimReachable = simulated === 'reachable' || simulated === 'revealing';
  const isSimRevealing = simulated === 'revealing';
  const isSimSkipped = simulated === 'skipped';

  // Live execution status (set by WorkflowEditorPage based on SignalR events)
  const liveStatus = data.__liveStatus as string | undefined;
  const description = (data.description as string) || '';
  const lintErrors = (data.__lintErrors as number) ?? 0;
  const lintWarnings = (data.__lintWarnings as number) ?? 0;
  const showLintBadge = !liveStatus && (lintErrors > 0 || lintWarnings > 0);
  // Live path-highlight colours, theme-aware (see LIVE_STATUS_STYLE). Dark mode uses deep fills
  // so the highlighted run path doesn't glow garishly on the near-black canvas. Paused keeps its
  // distinct orange (vs Running amber) — it means the Engine is holding at a debugger breakpoint
  // waiting for user input, not just passively running an activity.
  const liveStyle = resolveLiveStyle(liveStatus);
  const isPausedLive = liveStatus === 'Paused';
  // Canvas design #1: asymmetric failure emphasis — only a Failed step gets a soft red glow in
  // dark mode so it stands out on a busy graph; Succeeded/Running stay quiet (fill + border).
  const isFailedGlow = isDark && liveStatus === 'Failed' && !isDisabled;
  // Failure heatmap: when toggled on, override the border with a red tint scaled by failure rate.
  // Live-status takes priority (running/failed colors are more important than historical tint).
  const failureTint = data.__failureTint as number | undefined;
  const heatmapBorder = !liveStatus && failureTint !== undefined && failureTint > 0
    // 55%..100% — even 1% failure is clearly visible
    ? `color-mix(in srgb, var(--color-error) ${Math.round(Math.min(100, 55 + failureTint * 45))}%, transparent)`
    : null;

  // Critical Path overlay: orange glow for nodes on the critical path, slack badge for others.
  // Suppressed during live execution or simulation (live colors win).
  const criticalPath = data.__criticalPath as { isCritical: boolean; slack: number; earliestStart: number; duration: number } | undefined;
  const showCriticalPath = !liveStatus && !simulated && criticalPath?.isCritical;
  const showSlack = !liveStatus && !simulated && criticalPath && !criticalPath.isCritical && criticalPath.slack > 0;

  const effectiveBg = liveStyle?.bgColor ?? ac.bgColor;
  const effectiveBorder = liveStyle?.borderColor
    ?? (showCriticalPath ? 'color-mix(in srgb, var(--color-paused) 85%, transparent)' : null)
    ?? heatmapBorder
    ?? ac.borderColor;

  const machineColorIdx = data.__machineColorIdx as number | undefined;
  const machineStripe = machineColorIdx !== undefined
    ? MACHINE_COLORS[machineColorIdx % MACHINE_COLORS.length].stripe
    : undefined;

  // Premium node styling — only when premiumCanvas is enabled; idle state only within that.
  // All live/heatmap/critical states win via inline override regardless of premiumCanvas.
  const isIdle = !liveStyle && !isDisabled;
  // Thicker border in dark mode: 1.5px renders "frayed" at zoom-out levels; 2px stays crisp.
  const baseBorderPx   = premiumCanvas && isDark && isIdle ? 2 : 1.5;
  const premiumRadius  = premiumCanvas ? Math.min(Math.round(scale.iconBox * 0.37), 16) : undefined;
  const premiumBg      = premiumCanvas && isIdle && isDark ? 'rgba(13,17,22,.92)' : undefined;
  const premiumBgImage = premiumCanvas && isIdle
    ? isDark
      ? 'linear-gradient(180deg, rgba(255,255,255,.13) 0%, rgba(255,255,255,.04) 50%, transparent 100%)'
      : 'linear-gradient(180deg, rgba(255,255,255,.7) 0%, rgba(255,255,255,.15) 60%, transparent 100%)'
    : undefined;
  const premiumShadow  = premiumCanvas && isIdle && !heatmapBorder && !showCriticalPath
    ? isDark
      // Two-layer shadow: tight/opaque for crispness + medium/softer for depth. No large blur radii.
      ? '0 2px 5px rgba(0,0,0,.45), 0 8px 18px rgba(0,0,0,.38), inset 0 1px 0 rgba(255,255,255,.13), inset 0 -1px 0 rgba(0,0,0,.48)'
      : '0 1px 3px rgba(0,0,0,.10), 0 4px 12px rgba(0,0,0,.10), inset 0 1px 0 rgba(255,255,255,.9)'
    : undefined;

  const varFlowRole = data.__varFlowRole as 'producer' | 'consumer' | undefined;

  const health = data.__health as Array<{ status: string }> | undefined;
  const showSparkline = !!health?.length && !liveStatus && !simulated;

  // Coverage Heatmap (Feature 5): tint inactive nodes faintly so the user can spot
  // unreached logic at a glance. `never` = full grey-out; `rare` = subtle amber tint.
  // We deliberately don't replace the existing styling layers — coverage is an overlay,
  // not a takeover, so a node that's currently Running still wins coloring-wise.
  const coverage = data.__coverage as
    | { cls: 'never' | 'rare' | 'common'; executedCount: number; failedCount: number; totalExecutions: number; lastExecutedAt: string | null; windowDays: number }
    | undefined;
  const coverageDim = coverage?.cls === 'never' && !liveStatus && !simulated;
  const coverageRareTint = coverage?.cls === 'rare' && !liveStatus && !simulated;

  const [showTooltip, setShowTooltip] = useState(false);
  const [hovered, setHovered] = useState(false);
  const [tooltipAnchor, setTooltipAnchor] = useState<{ left: number; top: number } | null>(null);
  const hoverTimeout = useRef<ReturnType<typeof setTimeout>>(null);
  const nodeRef = useRef<HTMLDivElement>(null);

  const updateTooltipAnchor = () => {
    const rect = nodeRef.current?.getBoundingClientRect();
    setTooltipAnchor(rect ? { left: rect.left + rect.width / 2, top: rect.top - 12 } : null);
  };

  const handleMouseEnter = () => {
    setHovered(true);
    updateTooltipAnchor();
    hoverTimeout.current = setTimeout(() => {
      updateTooltipAnchor();
      setShowTooltip(true);
    }, 400);
  };
  const handleMouseLeave = () => {
    setHovered(false);
    if (hoverTimeout.current) clearTimeout(hoverTimeout.current);
    setShowTooltip(false);
  };

  const hs = scale.handleSize;
  const inDegree = (data.inDegreeCount as number) ?? 0;

  // Port reveal: with auto-hide on, ports show only when the cursor is near this node (radius),
  // or the node is selected/hovered. A connection-in-progress needs no extra branch — the editor's
  // onPointerMove bubbles up during the drag too, so the same pointer-proximity covers dropping onto
  // a target port. Boolean selector → the node re-renders only when the flag flips, not per move.
  const REVEAL_RADIUS = 48;
  const nearPointer = usePointerFlowPosition(useCallback((s) => {
    if (!autoHidePorts || s.x == null || s.y == null || width == null || height == null) return false;
    const nx = positionAbsoluteX ?? 0, ny = positionAbsoluteY ?? 0;
    const px = Math.max(nx, Math.min(s.x, nx + width));
    const py = Math.max(ny, Math.min(s.y, ny + height));
    return Math.hypot(s.x - px, s.y - py) <= REVEAL_RADIUS;
  }, [autoHidePorts, positionAbsoluteX, positionAbsoluteY, width, height]));
  const portsRevealed = !autoHidePorts || selected || hovered || nearPointer;

  if (nodeStyle === 'classic') {
    return (
      <div
        ref={nodeRef}
        className={`relative flex flex-col items-center ${isDisabled ? 'opacity-50' : ''} ${isSimSkipped ? 'opacity-[0.45]' : ''} ${coverageDim ? 'opacity-40 grayscale' : ''} ${coverageRareTint ? 'opacity-80' : ''}`}
        onMouseEnter={handleMouseEnter}
        onMouseLeave={handleMouseLeave}
        title={
          coverage
            ? t('nodes.coverageTooltip', { days: coverage.windowDays, executed: coverage.executedCount, total: coverage.totalExecutions })
            : undefined
        }
      >
        {/* Classic mode no longer gets a big ring — at a 42px icon size it would turn the whole
            node into a blue blob and hide the activity icon. Instead it gets a small corner
            badge (Will-run ▶ / Skipped ⊘) plus a subtle coloured outline around the icon box;
            see further down in the inner div. */}
        {simulated && <SimulationCornerBadge status={simulated} positionOverride={badgePos.topRight} />}
        {useGlyphIcon ? (
          // Icon view: the bare palette glyph IS the node — no silhouette, no box. Ports,
          // selection/sim rings, live-status and badges hang off an invisible icon-box so every
          // node affordance survives. Same glyph (ac.icon) the left "Actions" palette shows.
          (<div
            className={`relative flex items-center justify-center transition-transform ${
              isSimRevealing ? 'scale-125' :
              isSimReachable ? 'scale-105' :
              (!liveStatus && varFlowRole) ? 'scale-105' :
              selected ? 'scale-110' : 'hover:scale-110'
            }`}
            style={{ width: glyphFs, height: glyphFs }}
          >
            <ActivityPortHandles size={hs} flexible={flexiblePortsEnabled} connectable={isConnectable} revealed={portsRevealed} />
            {/* Status glow behind the bare glyph. The icon view has no box/silhouette to carry a
                coloured border or box-shadow, so the failure-heatmap / critical-path / failed-step
                signals ride a blurred coloured halo (same approach as the shaped render path).
                Without this the failure heatmap was invisible in icon view. */}
            {heatmapBorder && !liveStyle && !isDisabled && (
              <span
                data-testid="heatmap-glow"
                className="absolute inset-[-4px] rounded-lg pointer-events-none"
                style={{ backgroundColor: heatmapBorder, filter: 'blur(5px)', opacity: 0.85 }}
              />
            )}
            {showCriticalPath && !isDisabled && (
              <span
                className="absolute inset-[-4px] rounded-lg pointer-events-none"
                style={{ backgroundColor: 'color-mix(in srgb, var(--color-paused) 70%, transparent)', filter: 'blur(5px)', opacity: 0.85 }}
              />
            )}
            {isFailedGlow && (
              <span
                className="absolute inset-[-4px] rounded-lg pointer-events-none"
                style={{ backgroundColor: 'color-mix(in srgb, var(--color-error) 55%, transparent)', filter: 'blur(5px)' }}
              />
            )}
            {/* Running pulse — ping ring on the invisible box */}
            {liveStyle?.pulse && !isDisabled && (
              <span className="absolute inset-[-2px] rounded-lg animate-ping pointer-events-none" style={{ backgroundColor: effectiveBorder, opacity: 0.4 }} />
            )}
            {/* Selection / simulation / var-flow ring on the invisible box */}
            {(selected || isSimRevealing || isSimReachable || (!liveStatus && varFlowRole)) && (
              <span
                className={`absolute inset-[-3px] rounded-lg pointer-events-none ${isSimRevealing ? 'animate-pulse' : ''}`}
                style={{
                  border: `2px solid ${
                    isSimRevealing ? 'var(--color-running)' :
                    isSimReachable ? 'var(--color-info)' :
                    (!liveStatus && varFlowRole === 'producer') ? 'var(--color-info)' :
                    (!liveStatus && varFlowRole === 'consumer') ? 'var(--color-warning)' :
                    'var(--color-primary)'
                  }`,
                }}
              />
            )}
            {/* The bare palette glyph — the node's main visual */}
            <IconComp
              data-testid="glyph-icon"
              size={glyphFs}
              className={liveStyle?.pulse && !isDisabled ? 'animate-pulse' : undefined}
              style={{
                color: isDisabled ? 'var(--color-skipped)' : (liveStyle?.borderColor ?? (showEntryMarker ? entryAccentColor : ac.color)),
              }}
            />
            {/* Machine-color cue: a small dot (no silhouette to stripe) */}
            {machineStripe && (
              <span data-testid="machine-stripe" className="absolute bottom-0 left-0 w-2 h-2 rounded-full ring-1 ring-white/70 pointer-events-none" style={{ backgroundColor: machineStripe }} />
            )}
            {/* Badges — sized off the glyph font-size, positioned at the (ink-sized) box corners */}
            {inDegree >= 2 && <FanInBadge count={inDegree} activityType={activityType} config={config} iconBox={glyphFs} />}
            {showEntryMarker && inDegree < 2 && <EntryPointBadge iconBox={glyphFs} />}
            {isDisabled && <DisabledBadge iconBox={glyphFs} />}
            {hasBreakpoint && <BreakpointBadge active={isPausedLive} conditional={hasBreakpointCondition} iconBox={glyphFs} />}
            {showLintBadge && <LintBadge errors={lintErrors} warnings={lintWarnings} iconBox={glyphFs} />}
          </div>)
        ) : isShaped ? (
          // Shaped render: a stack of layers using clip-path. CSS `border`/`ring`/`box-shadow`
          // don't follow clip-path, so the shape is simulated with stacked divs instead
          // (border = outer layer, fill = inner layer with a small inset). Selection/pulse/
          // heatmap are additional clip-pathed layers placed behind, so they follow the shape too.
          // Handles: left/right handles are used even in Classic style — top/bottom handles would
          // produce steep Bezier curves on wide fan-outs (large Y distance) that run through
          // neighbouring nodes and get hidden behind their opaque node layer. Side handles keep
          // the Bezier curve horizontal so edges stay visible. The handles live inside this
          // wrapper (not the outer one) so they hug the shape's silhouette instead of the wider
          // label-column edges.
          (<div
            className={`relative transition-transform np-shape-wrap ${
              isSimRevealing ? 'scale-125' :
              isSimReachable ? 'scale-105' :
              !liveStatus && varFlowRole === 'producer' ? 'scale-105' :
              !liveStatus && varFlowRole === 'consumer' ? 'scale-105' :
              selected ? 'scale-110' : 'hover:scale-105'
            }`}
            style={{ width: effectiveIconBox, height: effectiveIconBox, '--np-act-color': ac.color } as React.CSSProperties}
          >
            <ActivityPortHandles
              size={hs}
              flexible={flexiblePortsEnabled}
              connectable={isConnectable}
              adjust={handleAdjustPx}
              revealed={portsRevealed}
            />
            {/* Handles anchor at bbox edge-midpoints; `adjust` (from each shape's handleInset)
                pulls a side inward onto the silhouette where the vertex isn't at the midpoint. */}
            {/* Pulse ring (Running/Paused live status) — clipped, ping-animated */}
            {liveStyle?.pulse && !isDisabled && (
              <div
                className="absolute animate-ping pointer-events-none"
                style={{
                  inset: '-3px',
                  clipPath: shapeClipPath,
                  backgroundColor: effectiveBorder,
                  opacity: 0.5,
                }}
              />
            )}
            {/* Selection / simulation / var-flow ring layer */}
            {(isSimRevealing || isSimReachable || (!liveStatus && varFlowRole) || selected) && (
              <div
                className={`absolute pointer-events-none ${isSimRevealing ? 'animate-pulse' : ''}`}
                style={{
                  inset: isSimRevealing ? '-7px' : '-3px',
                  clipPath: shapeClipPath,
                  backgroundColor:
                    isSimRevealing ? 'var(--color-running)' :
                    isSimReachable ? 'var(--color-info)' :
                    (!liveStatus && varFlowRole === 'producer') ? 'var(--color-info)' :
                    (!liveStatus && varFlowRole === 'consumer') ? 'var(--color-warning)' :
                    'color-mix(in srgb, var(--color-primary) 45%, transparent)',
                }}
              />
            )}
            {/* Heatmap glow — fuzzy red halo behind shape */}
            {heatmapBorder && !liveStyle && !isDisabled && (
              <div
                className="absolute pointer-events-none"
                style={{
                  inset: '-5px',
                  clipPath: shapeClipPath,
                  backgroundColor: heatmapBorder,
                  filter: 'blur(4px)',
                  opacity: 0.85,
                }}
              />
            )}
            {/* Failed-step glow (dark mode) — blurred red halo behind the shape. box-shadow can't
                follow clip-path, so the glow is a separate blurred shape layer (like the heatmap). */}
            {isFailedGlow && (
              <div
                className="absolute pointer-events-none"
                style={{
                  inset: '-5px',
                  clipPath: shapeClipPath,
                  backgroundColor: 'color-mix(in srgb, var(--color-error) 55%, transparent)',
                  filter: 'blur(5px)',
                }}
              />
            )}
            {/* Critical-path glow — orange halo (box-shadow can't follow clip-path, so a blurred
                clipped layer like the heatmap). Gated off the 3 legacy shapes so pennant/diamond/
                flag keep their prior (border-only) critical-path look. The orange border itself is
                already produced via effectiveBorder for every shaped node. */}
            {showCriticalPath && !isBookendShape && !isDisabled && (
              <div
                className="absolute pointer-events-none"
                style={{ inset: '-5px', clipPath: shapeClipPath, backgroundColor: 'color-mix(in srgb, var(--color-paused) 60%, transparent)', filter: 'blur(5px)' }}
              />
            )}
            {/* Control-group frame — shared indigo outer silhouette. The border layer below is
                pushed inward by controlFramePx, so this peeks out as a double-outline that marks
                the control-flow group. Always-on (incl. live/selected): it sits at the edge, a
                different channel than the rings, and the inner border still carries run colour. */}
            {isControl && !isDisabled && (
              <div
                data-testid="control-accent"
                className="absolute inset-0 pointer-events-none"
                style={{ clipPath: shapeClipPath, backgroundColor: 'var(--np-controlflow-accent)' }}
              />
            )}
            {/* Entry-point frame — marks enabled trigger nodes as workflow start points without
                changing their pennant shape or activity-specific fill colour. */}
            {showEntryMarker && (
              <div
                data-testid="entry-accent"
                className="absolute inset-0 pointer-events-none"
                style={{ clipPath: shapeClipPath, backgroundColor: entryAccentColor }}
              />
            )}
            {/* Border layer — solid color rendered as the shape silhouette */}
            <div
              className="absolute pointer-events-none"
              style={{
                inset: `${outerFramePx}px`,
                clipPath: shapeClipPath,
                backgroundColor: isDisabled ? 'var(--color-skipped)' : (heatmapBorder && !liveStyle ? heatmapBorder : effectiveBorder),
              }}
            />
            {/* Fill layer — inset by border-thickness, gives the visible "border ring" effect */}
            <div
              className="absolute pointer-events-none"
              style={{
                inset: `${(heatmapBorder && !liveStyle && !isDisabled ? 3 : baseBorderPx) + outerFramePx}px`,
                clipPath: shapeClipPath,
                backgroundColor: premiumBg ?? effectiveBg,
                backgroundImage: premiumBgImage,
              }}
            />
            {/* Machine-color indicator (parity with the square branch's left stripe). A 1px bar
                gets sliced by non-rect silhouettes, so paint the machine colour as the left ~9%
                of a full-box gradient, clipped to the shape → reads as a shape-aware left stripe.
                Data-gated (only REMOTE activities get __machineColorIdx) → never on the 3 legacy
                shapes (none are remote). */}
            {machineStripe && (
              <div
                data-testid="machine-stripe"
                className="absolute inset-0 pointer-events-none"
                style={{ clipPath: shapeClipPath, background: `linear-gradient(to right, ${machineStripe} 0 9%, transparent 9%)` }}
              />
            )}
            {/* Dashed-disabled cue: subtle stripe pattern overlay (clip-path can't do dashed border directly) */}
            {(liveStyle?.dashed || isDisabled) && (
              <div
                className="absolute pointer-events-none"
                style={{
                  inset: '1.5px',
                  clipPath: shapeClipPath,
                  background: 'repeating-linear-gradient(45deg, rgba(156,163,175,0.18) 0 4px, transparent 4px 8px)',
                }}
              />
            )}
            {/* Icon (centered) — not clipped so it always renders fully even at small scales.
                Sized to `iconFont × iconScale` (iconScale 1.0 = same px as a square node's icon,
                so inside-icons read equal across all shapes), plus the per-shape X/Y offset so
                asymmetric glyphs seat on their visual center (e.g. speechBubble body sits high). */}
            <div className="absolute inset-0 flex items-center justify-center pointer-events-none">
              <IconComp
                size={shapedIconFs}
                className={liveStyle?.pulse && !isDisabled ? 'animate-pulse' : undefined}
                style={{ ...shapedIconStyle, color: ac.color }}
              />
            </div>
            {/* Badges — positioned via shape-aware overrides so they sit on the visible silhouette */}
            {inDegree >= 2 && <FanInBadge count={inDegree} activityType={activityType} config={config} iconBox={effectiveIconBox} positionOverride={badgePos.topMiddle} />}
            {showEntryMarker && inDegree < 2 && <EntryPointBadge iconBox={effectiveIconBox} positionOverride={badgePos.topMiddle} />}
            {isDisabled && <DisabledBadge iconBox={effectiveIconBox} positionOverride={badgePos.topRight} />}
            {hasBreakpoint && <BreakpointBadge active={isPausedLive} conditional={hasBreakpointCondition} iconBox={effectiveIconBox} positionOverride={badgePos.topLeft} />}
            {showLintBadge && <LintBadge errors={lintErrors} warnings={lintWarnings} iconBox={effectiveIconBox} positionOverride={badgePos.bottomRight} />}
          </div>)
        ) : (
          // Square path: keep the handles in the icon-box wrapper so they hug the icon edge
          // instead of the OUTER wrapper (whose width is dominated by the label → otherwise the
          // handles would sit far left/right of the icon in empty space).
          (<div className="relative np-btn-node-wrap" style={{ width: scale.iconBox, height: scale.iconBox, '--np-act-color': ac.color } as React.CSSProperties}>
            <ActivityPortHandles size={hs} flexible={flexiblePortsEnabled} connectable={isConnectable} revealed={portsRevealed} />
            {/* Running indicator: expanding ping ring behind the node */}
            {liveStyle?.pulse && !isDisabled && (
              <span
                className="absolute inset-0 rounded-lg animate-ping"
                style={{ backgroundColor: effectiveBorder, opacity: 0.5 }}
              />
            )}
            <div
              className={`relative rounded-lg flex items-center justify-center shadow-sm transition-all np-btn-node-inner ${
                // Revealing state: scales up + pulses in a warm amber ("this one is running
                // right now"). Reachable (already revealed): a quiet indigo outline. The
                // difference makes the animation tangible: a step "flashes briefly" and then
                // settles into the stable reached state.
                isSimRevealing ? 'ring-4 ring-running ring-offset-2 scale-125 animate-[pulse_0.6s_ease-in-out_infinite]' :
                isSimReachable ? 'ring-2 ring-info ring-offset-1 scale-105' :
                !liveStatus && varFlowRole === 'producer' ? 'ring-2 ring-info ring-offset-1 scale-105' :
                !liveStatus && varFlowRole === 'consumer' ? 'ring-2 ring-warning ring-offset-1 scale-105' :
                selected ? 'ring-2 ring-primary/50 ring-offset-1 scale-110' : 'hover:scale-105'
              } ${liveStyle?.pulse && !isDisabled ? 'ring-4 ring-offset-1 animate-[pulse_1s_ease-in-out_infinite]' : ''}`}
              style={{
                width: scale.iconBox,
                height: scale.iconBox,
                borderRadius: premiumRadius,
                backgroundColor: premiumBg ?? effectiveBg,
                backgroundImage: premiumBgImage,
                // Disabled wins visually over live status: a disabled step is always dashed +
                // grey, even if it carries a "Succeeded" status from an earlier execution.
                // Without this rule it wouldn't be clear that it will now be skipped.
                // The heatmap border is deliberately thicker (3px) with a red drop-shadow glow,
                // so a "failing step" stays clearly visible even at a small node scale.
                border: `${(liveStyle?.dashed || isDisabled) ? '1.5px dashed' : ((heatmapBorder && !liveStyle) || showCriticalPath ? '3px solid' : `${baseBorderPx}px solid`)} ${isDisabled ? 'var(--color-skipped)' : effectiveBorder}`,
                ...(premiumShadow ? { boxShadow: premiumShadow } : {}),
                ...(heatmapBorder && !liveStyle && !isDisabled ? { boxShadow: `0 0 12px ${heatmapBorder}` } : {}),
                ...(showCriticalPath ? { boxShadow: '0 0 12px color-mix(in srgb, var(--color-paused) 60%, transparent)' } : {}),
                ...(isFailedGlow ? { boxShadow: FAILED_GLOW_SHADOW } : {}),
                ...(liveStyle?.pulse && !isDisabled ? { '--tw-ring-color': effectiveBorder } as React.CSSProperties : {}),
              }}
            >
              {machineStripe && (
                <div className="absolute left-0 top-0 bottom-0 w-1 rounded-l-lg pointer-events-none" style={{ backgroundColor: machineStripe }} />
              )}
              <IconComp
                size={scale.iconFont}
                className={liveStyle?.pulse && !isDisabled ? 'animate-pulse' : undefined}
                style={{ color: ac.color }}
              />
              {inDegree >= 2 && <FanInBadge count={inDegree} activityType={activityType} config={config} iconBox={scale.iconBox} />}
              {isDisabled && <DisabledBadge iconBox={scale.iconBox} />}
              {hasBreakpoint && <BreakpointBadge active={isPausedLive} conditional={hasBreakpointCondition} iconBox={scale.iconBox} />}
              {showLintBadge && <LintBadge errors={lintErrors} warnings={lintWarnings} iconBox={scale.iconBox} />}
            </div>
          </div>)
        )}
        <span
          className={`mt-0.5 font-label font-medium text-center leading-tight ${
            selected ? 'text-primary' : 'text-on-surface'
          }`}
          style={{ fontSize: `${effectiveLabelFont}px`, maxWidth: effectiveLabelWidth }}
        >{label}</span>
        {schedulePreview && (schedulePreview.paused ? (
          <div className="text-[9px] font-mono text-on-surface-variant/60 mt-0.5 truncate" style={{ maxWidth: effectiveLabelWidth }} title={t('nodes.triggerPaused')}>
            {t('nodes.pausedShort')}
          </div>
        ) : (
          <div className="flex items-center gap-0.5 text-[9px] font-mono text-warning mt-0.5 truncate" style={{ maxWidth: effectiveLabelWidth }} title={t('nodes.nextFire', { time: schedulePreview.absolute })}>
            <Timer size={9} className="shrink-0" /> {schedulePreview.relative}
          </div>
        ))}
        {showSparkline && <HealthSparkline entries={health!} />}
        {showSlack && (
          <div className="absolute -top-2 -right-2 bg-paused-container text-on-paused-container text-[9px] font-label font-bold px-1 py-0.5 rounded shadow-sm pointer-events-none" title={t('nodes.slackTooltip', { slack: (criticalPath!.slack / 1000).toFixed(1) })}>
            +{(criticalPath!.slack / 1000).toFixed(1)}s
          </div>
        )}
        {description && (
          <div
            className="absolute bottom-0 right-0 text-on-surface-variant/60 pointer-events-none"
            title={description}
          >
            <Chat size={10} />
          </div>
        )}
        <Tooltip
          show={showTooltip} label={label} summary={summary} icon={ac.icon} color={ac.color}
          fontSize={scale.tooltipFont} maxWidth={scale.tooltipMaxWidth}
          description={data.description as string | undefined}
          outputVar={(data.outputVariable as string) || undefined}
          retryConfig={(data.config as Record<string, unknown>)?.retry as Record<string, unknown> | undefined}
          health={health}
          stats={data.__stats as { totalRuns: number; failureRate: number; avgDurationMs: number; p95DurationMs: number; lastDurationMs: number } | undefined}
          anchor={tooltipAnchor}
        />
      </div>
    );
  }

  // Card style (Material Design 3).
  // Simulation styles take precedence over idle/selected rings, so the dry-run stays clearly
  // visible. Revealing → pulsing amber ring + scale (the sequential reveal animation highlights
  // the currently "active" step). Reachable → a quiet indigo ring (already revealed).
  // Skipped → opacity 35% + dashed grey (only once the reveal animation has finished).
  const simRingCls = isSimRevealing ? 'ring-4 ring-running shadow-[0_0_28px_rgba(251,191,36,0.6)] scale-[1.04]'
    : isSimReachable ? 'ring-4 ring-info/70 shadow-[0_0_20px_rgba(99,102,241,0.35)]'
    : isSimSkipped ? 'opacity-[0.35] [border:2px_dashed_var(--color-skipped)]'
    : !liveStatus && varFlowRole === 'producer' ? 'ring-2 ring-info'
    : !liveStatus && varFlowRole === 'consumer' ? 'ring-2 ring-warning'
    : '';
  const showEntryCardAccent = showEntryMarker && !liveStatus && !simulated && !selected
    && !heatmapBorder && !showCriticalPath && !isFailedGlow;
  return (
    <div
      ref={nodeRef}
      className={`relative bg-surface-lowest flex flex-col min-w-[220px] max-w-[280px] transition-all rounded-xl shadow-[var(--np-elev-1)] hover:shadow-[var(--np-elev-2)] ${
        simRingCls || (selected ? 'ring-2 ring-primary/50 shadow-[var(--np-elev-2)]' : 'ring-1 ring-outline-variant/20')
      } ${isDisabled ? 'opacity-50 [border:1.5px_dashed_var(--color-skipped)]' : ''} ${coverageDim ? 'opacity-40 grayscale' : ''} ${coverageRareTint ? 'opacity-80' : ''}`}
      style={(heatmapBorder && !liveStatus && !isDisabled)
        ? { border: `3px solid ${heatmapBorder}`, boxShadow: `0 0 14px ${heatmapBorder}` }
        : showCriticalPath && !isDisabled
          ? { border: '3px solid color-mix(in srgb, var(--color-paused) 85%, transparent)', boxShadow: '0 0 14px color-mix(in srgb, var(--color-paused) 60%, transparent)' }
          : isFailedGlow
            ? { boxShadow: FAILED_GLOW_SHADOW }
            : undefined}
      title={coverage
        ? t('nodes.coverageTooltip', { days: coverage.windowDays, executed: coverage.executedCount, total: coverage.totalExecutions })
        : showCriticalPath
          ? t('nodes.criticalPathTooltip', { duration: criticalPath!.duration })
          : showSlack
            ? t('nodes.slackTooltip', { slack: (criticalPath!.slack / 1000).toFixed(1) })
            : undefined}
      onMouseEnter={handleMouseEnter}
      onMouseLeave={handleMouseLeave}
    >
      {inDegree >= 2 && <FanInBadge count={inDegree} activityType={activityType} config={config} iconBox={scale.iconBox} />}
      {isDisabled && <DisabledBadge iconBox={scale.iconBox} />}
      {hasBreakpoint && <BreakpointBadge active={isPausedLive} />}
      {simulated && <SimulationBadge status={simulated} />}
      {showLintBadge && <LintBadge errors={lintErrors} warnings={lintWarnings} />}
      {showEntryCardAccent && (
        <div
          data-testid="entry-accent"
          className="absolute inset-0 rounded-md pointer-events-none"
          style={{ outline: `2px solid ${entryAccentColor}`, outlineOffset: 2 }}
        />
      )}
      <ActivityPortHandles size={12} flexible={flexiblePortsEnabled} connectable={isConnectable} borderWidth={2} revealed={portsRevealed} />
      <div className={`h-10 flex items-center px-4 border-l-2 relative rounded-t-xl ${liveStyle?.pulse ? 'animate-[pulse_1s_ease-in-out_infinite] ring-2 ring-offset-0' : ''}`}
        style={{
          borderLeftColor: liveStyle?.borderColor ?? machineStripe ?? (showEntryMarker ? entryAccentColor : ac.color),
          backgroundColor: liveStyle ? liveStyle.bgColor : (selected ? `${ac.color}08` : 'var(--color-surface-low)'),
          ...(liveStyle?.pulse ? { '--tw-ring-color': liveStyle.borderColor } as React.CSSProperties : {}),
        }}>
        {liveStyle?.pulse && (
          <span className="absolute -left-1 -right-1 -top-1 -bottom-1 rounded-md animate-ping pointer-events-none"
                style={{ backgroundColor: liveStyle.borderColor, opacity: 0.25 }} />
        )}
        {isShaped ? (
          // Card mode: a compact shape-clipped icon container in the header. Visually sets
          // triggers/control-flow/returnData apart without breaking the card itself out of its
          // rectangular shape (readability of the summary/description takes priority).
          (<div
            className={`mr-2 relative flex items-center justify-center shrink-0 ${liveStyle?.pulse ? 'animate-pulse' : ''}`}
            style={{ width: 26, height: 26 }}
            title={label}
          >
            {/* Shape-silhouette layer (border color; control nodes get the indigo group accent) */}
            <div
              className="absolute inset-0 pointer-events-none"
              style={{ clipPath: shapeClipPath, backgroundColor: isControl ? 'var(--np-controlflow-accent)' : (showEntryMarker ? entryAccentColor : ac.borderColor) }}
            />
            {/* Shape-fill layer */}
            <div
              className="absolute pointer-events-none"
              style={{ inset: 1, clipPath: shapeClipPath, backgroundColor: ac.bgColor }}
            />
            <IconComp
              size={15}
              className="relative"
              style={{ color: ac.color, ...iconTransformFor(26) }}
            />
          </div>)
        ) : (
          <span
            className={`mr-2 relative flex items-center justify-center shrink-0 rounded-[var(--np-radius-sm)] ${liveStyle?.pulse ? 'animate-pulse' : ''}`}
            style={{ width: 26, height: 26, backgroundColor: ac.bgColor, border: `1px solid ${ac.borderColor}` }}
          >
            <IconComp size={15} style={{ color: ac.color }} />
          </span>
        )}
        {showEntryMarker && <EntryPointBadge iconBox={scale.iconBox} inline />}
        <span className="font-headline text-sm font-semibold text-on-surface truncate relative min-w-0">{label}</span>
        {description && (
          <span className="ml-auto shrink-0 relative text-on-surface-variant/50 pl-1" title={description}>
            <Chat size={11} />
          </span>
        )}
      </div>
      {(summary || showSparkline || schedulePreview) && (
        <div className="px-4 py-2.5 flex flex-col gap-1">
          {summary && (activityType === 'restApi' && config.method ? (
            <div className="flex items-center gap-2">
              <span className="px-1.5 py-0.5 bg-secondary-container text-on-secondary-container text-[10px] font-bold rounded">
                {(config.method as string) || 'GET'}
              </span>
              <span className="font-mono text-[11px] text-on-surface-variant truncate">{(config.url as string) || '...'}</span>
            </div>
          ) : activityType === 'startWorkflow' && config.workflowNameOrId ? (
            <SubWorkflowPreviewLink
              nameOrId={(config.workflowNameOrId as string) || ''}
              summary={summary.split('\n')[0]}
            />
          ) : (
            <span className="font-mono text-[11px] text-on-surface-variant truncate">{summary.split('\n')[0]}</span>
          ))}
          {schedulePreview && (schedulePreview.paused ? (
            <div className="flex items-center gap-1.5 text-[10px] font-mono text-on-surface-variant/60" title={t('nodes.triggerPaused')}>
              <PauseFilled size={10} />
              <span className="truncate">{t('nodes.paused')}</span>
            </div>
          ) : (
            <div className="flex items-center gap-1.5 text-[10px] font-mono text-warning" title={t('nodes.nextFire', { time: schedulePreview.absolute })}>
              <Time size={10} />
              <span className="truncate">{t('nodes.next', { time: schedulePreview.relative })}</span>
            </div>
          ))}
          {showSparkline && <HealthSparkline entries={health!} />}
          {showSlack && (
            <div className="bg-paused-container text-on-paused-container text-[9px] font-label font-bold px-1 py-0.5 rounded" title={t('nodes.slackTooltip', { slack: (criticalPath!.slack / 1000).toFixed(1) })}>
              {t('nodes.slackBadge', { slack: (criticalPath!.slack / 1000).toFixed(1) })}
            </div>
          )}
        </div>
      )}
      <Tooltip
        show={showTooltip} label={label} summary={summary} icon={ac.icon} color={ac.color}
        fontSize={scale.tooltipFont} maxWidth={scale.tooltipMaxWidth}
        description={data.description as string | undefined}
        outputVar={(data.outputVariable as string) || undefined}
        retryConfig={(data.config as Record<string, unknown>)?.retry as Record<string, unknown> | undefined}
        health={health}
        anchor={tooltipAnchor}
      />
    </div>
  );
}

/**
 * `memo` protects the expensive ActivityNode component from re-reconciling synchronously on
 * every React Flow tick (e.g. node drag, another node's selection change, viewport pan). On
 * 200-node graphs this was measurable — every useDesignStore update (snap toggle, node-style
 * switch) or pan tick walked all 200 nodes.
 *
 * React Flow calls the node component with `data`/`selected`/`isConnectable` as props. Its
 * internal reducer creates a **new `data` reference** whenever `node.data` actually changed
 * (e.g. a live-status update, a config edit). Reference equality on `data` is therefore the
 * right cut-point: same reference → no content change → the re-render can be skipped.
 *
 * The component's own useDesignStore subscriptions (nodeStyle, flexiblePortsEnabled, etc.)
 * still trigger re-renders independently of this memo — that's intentional, otherwise style
 * switches wouldn't take effect.
 */
export const ActivityNode = memo(ActivityNodeImpl, (prev, next) => {
  return prev.data === next.data
    && prev.selected === next.selected
    && prev.isConnectable === next.isConnectable
    // Position/size feed the cursor-proximity port reveal — RF moves the wrapper without a
    // re-render, so without these a moved node would compute proximity against a stale rect.
    && prev.positionAbsoluteX === next.positionAbsoluteX
    && prev.positionAbsoluteY === next.positionAbsoluteY
    && prev.width === next.width
    && prev.height === next.height;
});

function ActivityPortHandles({
  size,
  flexible,
  connectable = true,
  adjust,
  borderWidth = 1,
  revealed = true,
}: Readonly<{
  size: number;
  flexible: boolean;
  connectable?: boolean;
  /** px to pull each port inward onto the silhouette (per shape's handleInset). */
  adjust?: Partial<Record<EdgePortSide, number>>;
  borderWidth?: 1 | 2;
  /** When false, active ports render at opacity 0 (auto-hide) — handles stay present + connectable. */
  revealed?: boolean;
}>) {
  const { t } = useTranslation('designer');
  return (
    <>
      {EDGE_PORT_SIDES.map((side) => {
        const visibleInClassicMode = side === 'left' || side === 'right';
        const visible = (flexible || visibleInClassicMode) && revealed;
        const canStart = !!connectable && (flexible || side === 'right');
        const canEnd = !!connectable && (flexible || side === 'left');
        return (
          <Handle
            key={side}
            id={side}
            type="source"
            position={portToPosition(side)}
            isConnectable={!!connectable}
            isConnectableStart={canStart}
            isConnectableEnd={canEnd}
            style={portHandleStyle(side, size, adjust?.[side] ?? 0, visible)}
            className={`np-port-dot ${borderWidth === 2 ? '!border-2' : '!border'} !border-surface-lowest !rounded-full transition-opacity`}
            title={flexible ? t('nodes.connectSide', { side: t(`edgePort.${side}`) }) : side === 'right' ? t('nodes.output') : side === 'left' ? t('nodes.input') : undefined}
          />
        );
      })}
    </>
  );
}

function portHandleStyle(side: EdgePortSide, size: number, adjust: number, visible: boolean): React.CSSProperties {
  const base: React.CSSProperties = {
    width: size,
    height: size,
    opacity: visible ? 1 : 0,
    zIndex: 20,
  };
  // `adjust` moves the handle inward from the bbox edge onto the silhouette (per shape).
  switch (side) {
    case 'top':
      return { ...base, top: -size / 2 + adjust, left: '50%', transform: 'translateX(-50%)' };
    case 'right':
      return { ...base, right: -size / 2 + adjust, top: '50%', transform: 'translateY(-50%)' };
    case 'bottom':
      return { ...base, bottom: -size / 2 + adjust, left: '50%', transform: 'translateX(-50%)' };
    case 'left':
      return { ...base, left: -size / 2 + adjust, top: '50%', transform: 'translateY(-50%)' };
  }
}

/* ---- Entry-point Badge (workflow start marker) ---- */

function EntryPointBadge({
  iconBox,
  positionOverride,
  inline = false,
}: Readonly<{ iconBox: number; positionOverride?: BadgePosition; inline?: boolean }>) {
  const { t } = useTranslation('designer');
  const size = inline ? 17 : Math.max(15, Math.round(iconBox * 0.33));
  const iconFont = inline ? 13 : Math.max(10, Math.round(size * 0.72));
  const defaultPos = { top: -size / 2, left: '50%', transform: 'translateX(-50%)' };
  const title = t('nodes.entryPoint');

  if (inline) {
    return (
      <span
        data-testid="entry-badge"
        className="mr-1.5 shrink-0 inline-flex items-center justify-center rounded-full bg-emerald-600 text-white shadow-sm ring-1 ring-white/90 dark:bg-emerald-400 dark:text-emerald-950 dark:ring-emerald-950/40"
        style={{ width: size, height: size }}
        title={title}
      >
        <PlayFilledAlt size={iconFont} />
      </span>
    );
  }

  return (
    <div
      data-testid="entry-badge"
      className="absolute z-10 rounded-full bg-emerald-600 text-white shadow-md ring-2 ring-white pointer-events-none flex items-center justify-center dark:bg-emerald-400 dark:text-emerald-950 dark:ring-emerald-950/60"
      style={{
        width: size,
        height: size,
        ...defaultPos,
        ...(positionOverride ?? {}),
      }}
      title={title}
    >
      <PlayFilledAlt size={iconFont} />
    </div>
  );
}

/* ---- Breakpoint Badge (Debugger) ---- */

/** Red circle, like an IDE breakpoint. Positioned top-left on the node so it doesn't collide
 *  with the fan-in badge (top-middle) or the disabled badge (top-right). When the step is
 *  CURRENTLY stopped at this breakpoint (liveStatus=Paused), the circle also pulses.
 *  `positionOverride` replaces the default position for special shapes (pennant/diamond/flag). */
function BreakpointBadge({ active, conditional = false, iconBox = 20, positionOverride }: Readonly<{ active: boolean; conditional?: boolean; iconBox?: number; positionOverride?: BadgePosition }>) {
  const { t } = useTranslation('designer');
  const size = Math.max(10, Math.round(iconBox * 0.45));
  const title = active
    ? t('nodes.breakpoint.paused')
    : conditional
      ? t('nodes.breakpoint.conditional')
      : t('nodes.breakpoint.set');
  const defaultPos = { top: -4, left: -4 };
  return (
    <div
      className={`absolute z-10 rounded-full shadow-md pointer-events-none flex items-center justify-center
        ${conditional ? 'bg-amber-500 ring-2 ring-white' : 'bg-red-600 ring-2 ring-white'}
        ${active ? 'animate-pulse' : ''}`}
      style={{ width: size, height: size, ...defaultPos, ...(positionOverride ?? {}) }}
      title={title}
    >
      {conditional && <span className="text-white font-bold leading-none" style={{ fontSize: Math.max(6, size * 0.55) }}>?</span>}
    </div>
  );
}

/* ---- Simulation Badge (Dry-Run visualization) ---- */

/** Text badge in the top-left — "Will run" (indigo) or "Skipped" (grey, strikethrough).
 *  Card mode has enough room for a spelled-out label. Deliberately placed TOP-LEFT so it
 *  doesn't collide with the fan-in badge or the disabled EyeOff badge (both top-right). */
function SimulationBadge({ status }: Readonly<{ status: 'reachable' | 'revealing' | 'skipped' }>) {
  const { t } = useTranslation('designer');
  const isReachable = status === 'reachable' || status === 'revealing';
  const isRevealing = status === 'revealing';
  return (
    <div
      className={`absolute -top-2 left-2 z-10 px-1.5 py-0.5 rounded-full text-[9px] font-label font-bold uppercase tracking-wider shadow-sm pointer-events-none whitespace-nowrap ${
        isRevealing ? 'bg-amber-400 text-amber-950 animate-pulse'
        : isReachable ? 'bg-indigo-600 text-white'
        : 'bg-outline text-white line-through'
      }`}
    >
      {isRevealing ? t('nodes.simulation.running') : isReachable ? t('nodes.simulation.ran') : t('nodes.simulation.skipped')}
    </div>
  );
}

/** Compact corner dot for Classic-mode icons — at a 42px icon size there's no room for a
 *  spelled-out label, so this is a 14px circle in the top-right with a checkmark (will run)
 *  or a cross symbol (skipped). The corner dot stays visible even when zoomed out.
 *  `positionOverride` adjusts its position for special shapes (whose bounding-box corners
 *  are clipped). */
function SimulationCornerBadge({ status, positionOverride }: Readonly<{ status: 'reachable' | 'revealing' | 'skipped'; positionOverride?: BadgePosition }>) {
  const { t } = useTranslation('designer');
  const isRevealing = status === 'revealing';
  const isReachable = status === 'reachable' || isRevealing;
  const defaultPos = { top: -4, right: -4 };
  return (
    <div
      className={`absolute z-10 rounded-full flex items-center justify-center text-white shadow-md ring-2 ring-white pointer-events-none font-bold ${
        isRevealing ? 'bg-amber-400 text-amber-950 animate-ping-slow'
        : isReachable ? 'bg-indigo-600'
        : 'bg-outline'
      }`}
      style={{
        width: isRevealing ? 20 : 16,
        height: isRevealing ? 20 : 16,
        ...defaultPos,
        ...(positionOverride ?? {}),
        fontSize: isRevealing ? 13 : 11,
        lineHeight: 1,
      }}
      title={isRevealing ? t('nodes.simulation.runningNow') : isReachable ? t('nodes.simulation.ranDryRun') : t('nodes.simulation.skippedDryRun')}
    >
      {isRevealing ? '▶' : isReachable ? '✓' : '⊘'}
    </div>
  );
}

/* ---- Disabled Badge (EyeOff overlay for author-disabled nodes) ---- */

function DisabledBadge({ iconBox, positionOverride }: Readonly<{ iconBox: number; positionOverride?: BadgePosition }>) {
  const { t } = useTranslation('designer');
  // Scales with the node (smaller icons → smaller badge). Positioned top-right — the fan-in
  // badge occupies top-middle, so there's no layout conflict.
  const size = Math.max(14, Math.round(iconBox * 0.34));
  const iconFont = Math.max(9, Math.round(size * 0.62));
  const defaultPos = { top: -size / 3, right: -size / 3 };
  return (
    <div
      className="absolute rounded-full bg-surface-lowest border border-outline-variant/60 shadow-sm flex items-center justify-center pointer-events-none"
      style={{
        width: size,
        height: size,
        ...defaultPos,
        ...(positionOverride ?? {}),
      }}
      title={t('nodes.disabledTooltip')}
    >
      <ViewOff size={iconFont} className="text-on-surface-variant" />
    </div>
  );
}

/* ---- Fan-in Badge (auto Junction indicator) ---- */

function FanInBadge({ count, activityType, config, iconBox, positionOverride }: Readonly<{
  count: number;
  activityType: string;
  config: Record<string, unknown>;
  iconBox: number;
  positionOverride?: BadgePosition;
}>) {
  const { t } = useTranslation('designer');
  const isJunction = activityType === 'junction';
  const mode = isJunction ? ((config.mode as string) || 'waitAll') : 'implicit';
  const n = (config.n as number) ?? (config.requiredCount as number) ?? 1;

  const b = iconBox;
  const size = Math.max(14, Math.round(b * 0.30));
  const fontSize = Math.max(9, Math.round(b * 0.18));
  const padX = Math.max(3, Math.round(b * 0.06));
  const offset = Math.max(3, Math.round(b * 0.11));

  const variants: Record<string, { label: string; tip: string; bgCls: string; textCls: string }> = {
    waitAll: {
      label: String(count),
      tip: t('nodes.fanIn.waitAll', { count }),
      bgCls: 'bg-indigo-500',
      textCls: 'text-white',
    },
    waitAny: {
      label: String(count),
      tip: t('nodes.fanIn.waitAny', { count }),
      bgCls: 'bg-amber-400',
      textCls: 'text-amber-950',
    },
    waitNofM: {
      label: `${n}/${count}`,
      tip: t('nodes.fanIn.waitNofM', { n, count }),
      bgCls: 'bg-emerald-500',
      textCls: 'text-white',
    },
    implicit: {
      label: String(count),
      tip: t('nodes.fanIn.implicit', { count }),
      bgCls: 'bg-slate-500',
      textCls: 'text-slate-100',
    },
  };

  const v = variants[mode] ?? variants.implicit;

  return (
    <div
      className={`absolute z-20 flex items-center justify-center rounded-full
                  ${v.bgCls} ${v.textCls}
                  shadow-sm ring-2 ring-white
                  select-none pointer-events-none`}
      style={{
        top: -offset,
        right: -offset,
        ...(positionOverride ?? {}),
        minWidth: size,
        height: size,
        paddingLeft: padX,
        paddingRight: padX,
        fontSize,
        fontWeight: 700,
        lineHeight: 1,
      }}
      title={v.tip}
    >
      {v.label}
    </div>
  );
}

/* ---- Lint Badge ---- */

function LintBadge({ errors, warnings, iconBox, positionOverride }: Readonly<{ errors: number; warnings: number; iconBox?: number; positionOverride?: BadgePosition }>) {
  const { t } = useTranslation('designer');
  const isError = errors > 0;
  const count = isError ? errors : warnings;
  // Scale 1:1 with the icon-size slider in Classic mode — without iconBox (Card mode) the
  // card itself is fixed-width, so the badge stays compact.
  const b = iconBox;
  const size = b ? Math.max(14, Math.round(b * 0.30)) : 16;
  const fontSize = b ? Math.max(9, Math.round(b * 0.18)) : 9;
  const padX = b ? Math.max(3, Math.round(b * 0.06)) : 4;
  const inset = b ? Math.max(3, Math.round(b * 0.05)) : 4;
  const defaultPos = { bottom: inset, right: inset };
  return (
    <div
      className={`absolute z-10 rounded-full
        flex items-center justify-center pointer-events-none shadow-sm
        ${isError ? 'bg-error text-on-error' : 'bg-amber-400 text-amber-950'}`}
      style={{
        ...defaultPos,
        ...(positionOverride ?? {}),
        minWidth: size, height: size,
        paddingLeft: padX, paddingRight: padX,
        fontSize, fontWeight: 700, lineHeight: 1,
      }}
      title={isError
        ? t('nodes.lint.errors', { count: errors })
        : t('nodes.lint.warnings', { count: warnings })}
    >
      {count > 9 ? '9+' : count}
    </div>
  );
}

/* ---- Sub-Workflow Preview Link ---- */

/**
 * Inline affordance on `startWorkflow` nodes — shows the referenced child workflow's
 * name + a small "expand" icon. Click opens the preview modal in the parent editor via
 * the SubWorkflowPreviewContext callback (no callback in node `data` so memo() stays
 * cheap). Uses `nodrag`/`nopan` and `stopPropagation` so React Flow's pane handler
 * doesn't swallow the click and start a drag instead.
 */
function SubWorkflowPreviewLink({ nameOrId, summary }: Readonly<{ nameOrId: string; summary: string }>) {
  const { t } = useTranslation('designer');
  const { onPreviewSubWorkflow } = useContext(SubWorkflowPreviewContext);
  const isTemplate = nameOrId.trim().startsWith('{{');
  return (
    <button
      type="button"
      onMouseDown={(e) => e.stopPropagation()}
      onClick={(e) => {
        e.stopPropagation();
        if (isTemplate) return;
        onPreviewSubWorkflow(nameOrId);
      }}
      disabled={isTemplate}
      className={`nodrag nopan flex items-center gap-1.5 -m-1 p-1 rounded-sm text-left transition-colors group ${
        isTemplate
          ? 'cursor-default'
          : 'cursor-pointer hover:bg-primary-fixed/30'
      }`}
      title={isTemplate
        ? t('nodes.subWorkflow.previewUnavailable')
        : t('nodes.subWorkflow.openPreview')}
      data-testid="subworkflow-preview-link"
    >
      <FolderTree size={11} className="text-on-surface-variant shrink-0 group-hover:text-primary transition-colors" />
      <span className="font-label text-xs text-on-surface-variant truncate flex-1">{summary}</span>
    </button>
  );
}

/* ---- Health Sparkline ---- */

function HealthSparkline({ entries }: Readonly<{ entries: Array<{ status: string }> }>) {
  const { t } = useTranslation('designer');
  const dots = [...entries].reverse(); // oldest first (left→right)
  return (
    <div className="flex items-center gap-0.5 mt-0.5 justify-center" title={t('nodes.healthSparkline')}>
      {dots.map((e, i) => (
        <div
          key={i}
          className={`rounded-full ${
            e.status === 'Succeeded' ? 'bg-green-400' :
            e.status === 'Failed' ? 'bg-red-400' :
            'bg-outline-variant/50'
          }`}
          style={{ width: 6, height: 6 }}
        />
      ))}
    </div>
  );
}

/* ---- Tooltip ---- */

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  const m = Math.floor(ms / 60_000);
  const s = Math.round((ms % 60_000) / 1000);
  return `${m}m${s}s`;
}

function Tooltip({
  show, label, summary, icon, color, fontSize = 12, maxWidth = 240,
  description, outputVar, retryConfig, health, stats, anchor,
}: {
  show: boolean; label: string; summary: string; icon: string; color: string;
  fontSize?: number; maxWidth?: number;
  description?: string;
  outputVar?: string;
  retryConfig?: Record<string, unknown>;
  health?: Array<{ status: string }>;
  stats?: { totalRuns: number; failureRate: number; avgDurationMs: number; p95DurationMs: number; lastDurationMs: number };
  anchor?: { left: number; top: number } | null;
}) {
  const { t } = useTranslation('designer');
  const hasContent = summary || description || outputVar || (health && health.length > 0) || (stats && stats.totalRuns > 0);
  if (!show || !hasContent || !anchor) return null;

  const retryAttempts = retryConfig?.maxAttempts as number | undefined;
  const retryBackoff = retryConfig?.backoff as string | undefined;
  const showRetry = retryAttempts && retryAttempts > 1;

  // Last 5 health dots oldest→newest
  const healthDots = health ? [...health].reverse().slice(0, 5) : [];

  const TooltipIcon = ACTIVITY_ICON_COMPONENTS[icon] ?? FALLBACK_ACTIVITY_ICON;
  const tooltip = (
    <div
      className="np-tooltip-portal fixed z-[9999] pointer-events-none -translate-x-1/2 -translate-y-full"
      style={{ left: anchor.left, top: anchor.top }}
    >
      <div
        className="bg-surface-lowest text-on-surface rounded-lg px-3.5 py-2.5 shadow-xl ring-1 ring-outline-variant/40"
        style={{ fontSize: `${fontSize}px`, maxWidth: `${maxWidth + 60}px` }}
      >
        {/* Header */}
        <div className="font-semibold mb-1.5 flex items-center gap-1.5 font-headline break-words min-w-0" style={{ color, fontSize: `${fontSize + 1}px` }}>
          <TooltipIcon size={fontSize + 2} className="shrink-0" />
          {label}
        </div>

        {/* Note / description */}
        {description && (
          <div className="text-on-surface-variant italic leading-snug font-label mb-1.5 whitespace-pre-wrap break-words"
               style={{ fontSize: `${fontSize - 1}px` }}>
            {description}
          </div>
        )}

        {/* Activity config summary — break-words so long unbreakable tokens (registry/file
            paths, URLs, scripts without spaces) wrap inside the fixed-width box instead of
            overflowing its right edge. */}
        {summary && (
          <div className="text-on-surface/85 leading-relaxed font-label whitespace-pre-wrap break-words">
            {summary}
          </div>
        )}

        {/* Meta row: output var + retry */}
        {(outputVar || showRetry) && (
          <div className="flex items-center gap-3 mt-1.5 pt-1.5 border-t border-outline-variant/40 flex-wrap"
               style={{ fontSize: `${fontSize - 1}px` }}>
            {outputVar && (
              <span className="text-on-surface-variant font-mono break-all min-w-0">
                {'→ {{'}{ outputVar }{'.output}}'}
              </span>
            )}
            {showRetry && (
              <span className="text-amber-700 font-label">
                ↺ {retryAttempts}× {retryBackoff ?? 'fixed'}
              </span>
            )}
          </div>
        )}

        {/* Performance annotations: avg/p95/last + failure rate */}
        {stats && stats.totalRuns > 0 && (
          <div className="flex items-center gap-3 mt-1.5 pt-1.5 border-t border-outline-variant/40 flex-wrap"
               style={{ fontSize: `${fontSize - 1}px` }}>
            <span className="text-on-surface-variant font-mono" title={t('nodes.tooltip.avg')}>
              avg {formatDuration(stats.avgDurationMs)}
            </span>
            <span className="text-on-surface-variant font-mono" title={t('nodes.tooltip.p95')}>
              p95 {formatDuration(stats.p95DurationMs)}
            </span>
            <span className="text-on-surface-variant font-mono" title={t('nodes.tooltip.last')}>
              last {formatDuration(stats.lastDurationMs)}
            </span>
            {stats.failureRate > 0 && (
              <span className="text-red-600 font-mono" title={t('nodes.tooltip.failTooltip', { rate: Math.round(stats.failureRate * 100), runs: stats.totalRuns })}>
                {t('nodes.tooltip.fail', { rate: Math.round(stats.failureRate * 100) })}
              </span>
            )}
          </div>
        )}

        {/* Recent runs sparkline */}
        {healthDots.length > 0 && (
          <div className="flex items-center gap-1 mt-1.5 pt-1.5 border-t border-outline-variant/40">
            <span className="text-on-surface-variant/70 mr-1 font-label" style={{ fontSize: `${fontSize - 2}px` }}>
              {t('nodes.tooltip.lastN', { count: healthDots.length })}
            </span>
            {healthDots.map((e, i) => (
              <div key={i} className={`rounded-full ${
                e.status === 'Succeeded' ? 'bg-green-500' :
                e.status === 'Failed'    ? 'bg-red-500' :
                'bg-outline-variant/50'
              }`} style={{ width: 7, height: 7 }} title={e.status} />
            ))}
          </div>
        )}

        <div className="absolute left-1/2 -translate-x-1/2 top-full w-0 h-0 border-l-[6px] border-l-transparent border-r-[6px] border-r-transparent border-t-[6px] border-t-surface-lowest" />
      </div>
    </div>
  );

  return createPortal(tooltip, document.body);
}


