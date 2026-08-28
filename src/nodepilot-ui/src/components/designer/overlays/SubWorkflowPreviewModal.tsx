import { Close, FolderTree, Launch, WarningAltFilled } from '@carbon/icons-react';
import { useEffect, useMemo } from 'react';
import { createPortal } from 'react-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  ReactFlow, ReactFlowProvider, Background, BackgroundVariant, Controls, MarkerType, ConnectionMode,
  type Node, type Edge,
} from '@xyflow/react';
import { resolveWorkflowRef } from '../../../lib/resolveWorkflowRef';
import { ActivityNode } from '../nodes/ActivityNode';
import { StickyNoteNode } from '../nodes/StickyNoteNode';
import { GroupNode } from '../nodes/GroupNode';
import { LabeledEdge } from '../edges/LabeledEdge';
import { withDefaultEdgePorts } from '../../../lib/edgePorts';

interface Props {
  workflowNameOrId: string;
  onClose: () => void;
  /** Optional — opens the child workflow in the main editor. Caller wires routing. */
  onOpenInEditor?: (workflowId: string) => void;
}

const nodeTypes = { activity: ActivityNode, stickyNote: StickyNoteNode, group: GroupNode };
const edgeTypes = { labeled: LabeledEdge };

/**
 * Read-only inline preview of a sub-workflow referenced by a `startWorkflow` node. Mounts
 * its own ReactFlow instance behind a `ReactFlowProvider` so it doesn't share state with the
 * parent editor. Pan and zoom work; drag, connect, and selection are disabled.
 *
 * Only renders one level deep — nested startWorkflow nodes show as normal nodes the user
 * can click through, rather than expanding automatically.
 */
