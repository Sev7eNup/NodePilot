import { ArrowLeft, Screen } from '@carbon/icons-react';
import { useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  ReactFlow, ReactFlowProvider,
  Background, BackgroundVariant, Controls, ConnectionMode, MarkerType,
  useNodesState, useEdgesState,
  type Node, type Edge, type NodeTypes, type EdgeTypes,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { api } from '../api/client';
import type { Workflow } from '../types/api';
import { ActivityNode } from '../components/designer/nodes/ActivityNode';
import { StickyNoteNode } from '../components/designer/nodes/StickyNoteNode';
import { GroupNode } from '../components/designer/nodes/GroupNode';
import { LabeledEdge } from '../components/designer/edges/LabeledEdge';
import { withDefaultEdgePorts } from '../lib/edgePorts';
import { NodeScaleOverrideContext } from '../components/designer/nodeScaleContext';
import { useWorkflowSignalR } from '../hooks/useSignalR';

// Render the read-only phone graph at the `lg` node scale (NODE_SCALES index 3) — much bigger
// icons/labels than the desktop `xs` default, so a workflow stays legible on a small screen.
const MOBILE_SCALE_INDEX = 3;

// Reuse the editor's node/edge renderers verbatim. ActivityNode reads everything it needs
// from the global designStore (defaults) + node.data, including data.__liveStatus, so it
// renders correctly in this read-only context with no editor callbacks.
const nodeTypes: NodeTypes = { activity: ActivityNode, stickyNote: StickyNoteNode, group: GroupNode };
const edgeTypes: EdgeTypes = { labeled: LabeledEdge };

/**
 * Read-only workflow graph for phones / portrait tablets. The full designer is
 * desktop-only (mouse drag-to-connect, dense side panels), so on small screens we render
 * a pannable/pinch-zoomable canvas that mirrors the workflow and reflects live execution
 * status — enough to monitor a run from a phone. Editing stays on the desktop.
 */
function MobileWorkflowViewInner({ workflowId }: Readonly<{ workflowId: string }>) {
  const navigate = useNavigate();
  const { t } = useTranslation(['workflows', 'common']);
  const [nodes, setNodes] = useNodesState<Node>([]);
  const [edges, setEdges] = useEdgesState<Edge>([]);

  const { data: workflow, isLoading, isError } = useQuery({
    queryKey: ['workflow', workflowId],
    queryFn: () => api.get<Workflow>(`/workflows/${workflowId}`),
    enabled: !!workflowId,
  });

  // Live step status for any active execution of this workflow (same hook the editor uses).
  const { liveExecution } = useWorkflowSignalR(workflowId);

  // Parse the stored definition into React Flow nodes/edges once per loaded workflow.
  useEffect(() => {
    if (!workflow) return;
    try {
      const def = JSON.parse(workflow.definitionJson) as { nodes?: Node[]; edges?: Edge[] };
      const rawNodes = def.nodes ?? [];
      // Render the authored layout verbatim: the mobile view must mirror the desktop
      // arrangement exactly — only the node *scale* is enlarged (NodeScaleOverrideContext
      // below), never the positions.
      // Groups must precede their children in the array (React Flow renders in order).
      // `connectable: true` is load-bearing: ActivityNode derives handle connectability from
      // it, and React Flow needs it to wire pre-existing edges to their target handles.
      setNodes([...rawNodes]
        .sort((a) => (a.type === 'group' ? -1 : 1))
        .map((n) => ({ ...n, draggable: false, selectable: false, connectable: true })));
      // Backfill default source/target ports (right→left) exactly like the editor and the
      // sub-workflow preview — without it React Flow drops edges whose handles are null and
      // no line renders.
      setEdges((def.edges ?? []).map((e) => withDefaultEdgePorts(e)));
    } catch {
      setNodes([]);
      setEdges([]);
    }
  }, [workflow, setNodes, setEdges]);

  // Mirror live step status onto node.data.__liveStatus — node id === step id (see editor).
  useEffect(() => {
    if (!liveExecution || liveExecution.steps.length === 0) return;
    const statusByStepId = new Map(liveExecution.steps.map((s) => [s.stepId, s.status]));
    setNodes((nds) => nds.map((n) => {
      const next = statusByStepId.get(n.id);
      const current = (n.data as Record<string, unknown>).__liveStatus as string | undefined;
      if (next === current) return n;
      return { ...n, data: { ...n.data, __liveStatus: next } };
    }));
  }, [liveExecution, setNodes]);

  return (
    <div className="np-shell flex flex-col h-screen bg-surface">
      <header className="shrink-0 h-12 px-3 flex items-center gap-2 border-b border-outline-variant/40 bg-surface-low/60 backdrop-blur-sm">
        <button
          onClick={() => navigate('/workflows')}
          className="-ml-1 shrink-0 p-1.5 rounded text-on-surface-variant hover:bg-surface-highest hover:text-on-surface transition-colors"
          aria-label={t('common:back')}
        >
          <ArrowLeft size={18} />
        </button>
        <h1 className="font-headline text-sm font-semibold text-on-surface truncate flex-1">
          {workflow?.name ?? t('common:loadingDots')}
        </h1>
      </header>
      <div className="shrink-0 flex items-center gap-2 px-3 py-1.5 bg-amber-500/10 text-amber-700 dark:text-amber-300 text-[11px]">
        <Screen size={13} className="shrink-0" />
        <span>{t('workflows:mobileReadonlyHint')}</span>
      </div>
      <section className="np-designer flex-1 relative bg-surface overflow-hidden">
        {isLoading ? (
          <div className="absolute inset-0 flex items-center justify-center text-on-surface-variant text-sm">
            {t('common:loadingDots')}
          </div>
        ) : isError ? (
          <div className="absolute inset-0 flex items-center justify-center text-on-surface-variant text-sm">
            {t('common:loadError')}
          </div>
        ) : (
          <NodeScaleOverrideContext.Provider value={MOBILE_SCALE_INDEX}>
            <ReactFlow
              nodes={nodes}
              edges={edges}
              nodeTypes={nodeTypes}
              edgeTypes={edgeTypes}
              defaultEdgeOptions={{ type: 'labeled', animated: false, markerEnd: { type: MarkerType.ArrowClosed, color: '#c3c6d7' } }}
              nodesDraggable={false}
              elementsSelectable={false}
              edgesReconnectable={false}
              connectionMode={ConnectionMode.Loose}
              isValidConnection={() => false}
              minZoom={0.1}
              fitView
              // minZoom floor keeps nodes legible on big graphs: instead of shrinking to fit
              // everything, fitView clamps at 0.7 and the user pans. Small graphs still zoom in
              // (up to 1.6) to fill the phone. Combined with the larger node scale (provider).
              fitViewOptions={{ padding: 0.08, minZoom: 0.7, maxZoom: 1.6 }}
            >
              <Background variant={BackgroundVariant.Dots} gap={16} size={1} />
              <Controls showInteractive={false} />
            </ReactFlow>
          </NodeScaleOverrideContext.Provider>
        )}
      </section>
    </div>
  );
}

export function MobileWorkflowView({ workflowId }: Readonly<{ workflowId: string }>) {
  return (
    <ReactFlowProvider>
      <MobileWorkflowViewInner workflowId={workflowId} />
    </ReactFlowProvider>
  );
}
