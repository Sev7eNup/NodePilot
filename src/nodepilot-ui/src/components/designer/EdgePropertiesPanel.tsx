import { ArrowDown, ArrowLeft, ArrowRight, ArrowUp, Close, TrashCan, View, ViewOff } from '@carbon/icons-react';
import { useState } from 'react';
import { useTranslation, Trans } from 'react-i18next';
import type { Edge, Node } from '@xyflow/react';
import { getUpstreamVariables, describeNodeOutputs } from '../../lib/upstreamVariables';
import { summarizeExpression } from '../../lib/summarizeExpression';
import { ConditionBuilder, type ExprNode } from './ConditionBuilder';
import { useDesignStore } from '../../stores/designStore';
import { EDGE_PORT_LABELS, EDGE_PORT_SIDES, edgeSourcePort, edgeTargetPort, type EdgePortSide } from '../../lib/edgePorts';
import { confirmDialog } from '../../stores/confirmStore';

interface Props {
  edge: Edge;
  allNodes: Node[];
  allEdges: Edge[];
  onUpdate: (edgeId: string, data: Partial<Edge>) => void;
  onDelete: (edgeId: string) => void;
  onClose: () => void;
  width?: number;
  /** When false, all edit controls are disabled (wrapped in a fieldset). Default true. */
  canWrite?: boolean;
}

