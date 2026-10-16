import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Node } from '@xyflow/react';
import { autoLayout, autoLayoutTB, autoLayoutCompact, autoLayoutELK } from '../lib/autoLayout';
import { useDesignStore, LAYOUT_MODES } from '../stores/designStore';
import { toast } from '../stores/toastStore';
import type { WorkflowGraphSource } from './useWorkflowHistory';
import type { useWorkflowPersistence } from './useWorkflowPersistence';

interface UseTidyLayoutArgs {
  workflowId: string | undefined;
  graph: WorkflowGraphSource;
  /** True while an async (ELK) layout owns the draft. The page holds it because saving reads it too. */
  isTidying: boolean;
  setTidyingWorkflowId: (update: (current: string | null) => string | null) => void;
  persistence: Pick<ReturnType<typeof useWorkflowPersistence>, 'beginAsyncGraphEdit' | 'applyAsyncGraphEdit' | 'markDirty'>;
  commitHistory: (label?: string) => void;
}

/**
 * Auto-layout ("Tidy"): cycles LR, TB, Compact and ELK in expert mode, always LR in standard
 * mode. The first tidy of a visit remembers the original positions so they can be restored.
 */
export function useTidyLayout({
  workflowId, graph, isTidying, setTidyingWorkflowId, persistence, commitHistory,
}: UseTidyLayoutArgs) {
  const { t } = useTranslation(['editor']);
  const { nodes, edges, setNodes } = graph;
  const { beginAsyncGraphEdit, applyAsyncGraphEdit, markDirty } = persistence;
  const designerMode = useDesignStore((s) => s.designerMode);
  const layoutMode = useDesignStore((s) => s.layoutMode);
  const setLayoutMode = useDesignStore((s) => s.setLayoutMode);
  // Snapshot of positions before the first auto-layout in this session.
  const origLayoutRef = useRef<{
    workflowId: string | undefined;
    positions: Map<string, { position: Node['position']; parentId: Node['parentId'] }>;
  } | null>(null);
  const [origLayoutWorkflowId, setOrigLayoutWorkflowId] = useState<string | null>(null);
  const hasOrigLayout = origLayoutWorkflowId === workflowId;

  const tidyLayout = async () => {
    if (nodes.length === 0 || isTidying) return;
    const modeToApply = designerMode === 'standard' ? 'LR' : layoutMode;
    const next = LAYOUT_MODES[(LAYOUT_MODES.indexOf(layoutMode) + 1) % LAYOUT_MODES.length];
    const rememberOriginal = () => {
      if (origLayoutRef.current?.workflowId !== workflowId) {
        origLayoutRef.current = {
          workflowId,
          positions: new Map(nodes.map((n) => [n.id, { position: { ...n.position }, parentId: n.parentId }])),
        };
        setOrigLayoutWorkflowId(workflowId ?? null);
      }
    };

    if (modeToApply === 'ELK') {
      const token = beginAsyncGraphEdit();
      if (!token || !workflowId) return;
      setTidyingWorkflowId(() => workflowId);
      try {
        const laidOut = await autoLayoutELK(nodes, edges);
        // Reject results after another edit, publish, unmount, or workflow visit.
        if (!applyAsyncGraphEdit(token, laidOut, edges)) return;
        rememberOriginal();
        commitHistory('Tidy layout');
        setNodes(laidOut);
        if (designerMode === 'expert') setLayoutMode(next);
      } catch (err) {
        toast.error(t('editor:tidyFailed', { message: (err as Error).message }));
      } finally {
        setTidyingWorkflowId((current) => current === workflowId ? null : current);
      }
      return;
    }

    rememberOriginal();
    commitHistory('Tidy layout');
    markDirty();
    if (modeToApply === 'LR') setNodes(autoLayout(nodes, edges));
    else if (modeToApply === 'TB') setNodes(autoLayoutTB(nodes, edges));
    else setNodes(autoLayoutCompact(nodes, edges));
    if (designerMode === 'expert') setLayoutMode(next);
  };

  const restoreOrigLayout = () => {
    if (!origLayoutRef.current || origLayoutRef.current.workflowId !== workflowId) return;
    commitHistory('Restore layout');
    markDirty();
    const { positions } = origLayoutRef.current;
    // Tidy changes only positions. Keep later edits and graph membership; a reparented
    // node also keeps its position because its coordinate system has changed.
    setNodes((current) => current.map((node) => {
      const original = positions.get(node.id);
      return original && original.parentId === node.parentId
        ? { ...node, position: { ...original.position } }
        : node;
    }));
    origLayoutRef.current = null;
    setOrigLayoutWorkflowId(null);
  };

  return {
    tidyLayout: () => { void tidyLayout(); },
    isTidying,
    layoutMode,
    restoreOrigLayout,
    hasOrigLayout,
  };
}

export type TidyLayoutApi = ReturnType<typeof useTidyLayout>;
