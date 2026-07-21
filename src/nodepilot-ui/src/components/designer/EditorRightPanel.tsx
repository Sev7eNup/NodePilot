import type { Node, Edge } from '@xyflow/react';
import type { ManagedMachine, Credential } from '../../types/api';
import { ResizeHandle } from './library/NodeLibrary';
import { PropertiesPanel } from './PropertiesPanel';
import { BulkEditPanel } from './BulkEditPanel';
import { EdgePropertiesPanel } from './EdgePropertiesPanel';
import { useDesignStore } from '../../stores/designStore';

interface EditorRightPanelProps {
  fullscreen: boolean;
  nodes: Node[];
  edges: Edge[];
  selectedNode: Node | null;
  selectedEdge: Edge | null;
  machines: ManagedMachine[];
  credentials: Credential[];
  workflowId: string | undefined;
  canWrite: boolean;
  panelSize: number;
  panelHandleProps: { onMouseDown: (e: React.MouseEvent) => void; onDoubleClick: () => void };
  setNodes: (updater: (nds: Node[]) => Node[]) => void;
  setSelected: (s: null) => void;
  setLeftTab: (t: 'nodes' | 'workflows') => void;
  setLeftCollapsed: (c: boolean) => void;
  handleBulkApply: (nodeIds: string[], patch: Record<string, unknown>, configPatch?: Record<string, unknown>) => void;
  handleNodeDataUpdate: (nodeId: string, newData: Record<string, unknown>) => void;
  handleEdgeUpdate: (edgeId: string, patch: Partial<Edge>) => void;
  handleEdgeDelete: (edgeId: string) => void;
  onVarHover: (producerNodeId: string | null) => void;
}

/**
 * Selects which right-side panel to show based on the current selection:
 *   - ≥2 nodes selected → BulkEditPanel
 *   - exactly 1 node selected → PropertiesPanel
 *   - exactly 1 edge selected → EdgePropertiesPanel
 *   - nothing → nothing
 *
 * Hidden entirely in fullscreen mode.
 */
export function EditorRightPanel({
  fullscreen,
  nodes,
  edges,
  selectedNode,
  selectedEdge,
  machines,
  credentials,
  workflowId,
  canWrite,
  panelSize,
  panelHandleProps,
  setNodes,
  setSelected,
  setLeftTab,
  setLeftCollapsed,
  handleBulkApply,
  handleNodeDataUpdate,
  handleEdgeUpdate,
  handleEdgeDelete,
  onVarHover,
}: Readonly<EditorRightPanelProps>) {
  const expertMode = useDesignStore((s) => s.designerMode === 'expert');
  if (fullscreen) return null;

  const multiSelected = nodes.filter((n) => n.selected);
  if (!expertMode && multiSelected.length >= 2) return null;
  if (multiSelected.length >= 2) {
    return (
      <>
        <ResizeHandle direction="horizontal" {...panelHandleProps} />
        <BulkEditPanel
          selectedNodes={multiSelected}
          machines={machines}
          onApply={handleBulkApply}
          onClose={() => setNodes((nds) => nds.map((n) => ({ ...n, selected: false })))}
          width={panelSize}
        />
      </>
    );
  }

  if (selectedNode) {
    return (
      <>
        <ResizeHandle direction="horizontal" {...panelHandleProps} />
        <PropertiesPanel
          node={selectedNode}
          allNodes={nodes}
          edges={edges}
          machines={machines}
          credentials={credentials}
          onUpdate={handleNodeDataUpdate}
          onClose={() => setSelected(null)}
          width={panelSize}
          workflowId={workflowId}
          onOpenWorkflowPicker={() => { setLeftTab('workflows'); setLeftCollapsed(false); }}
          onVarHover={onVarHover}
          canWrite={canWrite}
        />
      </>
    );
  }

  if (selectedEdge) {
    return (
      <>
        <ResizeHandle direction="horizontal" {...panelHandleProps} />
        <EdgePropertiesPanel
          edge={selectedEdge}
          allNodes={nodes}
          allEdges={edges}
          onUpdate={handleEdgeUpdate}
          onDelete={handleEdgeDelete}
          onClose={() => setSelected(null)}
          width={panelSize}
          canWrite={canWrite}
        />
      </>
    );
  }

  return null;
}