export function EdgePropertiesPanel({ edge, allNodes, allEdges, onUpdate, onDelete, onClose, width, canWrite = true }: Readonly<Props>) {
  const { t } = useTranslation('designer');
  const expertMode = useDesignStore((s) => s.designerMode === 'expert');
  const flexiblePortsEnabled = useDesignStore((s) => s.flexiblePortsEnabled);
  const edgeData = (edge.data as Record<string, unknown>) || {};
  const label = (edgeData.label as string) || '';
  const condition = (edgeData.condition as string) || '';
  const conditionExpression = (edgeData.conditionExpression as ExprNode | undefined) || null;
  const hasExpression = !!conditionExpression;
  const isDisabled = (edgeData.disabled as boolean) || false;

  const sourceNode = allNodes.find((n) => n.id === edge.source);
  const targetNode = allNodes.find((n) => n.id === edge.target);
  const sourceLabel = (sourceNode?.data as Record<string, unknown>)?.label as string || edge.source;
  const targetLabel = (targetNode?.data as Record<string, unknown>)?.label as string || edge.target;
  const sourcePort = edgeSourcePort(edge);
  const targetPort = edgeTargetPort(edge);
  const hasCustomPorts = sourcePort !== 'right' || targetPort !== 'left';
  const showPortControls = flexiblePortsEnabled || hasCustomPorts;

  // Upstream variables visible on this edge = source node's own outputs + everything reaching it
  const upstreamVars = [
    ...(sourceNode ? describeNodeOutputs(sourceNode) : []),
    ...getUpstreamVariables(edge.source, allNodes, allEdges),
  ];

  const [mode, setMode] = useState<'simple' | 'expression'>(conditionExpression ? 'expression' : 'simple');

  // Auto-label preview: identical to the fallback logic in LabeledEdge so the user
  // can see what the label WOULD be if it weren't overridden. Useful when a custom
  // label was set at authoring/import time and the user now edits the underlying
  // condition — the custom label stays, which otherwise feels like "nothing changed".
  const resolveStepLabel = (stepId: string) =>
    ((allNodes.find((n) => n.id === stepId)?.data as Record<string, unknown> | undefined)?.label as string) || stepId;
  const autoLabel = conditionExpression
    ? (summarizeExpression(conditionExpression, resolveStepLabel) || 'ƒ(x)')
    : condition.endsWith('.success') ? 'On Success'
    : condition.endsWith('.failed')  ? 'On Failure'
    : condition || 'Always';
  const labelOverridesAuto = !!label && label !== autoLabel;

  const updateEdgeData = (patch: Record<string, unknown>) => {
    const newData = { ...edgeData, ...patch };
    onUpdate(edge.id, {
      data: newData,
      label: patch.label !== undefined ? (patch.label as string) || undefined : edge.label,
      animated: !(newData.disabled as boolean),
      // Leave style empty — LabeledEdge.tsx picks the stroke colour based on
      // data.disabled and condition/conditionExpression (success=green, failed=red,
      // custom=orange, otherwise outline-variant). A style hardcoded here would
      // override conditionStroke via object spread and always render the edge grey.
      style: {},
    });
  };

  // In LabeledEdge, the label wins over the condition-based fallback. A previously set
  // "Always"/"On Success"/"On Failure" would otherwise stay stuck when the user changes the
  // condition. So: if the current label is one of the canonical values (or empty), we sync it
  // to the matching value. Custom manual labels are left alone.
  //
  // Returning '' means: clear the label, so LabeledEdge's fallback renders the summary or the
  // condition text instead.
  const CANONICAL_LABELS = new Set(['', 'Always', 'On Success', 'On Failure']);
  const deriveLabel = (cond: string, hasExpr: boolean): string => {
    if (hasExpr) return '';
    if (!cond) return 'Always';
    if (cond.endsWith('.success')) return 'On Success';
    if (cond.endsWith('.failed')) return 'On Failure';
    return '';
  };
  const setCondition = (newCondition: string) => {
    const patch: Record<string, unknown> = { condition: newCondition };
    if (CANONICAL_LABELS.has(label)) patch.label = deriveLabel(newCondition, hasExpression);
    updateEdgeData(patch);
  };
  const setConditionExpression = (next: ExprNode | null) => {
    const patch: Record<string, unknown> = { conditionExpression: next ?? undefined };
    if (CANONICAL_LABELS.has(label)) patch.label = deriveLabel(condition, !!next);
    updateEdgeData(patch);
  };

  const switchToSimple = () => {
    setMode('simple');
    if (conditionExpression) setConditionExpression(null);
  };
  const switchToExpression = () => {
    setMode('expression');
    if (condition) setCondition('');
  };

  return (
    <div className="np-anim-panel bg-surface-low flex flex-col h-full overflow-hidden shrink-0 z-10 shadow-[-20px_0_40px_rgba(0,0,0,0.02)]" style={{ width: width ?? 320 }}>
      <div className="flex items-center justify-between px-6 py-4 border-b border-surface-variant/50">
        <h2 className="font-headline text-base font-bold text-on-surface">{t('edgePanel.connection')}</h2>
        <button onClick={onClose} className="text-on-surface-variant hover:text-on-surface transition-colors">
          <Close size={18} />
        </button>
      </div>
      <div className="flex-1 overflow-y-auto p-6 space-y-5">
        <fieldset disabled={!canWrite} className="contents">
        <div className="bg-surface-highest rounded-md p-3">
          <div className="flex items-center gap-2 text-sm font-label">
            <span className="font-semibold text-on-surface">{sourceLabel}</span>
            <span className="text-outline">&rarr;</span>
            <span className="font-semibold text-on-surface">{targetLabel}</span>
          </div>
        </div>

        {expertMode && showPortControls && (
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-2">{t('edgePanel.ports')}</label>
            <div className="grid grid-cols-[72px_1fr] gap-x-3 gap-y-2 items-center">
              <span className="text-xs font-label text-on-surface-variant">{t('edgePanel.from')}</span>
              <PortSelector
                value={sourcePort}
                onChange={(side) => onUpdate(edge.id, { sourceHandle: side })}
              />
              <span className="text-xs font-label text-on-surface-variant">{t('edgePanel.to')}</span>
              <PortSelector
                value={targetPort}
                onChange={(side) => onUpdate(edge.id, { targetHandle: side })}
              />
            </div>
            {!flexiblePortsEnabled && (
              <p className="text-xs text-outline mt-2">
                {t('edgePanel.flexiblePortsOff')}
              </p>
            )}
          </div>
        )}

        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('edgePanel.label')}</label>
          <input
            type="text"
            value={label}
            onChange={(e) => updateEdgeData({ label: e.target.value })}
            className="input-field"
            placeholder={t('edgePanel.labelPlaceholder')}
          />
          <p className="text-xs text-outline mt-1">{t('edgePanel.displayedOnArrow')}</p>
          {labelOverridesAuto && (
            <div className="mt-2 flex items-start gap-2 p-2 bg-warning-container/60 border border-warning/30 rounded-md text-xs">
              <div className="flex-1 min-w-0">
                <div className="text-on-warning-container font-medium">
                  {t('edgePanel.labelOverrides')}
                </div>
                <div className="text-on-warning-container/80 mt-0.5 truncate" title={autoLabel}>
                  {t('edgePanel.auto')}<code className="font-mono">{autoLabel}</code>
                </div>
              </div>
              <button
                onClick={() => updateEdgeData({ label: '' })}
                className="shrink-0 px-2 py-1 bg-warning text-on-warning text-[10px] rounded hover:brightness-105 transition-colors"
                title={t('edgePanel.clearCustomLabel')}
              >
                {t('edgePanel.useAuto')}
              </button>
            </div>
          )}
        </div>

        <div>
          <div className="flex items-center justify-between mb-1">
            <label className="text-xs font-medium text-on-surface-variant">{t('edgePanel.conditionOptional')}</label>
            {expertMode && <div className="flex gap-1">
              <button
                onClick={switchToSimple}
                className={`px-2 py-0.5 text-[10px] rounded ${mode === 'simple' ? 'bg-inverse-surface text-white' : 'bg-surface-container text-on-surface-variant'}`}
              >{t('edgePanel.simple')}</button>
              <button
                onClick={switchToExpression}
                className={`px-2 py-0.5 text-[10px] rounded ${mode === 'expression' ? 'bg-inverse-surface text-white' : 'bg-surface-container text-on-surface-variant'}`}
              >{t('edgePanel.expression')}</button>
            </div>}
          </div>

          {mode === 'simple' ? (
            <>
              <input
                type="text"
                value={condition}
                onChange={(e) => setCondition(e.target.value)}
                className="input-field font-mono text-sm"
                placeholder={t('edgePanel.conditionPlaceholder')}
              />
              <p className="text-xs text-outline mt-1">
                <Trans
                  t={t}
                  i18nKey="edgePanel.supported"
                  components={[
                    <code className="bg-surface-container px-1 rounded" />,
                    <code className="bg-surface-container px-1 rounded" />,
                  ]}
                />
              </p>
              {sourceNode && (
                <div className="flex gap-1 mt-2">
                  <button
                    onClick={() => setCondition(`${edge.source}.success`)}
                    className="px-2 py-1 text-xs bg-success-container/70 text-on-success-container border border-success/30 rounded-md hover:brightness-95"
                  >{t('edgePanel.onSuccess')}</button>
                  <button
                    onClick={() => setCondition(`${edge.source}.failed`)}
                    className="px-2 py-1 text-xs bg-error-container/70 text-on-error-container border border-error/30 rounded-md hover:brightness-95"
                  >{t('edgePanel.onFailure')}</button>
                  <button
                    onClick={() => setCondition('')}
                    className="px-2 py-1 text-xs bg-surface-low text-on-surface-variant border border-outline-variant/50 rounded hover:bg-surface-container"
                  >{t('edgePanel.always')}</button>
                </div>
              )}
            </>
          ) : (
            <ConditionBuilder
              value={conditionExpression}
              upstreamVars={upstreamVars}
              onChange={(next) => setConditionExpression(next)}
            />
          )}
        </div>

        <hr className="border-surface-variant/50" />

        <div>
          <button
            onClick={() => updateEdgeData({ disabled: !isDisabled })}
            className={`flex items-center gap-2 w-full px-3 py-2.5 rounded-md text-sm font-label font-medium border transition-colors ${
              isDisabled
                ? 'bg-tertiary-fixed text-tertiary border-tertiary-fixed'
                : 'bg-surface-highest text-on-surface border-surface-variant/30 hover:bg-surface-high'
            }`}
          >
            {isDisabled ? <ViewOff size={16} /> : <View size={16} />}
            {isDisabled ? t('edgePanel.connectionDisabled') : t('edgePanel.connectionActive')}
          </button>
          <p className="text-xs text-outline mt-1">
            {t('edgePanel.disabledSkipped')}
          </p>
        </div>

        <div>
          <button
            onClick={async () => {
              if (await confirmDialog({ message: t('edgePanel.confirmDelete'), danger: true }))
                onDelete(edge.id);
            }}
            className="flex items-center gap-2 w-full py-2.5 rounded-md text-sm font-label font-medium text-error hover:bg-error-container hover:text-on-error-container transition-colors justify-center"
          >
            <TrashCan size={16} />
            {t('edgePanel.deleteConnection')}
          </button>
        </div>
        </fieldset>
      </div>
    </div>
  );
}

const PORT_ICONS: Record<EdgePortSide, typeof ArrowUp> = {
  top: ArrowUp,
  right: ArrowRight,
  bottom: ArrowDown,
  left: ArrowLeft,
};

function PortSelector({ value, onChange }: Readonly<{ value: EdgePortSide; onChange: (side: EdgePortSide) => void }>) {
  return (
    <div className="grid grid-cols-4 gap-1">
      {EDGE_PORT_SIDES.map((side) => {
        const Icon = PORT_ICONS[side];
        const selected = side === value;
        return (
          <button
            key={side}
            type="button"
            onClick={() => onChange(side)}
            className={`flex items-center justify-center h-8 rounded-md border transition-colors ${
              selected
                ? 'bg-primary text-on-primary border-primary'
                : 'bg-surface-highest text-on-surface-variant border-outline-variant/30 hover:bg-surface-high'
            }`}
            title={EDGE_PORT_LABELS[side]}
            aria-label={EDGE_PORT_LABELS[side]}
          >
            <Icon size={14} />
          </button>
        );
      })}
    </div>
  );
}
