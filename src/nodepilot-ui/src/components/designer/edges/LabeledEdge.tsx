import { useContext, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  useStore,
  EdgeLabelRenderer,
  BaseEdge,
  type EdgeProps,
} from '@xyflow/react';
import { edgeArrowPath, getSmartEdgePath, EDGE_ARROW_STROKE_FRAC, type ControlPoints } from './smartEdgePath';
import { EdgeReshapeHandles } from './EdgeReshapeHandles';
import { EdgeEditingContext } from './edgeEditingContext';
import { NodeScaleOverrideContext } from '../nodeScaleContext';
import { Add, MisuseOutline } from '@carbon/icons-react';
import { useDesignStore, NODE_SCALES, EDGE_WIDTHS, LABEL_FONT_OFFSETS } from '../../../stores/designStore';
import { summarizeExpression } from '../../../lib/summarizeExpression';
import type { ExprNode } from '../ConditionBuilder';

/** Backward-compat re-export of the editing context under its old name. The old
 *  EdgeInsertContext only carried `onInsertRequest`; the new EdgeEditingContext is a
 *  superset with reshape actions added. Existing tests and imports keep working. */
export const EdgeInsertContext = EdgeEditingContext;

/* ---- Custom Edge ---- */

export function LabeledEdge({
  id, source, target, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition,
  data, style, markerEnd, selected,
}: EdgeProps) {
  const { t } = useTranslation('designer');
  // Honour a mobile/preview scale override so edge labels grow with the nodes (see ActivityNode).
  const scaleOverride = useContext(NodeScaleOverrideContext);
  const storeScaleIndex = useDesignStore((s) => s.nodeScaleIndex);
  const scaleIndex = scaleOverride ?? storeScaleIndex;
  const scale = NODE_SCALES[scaleIndex];
  const labelOffset = LABEL_FONT_OFFSETS[useDesignStore((s) => s.labelFontOffsetIndex)];
  const edgeLabelFont = Math.max(8, scale.edgeLabelFont + Math.round(labelOffset * 0.75));
  const edgeWidth = EDGE_WIDTHS[useDesignStore((s) => s.edgeWidthIndex)];
  const edgeRouting = useDesignStore((s) => s.edgeRouting);
  const edgesAnimated = useDesignStore((s) => s.edgesAnimated);
  const premiumCanvas = useDesignStore((s) => s.premiumCanvas);
  // Edge colours ride the semantic status tokens (skin-stable, dark-aware). `custom` uses
  // the dedicated orange token; `reachable`/`varFlow`/`dataBus` share the info token.
  const EDGE_COLORS = {
    success: 'var(--color-success)', failed: 'var(--color-error)', custom: 'var(--color-custom)',
    running: 'var(--color-running)', reachable: 'var(--color-info)', skipped: 'var(--color-skipped)',
    varFlow: 'var(--color-info)', collapsed: 'var(--color-skipped)', dataBus: 'var(--color-info)',
    arrowDefault: 'var(--color-outline-variant)',
  };

  // Read each endpoint's simulation state from the React Flow store. IMPORTANT: one
  // separate `useStore` call per endpoint, each returning a *primitive* value. Returning a
  // tuple/object instead would take fewer lines, but would create a new object on every
  // store update and defeat React Flow's identity comparison — the edge would then
  // re-render on EVERY hover / pan / selection change. That's not just a perf problem: it
  // also yanks away the `+` insert button between mousedown and mouseup, causing clicks
  // to get lost.
  const srcSim = useStore((s) =>
    (s.nodeLookup.get(source)?.data as Record<string, unknown> | undefined)?.__simulated as string | undefined,
  );
  const tgtSim = useStore((s) =>
    (s.nodeLookup.get(target)?.data as Record<string, unknown> | undefined)?.__simulated as string | undefined,
  );
  const srcLive = useStore((s) =>
    (s.nodeLookup.get(source)?.data as Record<string, unknown> | undefined)?.__liveStatus as string | undefined,
  );
  const tgtLive = useStore((s) =>
    (s.nodeLookup.get(target)?.data as Record<string, unknown> | undefined)?.__liveStatus as string | undefined,
  );
  const simulationActive = !!(srcSim || tgtSim);

  // Replay/Live-Execution: coloring for edges where both endpoints were executed.
  // Target status determines the color (the flow arrives at the target). A target
  // that is running RIGHT NOW gets its own state — amber stroke + the animated
  // flow-dash overlay ("the flow is moving into this step").
  const replayEdgeState: 'success' | 'failed' | 'skipped' | 'running' | 'none' =
    srcLive && tgtLive
      ? (tgtLive === 'Failed' ? 'failed'
        : tgtLive === 'Skipped' ? 'skipped'
        : tgtLive === 'Running' ? 'running'
        : 'success')
      : 'none';
  // 'reachable' styling only applies between two nodes that are already revealed;
  // 'revealing' means the flow is currently traveling across this edge (source ran,
  // target just appeared). Otherwise neutral.
  const srcReached = srcSim === 'reachable' || srcSim === 'revealing';
  const tgtReached = tgtSim === 'reachable' || tgtSim === 'revealing';
  const edgeSimState: 'flowing' | 'reachable' | 'skipped' | 'none' =
    !simulationActive ? 'none'
    : (srcSim === 'reachable' && tgtSim === 'revealing') ? 'flowing'
    : (srcReached && tgtReached) ? 'reachable'
    : (srcSim === 'skipped' || tgtSim === 'skipped') ? 'skipped'
    : 'none';

  const edgeData = (data as Record<string, unknown>) || {};
  const isVarFlowHighlighted = !!(edgeData.__varFlowHighlighted);
  const isCollapsedSummary = !!edgeData.__collapsedSummary;
  const label = (edgeData.label as string) || '';
  const condition = (edgeData.condition as string) || '';
  const conditionExpression = edgeData.conditionExpression as ExprNode | undefined;
  const hasExpression = !!conditionExpression;
  const isDisabled = (edgeData.disabled as boolean) || false;
  const controlPoints = edgeData.controlPoints as ControlPoints | undefined;
  // Data-flow overlay: when the toggle is on, every edge carries the list of variable heads
  // that actually ride across it. Non-empty list → render a small chip; empty list →
  // de-emphasise the edge (it's pure control-flow with no data crossing).
  const flowingVars = edgeData.__flowingVars as string[] | undefined;
  const dataFlowActive = Array.isArray(flowingVars);
  const hasFlowingVars = dataFlowActive && (flowingVars?.length ?? 0) > 0;

  // Human-readable summary of the expression, with step labels resolved via nodeLookup.
  // The selector returns a plain string → React compares it with Object.is and only
  // re-renders when the summary text actually changes.
  const expressionSummary = useStore((s) => {
    if (!conditionExpression) return '';
    return summarizeExpression(conditionExpression, (stepId) => {
      const n = s.nodeLookup.get(stepId);
      const lbl = (n?.data as Record<string, unknown> | undefined)?.label as string | undefined;
      return lbl || stepId;
    });
  });

  // Arrowhead length scales PROPORTIONALLY with the edge stroke width (its base width is
  // derived from the length in arrowFromBaseAndTip), so a thicker edge gets a proportionally
  // bolder arrowhead instead of the old flat 16px ceiling. Floor keeps thin edges' arrows
  // visible; a soft ceiling avoids absurd heads at the very widest setting.
  const premiumArrowLength = premiumCanvas ? Math.max(12, Math.min(48, edgeWidth * 6)) : undefined;
  const segments = getSmartEdgePath({
    sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, routing: edgeRouting,
    controlPoints,
    markerEndLength: premiumArrowLength,
  });
  const [, labelX, labelY] = segments[0];

  // Condition-based edge coloring — semantic status tokens (skin-stable, dark-aware):
  //   .success → success-green, .failed → error-red, any other expression/condition →
  //   custom-orange (matches the condition pill — stroke and chip agree),
  //   empty (always) → neutral outline.
  const conditionKind: 'success' | 'failed' | 'custom' | 'always' =
    condition.endsWith('.success') ? 'success' :
    condition.endsWith('.failed')  ? 'failed'  :
    (hasExpression || condition)   ? 'custom'  : 'always';

  const conditionStroke =
    conditionKind === 'success' ? EDGE_COLORS.success :
    conditionKind === 'failed'  ? EDGE_COLORS.failed :
    conditionKind === 'custom'  ? EDGE_COLORS.custom :
    'var(--color-outline-variant)';
  const conditionArrowFill = conditionKind === 'always' ? EDGE_COLORS.arrowDefault : conditionStroke;

  // Premium: glow CSS class applied to the <g> wrapper for dark-mode drop-shadow filter.
  // Only active when premiumCanvas is on and no override state is active (live execution,
  // simulation, data-flow overlay, etc.) so we don't double-colour semantic edges.
  const isOverrideState =
    edgeSimState !== 'none' || replayEdgeState !== 'none'
    || isVarFlowHighlighted || isCollapsedSummary || dataFlowActive || isDisabled;
  const conditionGlowClass = premiumCanvas
    ? (!isOverrideState
        ? (conditionKind === 'success' ? 'np-edge-g--success'
          : conditionKind === 'failed'  ? 'np-edge-g--failed'
          : conditionKind === 'custom'  ? 'np-edge-g--custom'
          : '')
        : (replayEdgeState === 'success' ? 'np-edge-g--success'
          : replayEdgeState === 'failed'  ? 'np-edge-g--failed'
          : ''))
    : '';

  // Flow-animation overlay path — idle edges stay SOLID and calm; the animated dash
  // only rides edges whose flow is actually moving right now (live execution entering
  // a currently-running target). The `edgesAnimated` toggle remains the master switch.
  const showFlowAnimation = premiumCanvas
    && edgesAnimated
    && !isDisabled
    && replayEdgeState === 'running'
    && edgeSimState === 'none'
    && !isVarFlowHighlighted
    && !isCollapsedSummary
    && !dataFlowActive;

  // Premium normally renders arrowheads as explicit polygons aligned to the trimmed path.
  // The URL marker remains a fallback for too-short edges where trimming would eat the path.
  // Non-premium keeps React Flow's default markerEnd from defaultEdgeOptions.
  const markerUrl = premiumCanvas
    ? (conditionKind === 'success' ? 'url(#np-arrow-success)'
      : conditionKind === 'failed'  ? 'url(#np-arrow-failed)'
      : conditionKind === 'custom'  ? 'url(#np-arrow-custom)'
      : 'url(#np-arrow-default)')
    : markerEnd;

  // Simulation styling:
  //   'flowing'   — the currently active transition edge (source ran, target is running
  //                 right now): amber, thicker, animated dash ("flow is moving")
  //   'reachable' — between two nodes that already finished: calm indigo
  //   'skipped'   — at least one endpoint was skipped: grey-dashed, nearly invisible
  const edgeStyle = {
    ...(edgeSimState === 'flowing'
      ? { stroke: EDGE_COLORS.running, strokeWidth: edgeWidth + 2, strokeDasharray: '6,3' }
      : edgeSimState === 'reachable'
      ? { stroke: EDGE_COLORS.reachable, strokeWidth: edgeWidth + 1, opacity: 0.95 }
      : edgeSimState === 'skipped'
      ? { stroke: EDGE_COLORS.skipped, strokeWidth: edgeWidth, strokeDasharray: '4,4', opacity: 0.25 }
      : replayEdgeState !== 'none'
      ? (replayEdgeState === 'success'
          ? { stroke: EDGE_COLORS.success, strokeWidth: edgeWidth + 1, opacity: 1 }
          : replayEdgeState === 'failed'
          ? { stroke: EDGE_COLORS.failed, strokeWidth: edgeWidth + 1, opacity: 1 }
          : replayEdgeState === 'running'
          // Der Lauf in den Running-Target wird amber gefärbt.
          ? { stroke: EDGE_COLORS.running, strokeWidth: edgeWidth + 1, opacity: 1 }
          : { stroke: EDGE_COLORS.skipped, strokeWidth: edgeWidth, strokeDasharray: '4,4', opacity: 0.4 })
      : isVarFlowHighlighted
      ? { stroke: EDGE_COLORS.varFlow, strokeWidth: edgeWidth + 2, strokeDasharray: '8,3', opacity: 1 }
      : isCollapsedSummary
      ? { stroke: EDGE_COLORS.collapsed, strokeWidth: edgeWidth, strokeDasharray: '6,4', opacity: 0.7 }
      : dataFlowActive && hasFlowingVars
      // Data-flow overlay carries variables: info tint to mark the bus.
      ? { stroke: EDGE_COLORS.dataBus, strokeWidth: edgeWidth + 1, opacity: 0.95 }
      : dataFlowActive && !hasFlowingVars
      // Pure control-flow under the overlay: dim it so the user can quickly spot the
      // edges that don't carry any data — those are the ones suitable for being purely
      // sequencing decisions, often candidates for simplification.
      ? { stroke: 'var(--color-outline-variant)', strokeWidth: edgeWidth, opacity: 0.35 }
      : isDisabled
      ? { stroke: 'var(--color-outline-variant)', strokeWidth: edgeWidth, strokeDasharray: '5,8', opacity: 0.45 }
      : { stroke: selected ? 'var(--color-primary)' : conditionStroke, strokeWidth: selected ? edgeWidth + 1 : edgeWidth }),
    ...(style || {}),
  };

  const rawDisplayLabel = label
    || (hasExpression ? (expressionSummary || 'ƒ(x)') : '')
    || (condition ? (condition.endsWith('.success') ? t('edges.onSuccess') : condition.endsWith('.failed') ? t('edges.onFailure') : condition) : '');
  // Edge labels are rendered directly on the graph canvas → hard-truncate overly long
  // expressions so the layout doesn't fall apart. The full text is still visible in the
  // edge properties panel.
  const displayLabel = rawDisplayLabel.length > 60 ? rawDisplayLabel.slice(0, 60) + '…' : rawDisplayLabel;

  const { onInsertRequest, canWrite } = useContext(EdgeEditingContext);

  // Hover state for the inline-insert „+" button. Tracked separately for edge body and
  // button itself: when the cursor moves from the SVG edge into the button div (which
  // EdgeLabelRenderer mounts outside the edge `<g>`), the edge's mouseleave fires before
  // the button's mouseenter. Combining both states keeps the button visible across that
  // gap so a user actually reaching for it doesn't watch it disappear at the last moment.
  const [edgeHovered, setEdgeHovered] = useState(false);
  const [buttonHovered, setButtonHovered] = useState(false);
  const showInsertButton = !isCollapsedSummary && (edgeHovered || buttonHovered || !!selected);
  // Reshape handles: only on single-segment edges (cubic Bezier can't represent the
  // backward-edge U-loop), only for users who can write, only when the edge is actually
  // selected. Hover alone is too noisy on dense graphs and competes with the +-button.
  const showReshapeHandles = !isCollapsedSummary && !!selected && segments.length === 1 && canWrite;

  return (
    <>
      {segments.map(([segPath, , , arrow], i) => {
        const manualArrow = premiumCanvas && i === segments.length - 1 ? arrow : undefined;
        return (
        // Wrap each BaseEdge in a <g> so the zero-opacity `interactionWidth` hit path
        // bubbles its pointer events up to our hover handler. BaseEdge itself returns a
        // fragment, so without the wrapping group there's no element to listen on.
        // np-edge-g + conditionGlowClass drive the dark-mode drop-shadow glow in CSS.
        <g
          key={i}
          className={`np-edge-g ${conditionGlowClass}`}
          onMouseEnter={() => setEdgeHovered(true)}
          onMouseLeave={() => setEdgeHovered(false)}
        >
          <BaseEdge
            id={i === 0 ? id : `${id}-seg${i}`}
            path={segPath}
            style={edgeStyle}
            markerEnd={i === segments.length - 1 && !manualArrow ? markerUrl : undefined}
            interactionWidth={20}
          />
          {showFlowAnimation && (
            <path d={segPath} className="np-edge-flow" />
          )}
          {manualArrow && (
            // Same-color stroke with round joins rounds the dart's tip + barb corners into
            // the soft reference look; edgeArrowPath pre-insets the core geometry by half
            // this stroke so the rounded head still lands exactly on the port.
            <path
              data-testid={`premium-edge-arrow-${id}`}
              d={edgeArrowPath(manualArrow)}
              fill={conditionArrowFill}
              stroke={conditionArrowFill}
              strokeWidth={(premiumArrowLength ?? 12) * EDGE_ARROW_STROKE_FRAC}
              strokeLinejoin="round"
              strokeLinecap="round"
              style={{ pointerEvents: 'none' }}
            />
          )}
        </g>
        );
      })}
      <EdgeReshapeHandles
        edgeId={id}
        sourceX={sourceX} sourceY={sourceY} sourcePosition={sourcePosition}
        targetX={targetX} targetY={targetY} targetPosition={targetPosition}
        controlPoints={controlPoints}
        visible={showReshapeHandles}
      />
      <EdgeLabelRenderer>
        {displayLabel && (
          <div
            className={`np-edge-label-pill absolute pointer-events-all px-2 py-0.5 rounded font-label font-semibold border shadow-sm cursor-pointer
              ${isDisabled ? 'bg-surface-high text-outline border-outline-variant/30' :
                conditionKind === 'custom' ? 'bg-custom-container/80 text-on-custom-container border-custom/40' :
                conditionKind === 'success' ? 'bg-success-container/80 text-on-success-container border-success/40' :
                conditionKind === 'failed' ? 'bg-error-container/80 text-on-error-container border-error/40' :
                'bg-surface-lowest text-on-surface-variant border-outline-variant/30'}
              ${selected ? 'ring-2 ring-primary/40' : ''}`}
            style={{
              transform: `translate(-50%, -50%) translate(${labelX}px,${labelY}px)`,
              fontSize: `${edgeLabelFont}px`,
            }}
          >
            {displayLabel}
          </div>
        )}
        {/* Disabled-edge indicator: a small ⊘ badge floating on the line so the user
            immediately sees the edge is inactive, even without a label. */}
        {isDisabled && (
          <div
            className="absolute pointer-events-none select-none"
            style={{
              transform: `translate(-50%, -50%) translate(${labelX}px,${labelY + (displayLabel ? -20 : 0)}px)`,
            }}
          >
            <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded-full bg-surface-high border border-outline-variant/50 text-outline font-label font-semibold leading-none whitespace-nowrap" style={{ fontSize: 10 }}>
              <MisuseOutline size={9} aria-hidden />
              <span>{t('edges.disabled')}</span>
            </span>
          </div>
        )}
        {/* Data-flow overlay chip: shows the heads (step output names) that ride this
            edge. Mounts above the edge label so it doesn't fight for the same y-slot.
            >2 vars: collapsed to "first, second +N" with full list in title-tooltip. */}
        {dataFlowActive && hasFlowingVars && (
          <div
            className="absolute pointer-events-all px-1.5 py-0.5 rounded-sm font-mono shadow-sm border bg-info-container/80 text-on-info-container border-info/40"
            data-testid={`edge-flowingvars-${id}`}
            title={(flowingVars ?? []).map((v) => `{{${v}.…}}`).join('\n')}
            style={{
              transform: `translate(-50%, -50%) translate(${labelX}px,${labelY - (displayLabel ? 18 : 0)}px)`,
              fontSize: `${Math.max(edgeLabelFont - 1, 9)}px`,
              whiteSpace: 'nowrap',
            }}
          >
            {(flowingVars ?? []).length <= 2
              ? (flowingVars ?? []).join(' · ')
              : `${(flowingVars ?? []).slice(0, 2).join(' · ')} +${(flowingVars ?? []).length - 2}`}
          </div>
        )}
        {/* Inline insert button: small and unobtrusive at the edge's midpoint, offset a bit
            from the label so it doesn't collide with it. Hidden by default; appears as soon
            as the cursor reaches the edge (or the button itself). Clicking it opens the
            activity picker at the label's midpoint.
            pointer-events set inline + onMouseDown stopPropagation: otherwise React Flow's
            pane handler consumes the mousedown before our onClick can fire — that was the
            reason clicks appeared to "just do nothing". */}
        <div
          className={`nodrag nopan absolute transition-opacity duration-150 ${
            showInsertButton ? 'opacity-100' : 'opacity-0'
          }`}
          onMouseEnter={() => setButtonHovered(true)}
          onMouseLeave={() => setButtonHovered(false)}
          style={{
            transform: `translate(-50%, -50%) translate(${labelX}px,${labelY + (displayLabel ? 18 : 0)}px)`,
            pointerEvents: showInsertButton ? 'all' : 'none',
          }}
        >
          <button
            type="button"
            onMouseDown={(e) => { e.stopPropagation(); }}
            onClick={(e) => {
              e.stopPropagation();
              e.preventDefault();
              onInsertRequest(id, labelX, labelY);
            }}
            className="flex items-center justify-center w-6 h-6 rounded-full bg-primary text-on-primary shadow-md hover:scale-110 transition-transform cursor-pointer"
            title={t('edges.insertStep')}
          >
            <Add size={14} />
          </button>
        </div>
      </EdgeLabelRenderer>
    </>
  );
}