export function SubWorkflowPreviewModal({ workflowNameOrId, onClose, onOpenInEditor }: Props) {
  const { t } = useTranslation(['editor', 'common']);
  const { data: workflow, isLoading, isError, error } = useQuery({
    queryKey: ['workflow-resolve', workflowNameOrId],
    queryFn: () => resolveWorkflowRef(workflowNameOrId),
    staleTime: 60_000,
    retry: false, // 404 is "not found" not "transient" — don't waste round-trips
  });

  // ESC closes — convenience for users who reach for it before scanning the modal chrome.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    globalThis.addEventListener('keydown', handler);
    return () => globalThis.removeEventListener('keydown', handler);
  }, [onClose]);

  // Read into a local first: accessing `workflow.definitionJson` directly inside the memo
  // body makes the compiler treat the whole `workflow` object as a dependency instead of
  // just this property, breaking auto-memoization (react-hooks/preserve-manual-memoization).
  const definitionJson = workflow?.definitionJson;
  const definition = useMemo(() => {
    if (!definitionJson) return null;
    try {
      const parsed = JSON.parse(definitionJson) as { nodes?: Node[]; edges?: Edge[] };
      return { nodes: parsed.nodes ?? [], edges: parsed.edges ?? [] };
    } catch {
      return null;
    }
  }, [definitionJson]);

  // Every node is set non-selectable, non-draggable, and connectable: true. The first two
  // keep the preview read-only. `connectable: true` is required despite that: React Flow
  // refuses to wire a saved edge to a node that isn't connectable, so leaving this false
  // (or a stale `false` persisted in the definition) breaks rendering of existing edges.
  const previewNodes = useMemo(() => {
    if (!definition) return [];
    return definition.nodes.map((n) => ({
      ...n,
      selectable: false,
      draggable: false,
      connectable: true,
    }));
  }, [definition]);

  // Edges saved without an explicit sourceHandle/targetHandle resolve to handle id "null",
  // which React Flow refuses to render. Apply the same `withDefaultEdgePorts` fix the main
  // editor uses so the preview shows the same connections the runtime would see.
  const previewEdges = useMemo(
    () => (definition?.edges ?? []).map((e) => withDefaultEdgePorts(e)),
    [definition],
  );
  const isResolvable = !workflowNameOrId.trim().startsWith('{{') && workflowNameOrId.trim().length > 0;

  // Rendered via a portal to document.body. Otherwise the modal would sit inside the parent
  // editor's <ReactFlowProvider>, and the inner ReactFlow's `useStore` calls would collide
  // with the outer store, so React Flow would refuse to wire the edges.
  return createPortal(
    <div
      className="np-anim-backdrop fixed inset-0 z-50 flex items-center justify-center bg-black/40"
      onClick={onClose}
      onKeyDown={(e) => e.key === 'Escape' && onClose()}
      role="dialog"
      aria-modal="true"
      data-testid="subworkflow-preview-modal"
      tabIndex={-1}
    >
      <div
        className="w-[1100px] h-[80vh] max-w-[95vw] bg-surface-lowest rounded-lg shadow-2xl border border-outline-variant/30 overflow-hidden flex flex-col"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.stopPropagation()}
        role="presentation"
      >
        {/* Header */}
        <div className="px-5 py-3 border-b border-outline-variant/20 flex items-center justify-between bg-surface-high">
          <div className="flex items-center gap-2 min-w-0">
            <FolderTree size={16} className="text-on-surface-variant shrink-0" />
            <h2 className="font-headline text-sm font-bold text-on-surface truncate">
              {t('editor:subWorkflow.title')}
              <span className="ml-2 font-mono font-normal text-xs text-on-surface-variant">
                {workflow?.name ?? workflowNameOrId}
              </span>
            </h2>
            <span className="px-2 py-0.5 rounded-sm bg-amber-100 text-amber-900 text-[10px] font-label font-bold uppercase tracking-widest">
              {t('editor:subWorkflow.readOnly')}
            </span>
          </div>
          <div className="flex items-center gap-2 shrink-0">
            {workflow && onOpenInEditor && (
              <button
                onClick={() => { onOpenInEditor(workflow.id); onClose(); }}
                className="flex items-center gap-1.5 rounded-md h-8 px-2.5 bg-surface-low hover:bg-surface text-on-surface font-label text-xs font-semibold transition-colors"
                title={t('editor:subWorkflow.openInEditor')}
              >
                <Launch size={12} />
                {t('editor:subWorkflow.open')}
              </button>
            )}
            <button
              onClick={onClose}
              className="text-on-surface-variant hover:text-on-surface"
              aria-label={t('common:close')}
            >
              <Close size={14} />
            </button>
          </div>
        </div>

        {/* Body — same `bg-surface` and `relative` wrapper that the main canvas section uses,
            so the background colour matches across the editor and the inline preview. */}
        <div className="flex-1 overflow-hidden relative bg-surface">
          {!isResolvable ? (
            <FallbackBlock
              icon={<WarningAltFilled size={32} className="text-amber-600" />}
              title={t('editor:subWorkflow.previewImpossible')}
              detail={t('editor:subWorkflow.previewVariableHint', { name: workflowNameOrId })}
            />
          ) : isLoading ? (
            <FallbackBlock
              icon={<div className="w-8 h-8 border-2 border-primary/30 border-t-primary rounded-full animate-spin" />}
              title={t('editor:subWorkflow.loading')}
              detail={workflowNameOrId}
            />
          ) : isError ? (
            <FallbackBlock
              icon={<WarningAltFilled size={32} className="text-error" />}
              title={t('editor:subWorkflow.loadFailed')}
              detail={(error as Error)?.message ?? t('editor:subWorkflow.unknownError')}
            />
          ) : !workflow ? (
            <FallbackBlock
              icon={<WarningAltFilled size={32} className="text-amber-600" />}
              title={t('editor:subWorkflow.notFound')}
              detail={t('editor:subWorkflow.notFoundDetail', { name: workflowNameOrId })}
            />
          ) : !definition ? (
            <FallbackBlock
              icon={<WarningAltFilled size={32} className="text-error" />}
              title={t('editor:subWorkflow.definitionUnparsable')}
              detail={t('editor:subWorkflow.definitionUnparsableDetail')}
            />
          ) : previewNodes.length === 0 ? (
            <FallbackBlock
              icon={<FolderTree size={32} className="text-outline" />}
              title={t('editor:subWorkflow.emptyWorkflow')}
              detail={t('editor:subWorkflow.emptyWorkflowDetail')}
            />
          ) : (
            <ReactFlowProvider>
              <ReactFlow
                nodes={previewNodes}
                edges={previewEdges}
                nodeTypes={nodeTypes}
                edgeTypes={edgeTypes}
                defaultEdgeOptions={{
                  type: 'labeled', animated: false,
                  markerEnd: { type: MarkerType.ArrowClosed, color: '#c3c6d7' },
                }}
                fitView
                fitViewOptions={{ padding: 0.2, maxZoom: 1.0 }}
                nodesDraggable={false}
                edgesReconnectable={false}
                elementsSelectable={false}
                connectionMode={ConnectionMode.Loose}
                // Block user-initiated connection drag without setting nodesConnectable={false}
                // (which would also strip pre-existing edges' handle resolution).
                isValidConnection={() => false}
                panOnDrag
                zoomOnScroll
                zoomOnPinch
                proOptions={{ hideAttribution: true }}
              >
                {/* Match the main editor's Background settings 1:1 — same dot gap, size,
                    and CSS variable for the colour so theme switching stays consistent. */}
                <Background
                  variant={BackgroundVariant.Dots}
                  gap={24}
                  size={1}
                  color="var(--color-outline-variant)"
                  className="opacity-40"
                />
                <Controls showInteractive={false} position="bottom-right" />
              </ReactFlow>
            </ReactFlowProvider>
          )}
        </div>

        {/* Footer hint */}
        <div className="px-5 py-2 border-t border-outline-variant/10 bg-surface-high text-[11px] font-label text-on-surface-variant flex items-center justify-between">
          <span>
            {t('editor:subWorkflow.footerHint')}
            {workflow && previewNodes.length > 0 && (
              <span className="ml-2 font-mono">
                {t('editor:subWorkflow.footerStats', { nodes: previewNodes.length, edges: previewEdges.length })}
              </span>
            )}
          </span>
          <span className="italic">{t('editor:subWorkflow.closeHint')}</span>
        </div>
      </div>
    </div>,
    document.body,
  );
}

function FallbackBlock({ icon, title, detail }: Readonly<{ icon: React.ReactNode; title: string; detail: string }>) {
  return (
    <div className="absolute inset-0 flex flex-col items-center justify-center px-6 text-center">
      <div className="mb-3">{icon}</div>
      <div className="font-headline text-base font-bold text-on-surface mb-1">{title}</div>
      <div className="font-label text-xs text-on-surface-variant max-w-md">{detail}</div>
    </div>
  );
}
