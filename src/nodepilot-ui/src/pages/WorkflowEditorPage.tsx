import { useParams, useNavigate, useSearchParams } from 'react-router';
import { AgentCanvasExecutionContext } from '../components/designer/agents/AgentRunPanel';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import type { Workflow, MachineOption, Credential } from '../types/api';
import { Minimize } from '@carbon/icons-react';
import { randomUuid } from '../lib/uuid';
import { useState, useCallback, useEffect, useMemo, useRef } from 'react';
import {
  ReactFlow,
  ReactFlowProvider,
  useReactFlow,
  useStore,
  Controls,
  Background,
  addEdge,
  reconnectEdge,
  useNodesState,
  useEdgesState,
  type Connection,
  type FinalConnectionState,
  type Node,
  type Edge,
  type OnSelectionChangeParams,
  type IsValidConnection,
  type NodeTypes,
  BackgroundVariant,
  MarkerType,
  MiniMap,
  Panel,
  SelectionMode,
  ConnectionMode,
  type EdgeTypes,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { ActivityNode } from '../components/designer/nodes/ActivityNode';
import { StickyNoteNode } from '../components/designer/nodes/StickyNoteNode';
import { GroupNode, GroupNodeEditContext, GroupDropTargetContext } from '../components/designer/nodes/GroupNode';
import { lintWorkflow } from '../lib/workflowLint';
import { useSettledGraph } from '../hooks/useSettledGraph';
import { getSmartDefaults } from '../lib/lastSimilarNode';
import { newActivityConfig } from '../lib/customActivities';
import { absolutePosition, reparentDraggedNodes, findDropTargetGroupId } from '../lib/groupReparenting';
import { useDesignStore, MACHINE_COLORS } from '../stores/designStore';
import { usePointerFlowPosition } from '../stores/pointerFlowPositionStore';
import { LabeledEdge, EdgeInsertContext } from '../components/designer/edges/LabeledEdge';
import { NpEdgeMarkerDefs } from '../components/designer/edges/NpEdgeMarkerDefs';
import { NpConnectionLine } from '../components/designer/edges/NpConnectionLine';
import { EdgeDetachPreview } from '../components/designer/edges/EdgeDetachPreview';
import { npStatusFromExecution, STATUS_COLOR_VAR } from '../lib/statusTokens';
import { SubWorkflowPreviewContext } from '../components/designer/nodes/ActivityNode';
import { EdgeInserter } from '../components/designer/overlays/EdgeInserter';
import { NodeContextMenu } from '../components/designer/overlays/NodeContextMenu';
import { EdgeContextMenu } from '../components/designer/overlays/EdgeContextMenu';
import { QuickConnectPicker } from '../components/designer/overlays/QuickConnectPicker';
import { SimulationBanner } from '../components/designer/overlays/SimulationBanner';
import { pushRecentWorkflow } from '../components/designer/overlays/WorkflowQuickSwitcher';
import {
  carrySelection,
  getDownstreamNodeIds,
  renameVariableInNodeData,
  renameVariableInEdge,
} from '../lib/editorGraphHelpers';
import { insertJunctionForFanIn, requiresJunctionForConnection } from '../lib/junctionFanIn';
import {
  DEFAULT_SOURCE_PORT,
  DEFAULT_TARGET_PORT,
  connectionRadiusForZoom,
  edgeSourcePort,
  edgeTargetPort,
  normalizePort,
  quantizeZoom,
} from '../lib/edgePorts';
import { isAllowedNewConnection } from '../lib/edgeDetach';
import { FolderPathBreadcrumb } from '../components/designer/FolderPathBreadcrumb';
import { NewWorkflowButton } from '../components/designer/NewWorkflowButton';
import { ResizeHandle } from '../components/designer/library/NodeLibrary';
import { EditorOverlays } from '../components/designer/EditorOverlays';
import { ExecutionPanel } from '../components/designer/ExecutionPanel';
import { useWorkflowSignalR } from '../hooks/useSignalR';
import { useResizable } from '../hooks/useResizable';
import { confirmDialog } from '../stores/confirmStore';
import { useWorkflowHistory } from '../hooks/useWorkflowHistory';
import { useWorkflowClipboard } from '../hooks/useWorkflowClipboard';
import { useWorkflowSimulation } from '../hooks/useWorkflowSimulation';
import { useEditorKeyboardShortcuts } from '../hooks/useEditorKeyboardShortcuts';
import { useNodeAnnotations } from '../hooks/useNodeAnnotations';
import { useCoverageHeatmap } from '../hooks/useCoverageHeatmap';
import { useCriticalPath } from '../hooks/useCriticalPath';
import { useNodeOperations } from '../hooks/useNodeOperations';
import { useCanvasConnect } from '../hooks/useCanvasConnect';
import { useCanvasExecutionState } from '../hooks/useCanvasExecutionState';
import { useDisplayedGraph } from '../hooks/useDisplayedGraph';
import { useWorkflowLock } from '../hooks/useWorkflowLock';
import { useWorkflowPersistence } from '../hooks/useWorkflowPersistence';
import { useWorkflowExecution } from '../hooks/useWorkflowExecution';
import { useEditorOverlays } from '../hooks/useEditorOverlays';
import { useEditorLifecycle } from '../hooks/useEditorLifecycle';
import { useEdgeDetach } from '../hooks/useEdgeDetach';
import { useTidyLayout } from '../hooks/useTidyLayout';
import { useWorkflowExport } from '../hooks/useWorkflowExport';
import { EditorHeader } from '../components/designer/EditorHeader';
import { MaintenanceWindowBadge } from '../components/designer/MaintenanceWindowBadge';
import { EditorStatusBanners } from '../components/designer/EditorStatusBanners';
import { EditorRightPanel } from '../components/designer/EditorRightPanel';
import { AiWorkflowChatPanel } from '../components/ai/AiWorkflowChatPanel';
import { useKnowledgeChatSessionStore } from '../stores/knowledgeChatSessionStore';
import { stripRuntimeDefinition } from '../lib/workflowDefinitionSanitizer';
import { toast } from '../stores/toastStore';
import { EditorSidebar } from '../components/designer/EditorSidebar';
import { useRole } from '../lib/rbac';
import { useAuthStore } from '../stores/authStore';
import { ErrorBoundary } from '../components/ErrorBoundary';

/* ---- Editor Page ---- */

type SelectedItem = { type: 'node'; id: string } | { type: 'edge'; id: string } | null;

/** Stable identity for the closed AI panel, so its memo does not churn on every frame. */
const EMPTY_AI_SELECTION = Object.freeze({ nodeLabels: [] as string[], edgeCount: 0 });

export function WorkflowEditorPage() {
  const { t } = useTranslation(['editor']);
  // Local boundary so a SignalR init crash, malformed workflow JSON, or a React Flow render
  // error only blanks the editor area and leaves the AppLayout sidebar, header and navigation
  // usable. The outer App.tsx boundary still catches anything that escapes here.
  return (
    <ErrorBoundary
      scope="WorkflowEditor"
      fallback={(error, reset) => (
        <div className="h-full flex items-center justify-center bg-surface p-6">
          <div className="max-w-lg w-full rounded-lg border border-error/30 bg-surface-container p-6 shadow-sm">
            <h2 className="text-lg font-semibold text-error mb-2">{t('editor:loadFailed')}</h2>
            <p className="text-sm text-on-surface-variant mb-4">
              {t('editor:loadFailedDescription')}
            </p>
            <pre className="text-xs bg-surface-container-high text-on-surface p-3 rounded overflow-auto max-h-48 mb-4">
              {error.message}
            </pre>
            <div className="flex gap-2">
              <button
                type="button"
                onClick={reset}
                className="px-3 py-1.5 text-sm rounded bg-primary text-on-primary hover:opacity-90"
              >
                {t('editor:tryAgain')}
              </button>
              <button
                type="button"
                onClick={() => globalThis.location.reload()}
                className="px-3 py-1.5 text-sm rounded border border-outline text-on-surface hover:bg-surface-container-high"
              >
                {t('editor:reloadPage')}
              </button>
            </div>
          </div>
        </div>
      )}
    >
      <ReactFlowProvider>
        <WorkflowEditorInner />
      </ReactFlowProvider>
    </ErrorBoundary>
  );
}

function WorkflowEditorInner() {
  const { t } = useTranslation(['editor', 'workflows', 'common']);
  const { id } = useParams<{ id: string }>();
  const [searchParams] = useSearchParams();
  const focusNode = searchParams.get('focusNode');
  const focusedDependencyRef = useRef<string | null>(null);
  const { screenToFlowPosition, fitView, setCenter } = useReactFlow();
  // Quantized, so the page re-renders on a few zoom steps instead of every wheel tick.
  const zoomStep = useStore((s) => quantizeZoom(s.transform[2]));
  const canvasRef = useRef<HTMLElement>(null);
  // Track the cursor in flow-coords (rAF-throttled) so nodes can reveal their ports on proximity.
  // Only runs while auto-hide is on; off-canvas resets to null so ports hide again.
  const setPointerFlow = usePointerFlowPosition((s) => s.set);
  const pointerRafRef = useRef<number | null>(null);
  const handleCanvasPointerMove = useCallback((e: React.PointerEvent) => {
    if (!useDesignStore.getState().autoHidePorts) return;
    const { clientX, clientY } = e;
    if (pointerRafRef.current != null) return;
    pointerRafRef.current = requestAnimationFrame(() => {
      pointerRafRef.current = null;
      const p = screenToFlowPosition({ x: clientX, y: clientY });
      setPointerFlow(p.x, p.y);
    });
  }, [screenToFlowPosition, setPointerFlow]);
  const handleCanvasPointerLeave = useCallback(() => {
    if (pointerRafRef.current != null) {
      cancelAnimationFrame(pointerRafRef.current);
      pointerRafRef.current = null;
    }
    setPointerFlow(null, null);
  }, [setPointerFlow]);
  // On unmount, cancel any pending frame and clear the shared pointer store so a stale cursor
  // cannot leak port reveals into other ReactFlow instances that reuse ActivityNode.
  // getState() keeps this effect dependency-free.
  useEffect(() => () => {
    if (pointerRafRef.current != null) cancelAnimationFrame(pointerRafRef.current);
    usePointerFlowPosition.getState().set(null, null);
  }, []);
  const navigate = useNavigate();
  const { canWrite: roleCanWrite, isAdmin, isViewer } = useRole();
  const currentUserId = useAuthStore((s) => s.userId);
  const designerMode = useDesignStore((s) => s.designerMode);
  // Atelier marker on <html>: designer UI portaled to body (activity tooltips via
  // .np-tooltip-portal) escapes the .np-designer token scope, so the root marker lets
  // designer-atelier.css re-assert the Atelier surface tokens on those portals too. Bound to
  // the editor's lifetime, so other routes never render Atelier-tinted.
  useEffect(() => {
    const rootEl = document.documentElement;
    rootEl.classList.add('wd-atelier-on');
    return () => rootEl.classList.remove('wd-atelier-on');
  }, []);
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const [tidyingWorkflowId, setTidyingWorkflowId] = useState<string | null>(null);
  const isTidying = !!id && tidyingWorkflowId === id;
  const [selected, setSelected] = useState<SelectedItem>(null);
  const [connectionNotice, setConnectionNotice] = useState('');
  const [leftTab, setLeftTab] = useState<'nodes' | 'workflows'>('workflows');
  const [leftCollapsed, setLeftCollapsed] = useState(false);
  const overlays = useEditorOverlays();
  const { liveExecution, liveExecutions, liveActiveCount, connected, joinExecution, leaveExecution } = useWorkflowSignalR(id);
  const {
    effectiveCanvasExecution,
    replayExecutionId,
    replaySteps,
    scrubTimeMs,
    designerCanvasRunIsTerminal,
    designerCanvasRunShortId,
    pinCanvasExecution,
    clearReplay,
    clearDesignerCanvasHighlight,
    toggleReplay,
    scrubTo,
  } = useCanvasExecutionState({ liveExecutions, workflowId: id, joinExecution, leaveExecution });
  // Default panel sizes target screens 1440px and wider: large enough that workflow names,
  // node-property labels and history rows fit without a first drag. The upper bounds let
  // users on wider screens push the panels further.
  const leftPanel = useResizable({ initialSize: 350, minSize: 160, maxSize: 500, direction: 'horizontal' });
  const rightPanel = useResizable({ initialSize: 450, minSize: 280, maxSize: 640, direction: 'horizontal', reverse: true });
  const bottomPanel = useResizable({ initialSize: 340, minSize: 100, maxSize: 700, direction: 'vertical', reverse: true });

  const { data: workflow } = useQuery({
    queryKey: ['workflow', id],
    queryFn: () => api.get<Workflow>(`/workflows/${id}`),
    enabled: !!id,
  });

  const lockState = useWorkflowLock({ workflowId: id, workflow, currentUserId, roleCanWrite });
  const persistence = useWorkflowPersistence({
    workflowId: id, workflow, nodes, edges, suspendAutoSave: isTidying,
  });
  const {
    name, isDirty, isPublishing,
    rename, markDirty, saveAsync, syncFromServer,
    beginAsyncGraphEdit, applyAsyncGraphEdit,
  } = persistence;
  // Publish saves and releases the lock atomically. Freeze all edit affordances as soon as it
  // is queued, and while an async layout still owns a pre-apply graph snapshot.
  const canWrite = lockState.canWrite && !isPublishing && !isTidying;

  const { data: allWorkflows } = useQuery({
    queryKey: ['workflows', 'names'],
    queryFn: () => api.get<Array<{ id: string; name: string }>>('/workflows/names'),
    staleTime: 60_000,
  });

  const { data: machines = [] } = useQuery({ queryKey: ['machines', 'options'], queryFn: () => api.get<MachineOption[]>('/machines/options') });
  const { data: credentials = [] } = useQuery({ queryKey: ['credentials'], queryFn: () => api.get<Credential[]>('/credentials') });

  // ---- Undo / Redo History ------------------------------------------------
  // The graph source of truth for everything that snapshots or rewrites it. React Flow's
  // store holds the projected graph instead and must not be used for this.
  const graphSource = { nodes, edges, setNodes, setEdges };
  // Same graph behind a stable reference, for callbacks that must read it on demand without
  // taking a new identity on every frame of a drag.
  const liveGraphRef = useRef(graphSource);
  useEffect(() => { liveGraphRef.current = graphSource; });
  // Graph as of the last pause in editing — carries the whole-graph passes (lint, autosave
  // draft) so they do not run per frame.
  const settledGraph = useSettledGraph(nodes, edges);
  const { historyPast, historyFuture, commitHistory, undo, redo } = useWorkflowHistory(id, graphSource, markDirty);

  // Debounced commit for rapid property edits (e.g. typing in script field).
  // Structural changes (disabled, breakpoint, outputVariable) still commit immediately.
  const propertyEditTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const commitHistoryForPropertyEdit = useCallback(() => {
    if (propertyEditTimer.current) clearTimeout(propertyEditTimer.current);
    propertyEditTimer.current = setTimeout(() => {
      commitHistory();
      propertyEditTimer.current = null;
    }, 800);
  }, [commitHistory]);

  // Tracks which workflow id was already fit-viewed, so lifecycle refetches
  // (lock/unlock/publish/disable) do not re-fit and steal the user's current zoom and pan.
  const fittedWorkflowIdRef = useRef<string | null>(null);
  // Workflow whose server graph is already on the canvas. A refetch of the same workflow keeps the
  // user's selection, so the open properties panel survives lock/unlock/publish.
  const seededWorkflowIdRef = useRef<string | null>(null);

  // ---- Copy / Paste -------------------------------------------------------
  // Copy and paste use the system clipboard, so nodes can go to a text editor and back. Inline
  // secrets in node config go along, as with copying a script by hand.
  const {
    copySelection, pasteText, duplicateSelection, resetPasteCount, updateSelection,
  } = useWorkflowClipboard(commitHistory, markDirty, graphSource);

  const copyForClipboard = useCallback((): string | null => {
    const text = copySelection();
    if (text) toast.success(t('editor:clipboard.copied'));
    return text;
  }, [copySelection, t]);

  // Pasting needs write access. Text that is not JSON is ignored, broken JSON is reported.
  const pasteFromText = useCallback((text: string) => {
    if (!canWrite) return;
    // Text copied elsewhere lands near the middle of the visible canvas, like an inserted snippet.
    const rect = canvasRef.current?.getBoundingClientRect();
    const origin = rect
      ? screenToFlowPosition({ x: rect.left + rect.width / 2 - 200, y: rect.top + rect.height / 2 - 100 })
      : { x: 100, y: 100 };
    const result = pasteText(text, origin);
    if (!result.ok) {
      if (/^\s*[[{]/.test(text)) toast.error(t(`editor:clipboard.error.${result.error}`));
      return;
    }
    if (result.redactedValues > 0) toast.info(t('editor:clipboard.redactedValues', { count: result.redactedValues }));
  }, [canWrite, screenToFlowPosition, pasteText, t]);

  // The command palette fires no copy or paste event, so it goes through the asynchronous
  // clipboard API, which browsers offer only over HTTPS or on localhost.
  const copyToClipboard = useCallback(() => {
    const text = copySelection();
    if (!text) return;
    if (!navigator.clipboard) {
      toast.error(t('editor:clipboard.unavailable'));
      return;
    }
    navigator.clipboard.writeText(text).then(
      () => toast.success(t('editor:clipboard.copied')),
      () => toast.error(t('editor:clipboard.unavailable')),
    );
  }, [copySelection, t]);
  const pasteFromClipboard = useCallback(() => {
    if (!navigator.clipboard) {
      toast.error(t('editor:clipboard.unavailable'));
      return;
    }
    navigator.clipboard.readText().then(pasteFromText, () => toast.error(t('editor:clipboard.unavailable')));
  }, [pasteFromText, t]);
  const duplicateIfWritable = useCallback(() => { if (canWrite) duplicateSelection(); }, [canWrite, duplicateSelection]);

  useEffect(() => {
    if (workflow) {
      if (!syncFromServer(workflow.name)) return;
      resetPasteCount();
      pushRecentWorkflow(workflow.id);
      try {
        // Always present here: this comes from the single-workflow endpoint, not the list.
        const def = JSON.parse(workflow.definitionJson ?? '{}');
        // Groups must come before their children in the array (React Flow renders in array order).
        const rawNodes: Node[] = (def.nodes || []);
        const serverNodes = [...rawNodes].sort((a) => (a.type === 'group' ? -1 : 1));
        const serverEdges: Edge[] = def.edges || [];
        if (seededWorkflowIdRef.current === workflow.id) {
          setNodes((prev) => carrySelection(serverNodes, prev));
          setEdges((prev) => carrySelection(serverEdges, prev));
        } else {
          setNodes(serverNodes);
          setEdges(serverEdges);
        }
        seededWorkflowIdRef.current = workflow.id;
        // Fit the view once, on the first load of a workflow that has nodes. The setTimeout
        // lets React Flow render the nodes into its internal store before the viewport is
        // computed. The fitView prop on <ReactFlow> is not used because it re-fires on every
        // transition from zero to one node and would distort both the zoom level and the drop
        // position during the first drag-and-drop.
        if (rawNodes.length > 0 && fittedWorkflowIdRef.current !== workflow.id) {
          fittedWorkflowIdRef.current = workflow.id;
          setTimeout(() => fitView({ padding: 0.15 }), 50);
        }
      } catch { setNodes([]); setEdges([]); }
    }
  }, [workflow, setNodes, setEdges, resetPasteCount, fitView, syncFromServer]);

  // ---- Runtime annotations: derived, merged into the display projection only ----
  const { nodeAnnotations, varFlowEdgeIds, stepStats, legendMachines, handleVarHover } = useNodeAnnotations({
    workflowId: id,
    workflowIsEnabled: !!workflow?.isEnabled,
    nodes, edges,
    selected,
    liveExecution: effectiveCanvasExecution,
    replayExecutionId, replaySteps, scrubTimeMs,
    machines,
  });
  const machineColoringEnabled = useDesignStore((s) => s.machineColoringEnabled);
  const coverageHeatmapEnabled = useDesignStore((s) => s.coverageHeatmapEnabled);
  const coverageWindowDays = useDesignStore((s) => s.coverageWindowDays);
  const criticalPathEnabled = useDesignStore((s) => s.criticalPathEnabled);
  const coverage = useCoverageHeatmap({
    workflowId: id,
    enabled: coverageHeatmapEnabled,
    windowDays: coverageWindowDays,
    nodes,
  });
  const criticalPath = useCriticalPath(criticalPathEnabled, nodes, edges, stepStats);

  const showConnectionNotice = useCallback((message: string) => {
    setConnectionNotice(message);
    globalThis.setTimeout(() => {
      setConnectionNotice((current) => current === message ? '' : current);
    }, 3200);
  }, []);

  const hasDuplicateConnection = useCallback(
    (source: string | null | undefined, target: string | null | undefined) =>
      !!source && !!target && edges.some((e) => e.source === source && e.target === target),
    [edges],
  );

  const isValidConnection = useCallback<IsValidConnection>(
    (connection) => isAllowedNewConnection(connection, edges),
    [edges],
  );

  const offerRequiredJunction = useCallback(
    async (connection: Connection, movingEdge?: Edge) => {
      if (!connection.target) return;
      const target = nodes.find((node) => node.id === connection.target);
      const targetLabel = ((target?.data as Record<string, unknown> | undefined)?.label as string | undefined)
        ?? connection.target;
      // The insertion below is computed from the graph as it is now, so claim that draft before
      // the dialog awaits and reject the result if anything replaced it meanwhile.
      const token = beginAsyncGraphEdit();
      if (!token) {
        showConnectionNotice(t('editor:fanIn.cancelled'));
        return;
      }
      const accepted = await confirmDialog({
        title: t('editor:fanIn.title'),
        message: t('editor:fanIn.message', { target: targetLabel }),
        confirmLabel: t('editor:fanIn.insert'),
      });
      if (!accepted) {
        showConnectionNotice(t('editor:fanIn.cancelled'));
        return;
      }

      const result = insertJunctionForFanIn(nodes, edges, connection, {
        junctionId: `step-${randomUuid()}`,
        incomingEdgeId: `edge-${randomUuid()}`,
        outgoingEdgeId: `edge-${randomUuid()}`,
        label: t('editor:fanIn.defaultLabel'),
        movingEdge,
      });
      // Also marks the draft dirty, so no separate markDirty() here.
      if (!applyAsyncGraphEdit(token, result.nodes, result.edges)) {
        showConnectionNotice(t('editor:fanIn.cancelled'));
        return;
      }
      commitHistory(movingEdge ? 'Move edge with junction' : 'Add edge with junction');
      setNodes(result.nodes);
      setEdges(result.edges);
      setSelected({ type: 'node', id: result.junctionId });
      showConnectionNotice(t('editor:fanIn.inserted'));
    },
    [nodes, edges, t, beginAsyncGraphEdit, applyAsyncGraphEdit, commitHistory,
      setNodes, setEdges, setSelected, showConnectionNotice],
  );

  const onConnect = useCallback(
    (params: Connection) => {
      if (hasDuplicateConnection(params.source, params.target)) {
        showConnectionNotice(t('editor:connection.duplicate'));
        return;
      }
      if (requiresJunctionForConnection(params.target, nodes, edges)) {
        void offerRequiredJunction(params);
        return;
      }
      commitHistory('Add edge');
      markDirty();
      setEdges((eds: Edge[]) => addEdge({
        ...params,
        sourceHandle: normalizePort(params.sourceHandle, DEFAULT_SOURCE_PORT),
        targetHandle: normalizePort(params.targetHandle, DEFAULT_TARGET_PORT),
        type: 'labeled',
        data: { label: '', condition: '', disabled: false },
      }, eds));
    },
    [nodes, edges, setEdges, commitHistory, hasDuplicateConnection, showConnectionNotice, markDirty, offerRequiredJunction, t],
  );

  // Drags the endpoint of an existing edge onto a different source or target (detach and
  // reattach). edge.data (label, condition, disabled, port defaults) is preserved.
  const onReconnect = useCallback(
    (oldEdge: Edge, newConnection: Connection) => {
      const targetChanged =
        newConnection.source !== oldEdge.source || newConnection.target !== oldEdge.target;
      if (targetChanged) {
        const isDuplicate = edges.some(
          (e) =>
            e.id !== oldEdge.id &&
            e.source === newConnection.source &&
            e.target === newConnection.target,
        );
        if (isDuplicate) {
          showConnectionNotice('Diese Verbindung existiert bereits.');
          return;
        }
        if (requiresJunctionForConnection(newConnection.target, nodes, edges, oldEdge.id)) {
          void offerRequiredJunction(newConnection, oldEdge);
          return;
        }
      }
      commitHistory('Move edge');
      markDirty();
      setEdges((eds: Edge[]) =>
        reconnectEdge(
          oldEdge,
          {
            ...newConnection,
            sourceHandle: normalizePort(newConnection.sourceHandle, DEFAULT_SOURCE_PORT),
            targetHandle: normalizePort(newConnection.targetHandle, DEFAULT_TARGET_PORT),
          },
          eds,
        ),
      );
    },
    [nodes, edges, setEdges, commitHistory, showConnectionNotice, markDirty, offerRequiredJunction],
  );

  const onSelectionChange = useCallback(({ nodes: sn, edges: se }: OnSelectionChangeParams) => {
    updateSelection({ nodeIds: sn.map((n) => n.id), edgeIds: se.map((e) => e.id) });
    if (se.length === 1 && sn.length === 0) setSelected({ type: 'edge', id: se[0].id });
    else if (sn.length === 1 && se.length === 0) setSelected({ type: 'node', id: sn[0].id });
    else setSelected(null);
  }, [updateSelection]);

  // ---- Quick-edit popup (double-click on a node) --------------------------
  const { setQuickEdit, setScriptEditNodeId } = overlays;
  const onNodeDoubleClick = useCallback((_e: React.MouseEvent, node: Node) => {
    if (node.type !== 'activity') return;
    const activityType = (node.data as Record<string, unknown>)?.activityType as string | undefined;
    // runScript opens the full ScriptEditorDialog (CodeMirror, PowerShell lint, run button)
    // instead of the quick-edit popup, whose small textarea is not enough for scripts.
    if (activityType === 'runScript') {
      setScriptEditNodeId(node.id);
      return;
    }
    setQuickEdit({ node, x: _e.clientX, y: _e.clientY });
  }, [setQuickEdit, setScriptEditNodeId]);

  // Live drop-target highlight: id of the group the dragged node hovers over and would be
  // reparented into on drop. Null clears the highlight. Only changes when the target changes,
  // so re-renders stay rare even though onNodeDrag fires on every move.
  const [dropTargetGroupId, setDropTargetGroupId] = useState<string | null>(null);

  // The drag callbacks hand back a native event, not a synthetic one, because React Flow types
  // OnNodeDrag as MouseEvent | TouchEvent. Only the node is needed here.
  const onNodeDrag = useCallback((_e: MouseEvent | TouchEvent, node: Node) => {
    if (node.type === 'group') { setDropTargetGroupId(null); return; }
    const over = findDropTargetGroupId(nodes, node);
    // Highlight only when dropping there would change the parent (skip the node's current group).
    const next = over && over !== (node.parentId ?? null) ? over : null;
    setDropTargetGroupId((prev) => (prev === next ? prev : next));
  }, [nodes]);

  // Drag into a group: when a drag ends, a node whose center landed inside a group frame becomes
  // that group's child, and a node dragged out is detached. History is already committed by
  // onNodeDragStart('Move nodes'), so the reparent rides the same undo step. The third argument
  // carries every node of a multi-select drag.
  const onNodeDragStop = useCallback((_e: MouseEvent | TouchEvent, node: Node, draggedNodes: Node[]) => {
    setDropTargetGroupId(null);
    const ids = (draggedNodes.length > 0 ? draggedNodes : [node]).map((n) => n.id);
    markDirty();
    setNodes((nds) => reparentDraggedNodes(nds, ids) ?? nds);
  }, [setNodes, markDirty]);

  const handleQuickEditSave = useCallback((nodeId: string, configPatch: Record<string, unknown>) => {
    // Same order as the delete handler: commitHistory() and markDirty() must run before
    // setNodes, otherwise useWorkflowHistory snapshots the already mutated state and undo
    // breaks. Programmatic setNodes does not trigger onNodesChange, so the auto-dirty logic
    // in onNodesChangeDirty does not run here and the dirty flag is set explicitly.
    commitHistory('Quick edit');
    markDirty();
    setNodes((nds) => nds.map((n) => {
      if (n.id !== nodeId) return n;
      const config = (n.data as Record<string, unknown>).config as Record<string, unknown> ?? {};
      return { ...n, data: { ...n.data, config: { ...config, ...configPatch } } };
    }));
  }, [setNodes, commitHistory, markDirty]);

  // ---- Fullscreen / Distraction-free (F11) -------------------------------
  const [fullscreen, setFullscreen] = useState(false);

  // AI workflow assistant, docked in the right panel.
  const aiChatOpen = useKnowledgeChatSessionStore((s) => s.workflowChatOpen);
  const setAiChatOpen = useKnowledgeChatSessionStore((s) => s.setWorkflowChatOpen);
  useEffect(() => () => setAiChatOpen(false), [setAiChatOpen]);
  const getCurrentDefinition = useCallback(
    () => stripRuntimeDefinition({ nodes, edges }),
    [nodes, edges],
  );
  const applyAiDefinition = useCallback((def: { nodes: Node[]; edges: Edge[] }) => {
    // Same order as any programmatic graph mutation: history and dirty before setNodes, so undo
    // snapshots the previous state. Group nodes come before their children, as on initial load.
    commitHistory('AI assistant change');
    markDirty();
    setNodes([...def.nodes].sort((a) => (a.type === 'group' ? -1 : 1)));
    setEdges(def.edges);
  }, [commitHistory, markDirty, setNodes, setEdges]);
  // The right panel is shared space: the AI chat covers the node and edge properties. Selecting
  // anything on the canvas brings the properties back and closes the chat. Clicking an already
  // selected node does not change `selected`, so that case is handled in `onNodeClick`.
  useEffect(() => {
    if (selected) setAiChatOpen(false);
  }, [selected, setAiChatOpen]);
  // Current canvas labels scope AI chat to the selection. Only scanned while the panel is
  // open — the node and edge arrays are replaced on every frame of a drag.
  const aiSelection = useMemo(() => (aiChatOpen ? {
    nodeLabels: nodes.filter((n) => n.selected).map((n) => (n.data?.label as string) || n.id),
    edgeCount: edges.filter((e) => e.selected).length,
  } : EMPTY_AI_SELECTION), [aiChatOpen, nodes, edges]);
  const toggleFullscreen = useCallback(() => setFullscreen((f) => !f), []);

  // ---- Activity-Type-Filter (transient — not persisted, resets on workflow switch) ----
  const [hiddenActivityTypes, setHiddenActivityTypes] = useState<Set<string>>(new Set());

  // ---- Failure heatmap (toggled in the overlays menu, applied per-node via __failureTint) ----
  const failureHeatmapEnabled = useDesignStore((s) => s.failureHeatmapEnabled);

  // ---- Search & Jump (Ctrl+F) --------------------------------------------
  const { setSearchOpen, setSearchInput } = overlays;
  const jumpToNode = useCallback((n: Node) => {
    const position = absolutePosition(n, new Map(nodes.map(node => [node.id, node])));
    const x = position.x + ((n.measured?.width ?? 140) / 2);
    const y = position.y + ((n.measured?.height ?? 100) / 2);
    setCenter(x, y, { zoom: 1.1, duration: 400 });
    setSelected({ type: 'node', id: n.id });
    setSearchOpen(false);
    setSearchInput('');
  }, [nodes, setCenter, setSearchOpen, setSearchInput]);

  useEffect(() => {
    if (!focusNode || !workflow || nodes.length === 0 || seededWorkflowIdRef.current !== workflow.id) return;
    const key = `${workflow.id}:${focusNode}`;
    if (focusedDependencyRef.current === key) return;
    const node = nodes.find(n => n.id === focusNode);
    if (!node) { focusedDependencyRef.current = key; toast.info(t('editor:dependencyStepMissing')); return; }
    // Focus after the initial canvas fit; selection never requests an edit lock.
    const timer = setTimeout(() => {
      focusedDependencyRef.current = key;
      jumpToNode(node);
      setNodes(current => current.map(n => ({ ...n, selected: n.id === focusNode })));
    }, 100);
    return () => clearTimeout(timer);
  }, [focusNode, workflow, nodes, jumpToNode, setNodes, t]);

  const jumpToEdge = useCallback((e: Edge) => {
    const src = nodes.find((n) => n.id === e.source);
    const tgt = nodes.find((n) => n.id === e.target);
    if (!src || !tgt) return;
    const byId = new Map(nodes.map(node => [node.id, node]));
    const sourcePosition = absolutePosition(src, byId);
    const targetPosition = absolutePosition(tgt, byId);
    const x = ((sourcePosition.x + (src.measured?.width ?? 140) / 2) + (targetPosition.x + (tgt.measured?.width ?? 140) / 2)) / 2;
    const y = ((sourcePosition.y + (src.measured?.height ?? 100) / 2) + (targetPosition.y + (tgt.measured?.height ?? 100) / 2)) / 2;
    setCenter(x, y, { zoom: 1.1, duration: 400 });
    setSelected({ type: 'edge', id: e.id });
  }, [nodes, setCenter]);

  // ---- Auto-layout (Tidy) -------------------------------------------------
  const layout = useTidyLayout({
    workflowId: id, graph: graphSource, isTidying, setTidyingWorkflowId, persistence, commitHistory,
  });

  // ---- Select All (Ctrl+A) ------------------------------------------------
  const selectAll = useCallback(() => {
    setNodes((nds: Node[]) => nds.map((n) => ({ ...n, selected: true })));
    setEdges((eds: Edge[]) => eds.map((e) => ({ ...e, selected: true })));
  }, [setNodes, setEdges]);

  // ---- Zoom to Selection (Ctrl+Shift+E) -----------------------------------
  const zoomToSelection = useCallback(() => {
    const sel = nodes.filter((n) => n.selected);
    if (sel.length === 0) return;
    fitView({ nodes: sel, padding: 0.2, duration: 300 });
  }, [nodes, fitView]);

  // ---- Node keyboard navigation (Tab / Shift+Tab) -------------------------
  const navigateNode = useCallback((dir: 'next' | 'prev') => {
    const src = selected?.type === 'node' ? nodes.find((n) => n.id === selected.id) ?? null : null;
    if (!src) return;
    const candidateEdges = dir === 'next'
      ? edges.filter((e) => e.source === src.id && !(e.data as Record<string, unknown>)?.disabled)
      : edges.filter((e) => e.target === src.id && !(e.data as Record<string, unknown>)?.disabled);
    if (candidateEdges.length === 0) return;
    const targetId = dir === 'next' ? candidateEdges[0].target : candidateEdges[0].source;
    const target = nodes.find((n) => n.id === targetId);
    if (!target) return;
    setNodes((nds: Node[]) => nds.map((n) => ({ ...n, selected: n.id === targetId })));
    setSelected({ type: 'node', id: targetId });
    fitView({ nodes: [target], padding: 0.3, duration: 250 });
  }, [selected, edges, nodes, setNodes, fitView]);

  // ---- Export (JSON, PNG) and Dry-Run / Simulate --------------------------
  const { exportPng, exportJson } = useWorkflowExport({ workflowId: id, name, canvasRef });
  const sim = useWorkflowSimulation(nodes, edges);
  const { simulation, revealIndex, runSimulation, clearSimulation } = sim;

  // ---- Edge detach (release the target) -----------------------------------
  // Set up before both context-menu handlers, which cancel a running detach.
  const edgeDetach = useEdgeDetach({
    graph: graphSource, canWrite, setSelected, commitHistory, markDirty, showConnectionNotice,
  });
  const { consumeContextMenuAsDetachCancel, completeEdgeReattach, cancelEdgeDetach, detachedEdge } = edgeDetach;

  // ---- Node Context Menu --------------------------------------------------
  const [contextMenu, setContextMenu] = useState<{ nodeId: string; x: number; y: number } | null>(null);

  const handleNodeContextMenu = useCallback((e: React.MouseEvent, node: Node) => {
    // Runs before the node-type check so a right-click on a group or sticky note also cancels
    // the detach, even though those node types have no context menu of their own.
    if (consumeContextMenuAsDetachCancel(e)) return;
    if (node.type !== 'activity') return;
    if (!canWrite) return;  // Every entry changes the graph, so viewers get no menu.
    e.preventDefault();
    const rect = canvasRef.current?.getBoundingClientRect();
    if (!rect) return;
    setContextMenu({ nodeId: node.id, x: e.clientX - rect.left, y: e.clientY - rect.top });
  }, [canWrite, consumeContextMenuAsDetachCancel]);

  // ---- Edge Context Menu --------------------------------------------------
  const [edgeContextMenu, setEdgeContextMenu] = useState<{ edgeId: string; x: number; y: number } | null>(null);

  const handleEdgeContextMenu = useCallback((e: React.MouseEvent, edge: Edge) => {
    if (consumeContextMenuAsDetachCancel(e)) return;
    if (!canWrite) return;  // Viewers and read-only mode get no edit-actions menu
    e.preventDefault();
    const rect = canvasRef.current?.getBoundingClientRect();
    if (!rect) return;
    setEdgeContextMenu({ edgeId: edge.id, x: e.clientX - rect.left, y: e.clientY - rect.top });
    setSelected({ type: 'edge', id: edge.id });
  }, [canWrite, consumeContextMenuAsDetachCancel]);

  // ---- Quick-connect (drag a handle onto empty canvas) + edge insert (`+` on edge) ----
  const {
    quickConnect, setQuickConnect, handleConnectEnd, handleQuickConnectPick,
    insertAt, setInsertAt, requestInsert, insertOnEdge,
  } = useCanvasConnect({
    edges, setNodes, setEdges, setSelected,
    commitHistory, markDirty, canvasRef, screenToFlowPosition,
  });

  const onConnectEnd = useCallback((event: MouseEvent | TouchEvent, connectionState: FinalConnectionState) => {
    if (connectionState.toNode && hasDuplicateConnection(connectionState.fromNode?.id, connectionState.toNode.id)) {
      showConnectionNotice(t('editor:connection.duplicate'));
      return;
    }
    handleConnectEnd(event, connectionState);
  }, [handleConnectEnd, hasDuplicateConnection, showConnectionNotice, t]);

  const { addNode, addSnippet, duplicateNode, deleteNodeById, groupSelection } = useNodeOperations({
    nodes, setNodes, edges, setEdges,
    selected, setSelected,
    commitHistory, markDirty,
    canvasRef, screenToFlowPosition,
  });
  const groupIfWritable = useCallback(() => { if (canWrite) groupSelection(); }, [canWrite, groupSelection]);

  const handleNodeDataUpdate = useCallback(
    (nodeId: string, newData: Record<string, unknown>) => {
      const isStructural = 'disabled' in newData || 'breakpoint' in newData || 'outputVariable' in newData || 'breakpointCondition' in newData;
      if (isStructural) commitHistory('Edit property'); else commitHistoryForPropertyEdit();
      markDirty();
      // Merge onto the existing node data instead of replacing it. Callers that pass a full
      // data object are unaffected, but partial-patch callers such as the context menu's
      // disable and breakpoint toggles would otherwise wipe activityType, config and label
      // and produce a definition the backend rejects on save.
      setNodes((nds: Node[]) => {
        const oldNode = nds.find((n) => n.id === nodeId);
        const oldAlias = (oldNode?.data as Record<string, unknown>)?.outputVariable as string | undefined;
        const newAlias = newData.outputVariable as string | undefined;
        if (oldAlias && newAlias && oldAlias !== newAlias) {
          const downstream = getDownstreamNodeIds(nodeId, edges);
          // Rename the alias in edges too: condition shortcuts and conditionExpression
          // variable references. Done on the same change so nodes and edges stay consistent.
          setEdges((eds) => eds.map((e) => renameVariableInEdge(e, oldAlias, newAlias)));
          return nds.map((n) => {
            if (n.id === nodeId) return { ...n, data: { ...(n.data as Record<string, unknown>), ...newData } };
            if (downstream.has(n.id)) return { ...n, data: renameVariableInNodeData(n.data as Record<string, unknown>, oldAlias, newAlias) };
            return n;
          });
        }
        return nds.map((n) => (n.id === nodeId ? { ...n, data: { ...(n.data as Record<string, unknown>), ...newData } } : n));
      });
    }, [setNodes, setEdges, edges, commitHistory, commitHistoryForPropertyEdit, markDirty],
  );

  const handleBulkApply = useCallback(
    (nodeIds: string[], patch: Record<string, unknown>, configPatch?: Record<string, unknown>) => {
      if (!canWrite) return;
      commitHistory('Edit property');
      markDirty();
      const ids = new Set(nodeIds);
      setNodes((nds) => nds.map((n) => {
        if (!ids.has(n.id) || n.type !== 'activity') return n;
        const oldData = n.data as Record<string, unknown>;
        const newData = { ...oldData, ...patch };
        if (configPatch) {
          const oldCfg = (oldData.config as Record<string, unknown>) ?? {};
          newData.config = { ...oldCfg, ...configPatch };
          if (oldData.activityType === 'aiAgent' || oldData.activityType === 'aiAgentTeam')
            delete (newData.config as Record<string, unknown>).retry;
        }
        return { ...n, data: newData };
      }));
    },
    [canWrite, setNodes, commitHistory, markDirty],
  );

  const handleEdgeUpdate = useCallback(
    (edgeId: string, patch: Partial<Edge>) => {
      commitHistory('Edit edge');
      markDirty();
      setEdges((eds: Edge[]) => eds.map((e) => (e.id === edgeId ? { ...e, ...patch } : e)));
    }, [setEdges, commitHistory, markDirty],
  );

  const handleEdgeDelete = useCallback(
    (edgeId: string) => { commitHistory('Delete edge'); markDirty(); setEdges((eds: Edge[]) => eds.filter((e) => e.id !== edgeId)); setSelected(null); },
    [setEdges, commitHistory, markDirty],
  );

  // Reshape actions for cubic-Bezier edge handles. History and dirty state are handled here,
  // committing before the mutation so the snapshot captures the pre-shape state. Programmatic
  // setEdges bypasses onEdgesChangeDirty, so the dirty flag is set explicitly.
  const beginEdgeReshape = useCallback((_edgeId: string) => {
    commitHistory('Reshape edge');
    markDirty();
  }, [commitHistory, markDirty]);

  const updateEdgeShape = useCallback((edgeId: string, controlPoints: { cp1x: number; cp1y: number; cp2x: number; cp2y: number }) => {
    setEdges((eds: Edge[]) => eds.map((e) =>
      e.id === edgeId
        ? { ...e, data: { ...((e.data as Record<string, unknown>) ?? {}), controlPoints } }
        : e
    ));
  }, [setEdges]);

  const resetEdgeShape = useCallback((edgeId: string) => {
    commitHistory('Reset edge shape');
    markDirty();
    setEdges((eds: Edge[]) => eds.map((e) => {
      if (e.id !== edgeId) return e;
      const { controlPoints: _omit, ...restData } = ((e.data as Record<string, unknown>) ?? {});
      void _omit;
      return { ...e, data: restData };
    }));
  }, [setEdges, commitHistory, markDirty]);

  // A click on an already selected node does not fire onSelectionChange, so close chat here.
  // Multi-select keeps chat open, and edge detachment consumes its target click first.
  const onNodeClick = useCallback((e: React.MouseEvent, node: Node) => {
    if (e.shiftKey || e.ctrlKey || e.metaKey) return;
    if (completeEdgeReattach(e, node)) return;
    setAiChatOpen(false);
  }, [completeEdgeReattach, setAiChatOpen]);

  // Mark dirty on any graph or name change (after initial load).
  const onNodesChangeDirty = useCallback((changes: Parameters<typeof onNodesChange>[0]) => {
    onNodesChange(changes);
    if (changes.some((c) => c.type !== 'select' && c.type !== 'dimensions')) markDirty();
  }, [onNodesChange, markDirty]);
  const onEdgesChangeDirty = useCallback((changes: Parameters<typeof onEdgesChange>[0]) => {
    onEdgesChange(changes);
    if (changes.some((c) => c.type !== 'select')) markDirty();
  }, [onEdgesChange, markDirty]);

  // Revalidate every graph change for the header badge and lint panel without blocking saves.
  // Runs on the settled graph, not the live one: lintWorkflow walks every edge against every
  // node, which is far too much work to repeat on each frame of a drag. The badge and the
  // panel trail the canvas by one settle interval, which is what they are worth. A graph with
  // nodes but no enabled trigger yields the `no-trigger` error, which also blocks publish.
  const lintResult = useMemo(
    () => lintWorkflow(settledGraph.nodes, settledGraph.edges, allWorkflows),
    [settledGraph, allWorkflows],
  );
  // Handed to the AI chat at send time, so it sees what the panel shows for the canvas as it is
  // now rather than one settle interval ago.
  const getAiDiagnostics = useCallback(() => {
    const live = liveGraphRef.current;
    const { errors, warnings } = lintWorkflow(live.nodes, live.edges, allWorkflows);
    return [...errors, ...warnings];
  }, [allWorkflows]);

  const {
    run, confirmRunWithParams, closeRunDialog, showRunDialog, lastExecutionList, cancel: cancelExecution,
  } = useWorkflowExecution({
    workflowId: id, workflow, canWrite, isDirty, nodes, edges,
    saveAsync, pinCanvasExecution, clearReplay,
  });

  // ---- Lifecycle: lock, save, publish (state object + gated shortcut triggers) ----
  const { lifecycle, confirmPrePublish, triggerSave, triggerLock, triggerUnlock, triggerForceUnlock } = useEditorLifecycle({
    workflow, roleCanWrite, isAdmin, canWrite, isTidying, lockState, persistence,
    getLiveGraph: () => liveGraphRef.current,
    allWorkflows,
    openPrePublish: overlays.setPrePublishLint,
    closePrePublish: () => overlays.setPrePublishLint(null),
  });

  // Run shortcuts self-gate against the current state, like the lifecycle triggers above.
  const isRunning = liveExecution?.status === 'Running';
  const triggerTest = () => { if (roleCanWrite && !isRunning) run(false); };
  const triggerDebug = () => { if (roleCanWrite && !isRunning) run(true); };
  const cancelRun = () => {
    if (isRunning && liveExecution?.executionId) cancelExecution(liveExecution.executionId);
  };
  const triggerTidy = () => { if (canWrite && !isTidying) layout.tidyLayout(); };
  const triggerSimulation = () => { if (simulation) clearSimulation(); else runSimulation(); };

  // Per-node Quick-Toggle bindings. We toggle EVERY currently-selected Activity node — useful
  // when triaging a debugging session ("disable these 5 steps temporarily"). When a mixed group
  // (some on, some off) is selected, the whole group flips to the *opposite* of the first
  // selected node — VS-Code-style "make them all match the inverse of the lead". No-op when
  // the user can't write or when no Activity nodes are selected.
  const toggleSelectedFlag = useCallback((flag: 'disabled' | 'breakpoint') => {
    if (!canWrite) return;
    const selectedActivityNodes = nodes.filter((n) => n.selected && n.type === 'activity');
    if (selectedActivityNodes.length === 0) return;
    const leadValue = !!(selectedActivityNodes[0].data as Record<string, unknown>)[flag];
    const next = !leadValue;
    commitHistory('Toggle');
    markDirty();
    const ids = new Set(selectedActivityNodes.map((n) => n.id));
    setNodes((nds: Node[]) => nds.map((n) => (
      ids.has(n.id) ? { ...n, data: { ...n.data, [flag]: next } } : n
    )));
  }, [canWrite, nodes, setNodes, commitHistory, markDirty]);

  // Arrow-key nudge: moves all selected nodes by (dx,dy). Shift = 1px fine, normal = 10px step.
  const nudgeSelectedNodes = useCallback((dx: number, dy: number) => {
    if (!canWrite) return;
    const selected = nodes.filter((n) => n.selected);
    if (selected.length === 0) return;
    commitHistory('Nudge');
    markDirty();
    const ids = new Set(selected.map((n) => n.id));
    setNodes((nds: Node[]) => nds.map((n) =>
      ids.has(n.id)
        ? { ...n, position: { x: n.position.x + dx, y: n.position.y + dy } }
        : n
    ));
  }, [canWrite, nodes, setNodes, commitHistory, markDirty]);

  // Home key: fit all nodes into view.
  const fitViewAll = useCallback(() => {
    fitView({ padding: 0.15, duration: 300 });
  }, [fitView]);

  useEditorKeyboardShortcuts({
    designerMode,
    undo, redo, copySelection: copyForClipboard, pasteText: pasteFromText,
    duplicateSelection: duplicateIfWritable, groupSelection: groupIfWritable,
    selectAll, zoomToSelection, navigateNode,
    searchOpen: overlays.searchOpen, setSearchOpen, setSearchInput,
    helpOpen: overlays.helpOpen, setHelpOpen: overlays.setHelpOpen,
    findReplaceOpen: overlays.findReplaceOpen, setFindReplaceOpen: overlays.setFindReplaceOpen,
    edgeDetachActive: !!edgeDetach.detachedEdgeId, cancelEdgeDetach,
    toggleFullscreen,
    toggleQuickSwitcher: overlays.toggleQuickSwitcher,
    toggleCommandPalette: overlays.toggleCommandPalette,
    triggerSave, triggerLock, triggerUnlock, triggerForceUnlock,
    triggerPublish: lifecycle.requestPublish, triggerTest, triggerDebug, triggerCancel: cancelRun,
    triggerTidy, toggleLintPanel: overlays.toggleLintPanel,
    // Layout
    restoreOrigLayout: layout.restoreOrigLayout,
    setDiffOpen: overlays.setDiffOpen,
    triggerSimulation,
    clearActivityTypeFilter: () => setHiddenActivityTypes(new Set()),
    // Style/view toggles — read fresh state from the design store on every key press so
    // stacking them (e.g. spam 'R' to cycle through routing modes) works correctly without
    // stale closures. The design store is a Zustand singleton; getState() is cheap.
    toggleEdgesAnimated: () => useDesignStore.getState().toggleEdgesAnimated(),
    cycleEdgeRouting: () => {
      const order = ['smart', 'curved', 'straight'] as const;
      const cur = useDesignStore.getState().edgeRouting;
      const next = order[(order.indexOf(cur as typeof order[number]) + 1) % order.length];
      useDesignStore.getState().setEdgeRouting(next);
    },
    edgeWidthInc: () => useDesignStore.getState().edgeWidthInc(),
    edgeWidthDec: () => useDesignStore.getState().edgeWidthDec(),
    toggleNodeStyle: () => useDesignStore.getState().toggleNodeStyle(),
    nodeSizeInc: () => useDesignStore.getState().zoomIn(),
    nodeSizeDec: () => useDesignStore.getState().zoomOut(),
    labelFontInc: () => useDesignStore.getState().labelFontInc(),
    labelFontDec: () => useDesignStore.getState().labelFontDec(),
    toggleMachineColoring: () => useDesignStore.getState().toggleMachineColoring(),
    toggleFailureHeatmap: () => useDesignStore.getState().toggleFailureHeatmap(),
    toggleCriticalPath: () => useDesignStore.getState().toggleCriticalPath(),
    toggleSnapToGrid: () => {
      const s = useDesignStore.getState();
      s.setSnapToGrid(!s.snapToGrid);
    },
    toggleSelectedDisabled: () => toggleSelectedFlag('disabled'),
    toggleSelectedBreakpoint: () => toggleSelectedFlag('breakpoint'),
    nudgeSelectedNodes, fitViewAll,
    // Export
    exportJson,
    exportPng,
    // Navigate
    navigate,
  });

  const nodeTypes: NodeTypes = useMemo(() => ({ activity: ActivityNode, stickyNote: StickyNoteNode, group: GroupNode }), []);
  const edgeTypes: EdgeTypes = useMemo(() => ({ labeled: LabeledEdge }), []);
  const edgesAnimated = useDesignStore((s) => s.edgesAnimated);
  const snapToGrid = useDesignStore((s) => s.snapToGrid);
  const snapGridSize = useDesignStore((s) => s.snapGridSize);
  const dataFlowOverlayEnabled = useDesignStore((s) => s.dataFlowOverlayEnabled);
  const premiumCanvas = useDesignStore((s) => s.premiumCanvas);

  const defaultEdgeOptions = useMemo(() => ({
    type: 'labeled',
    // Animation is handled by the np-edge-flow overlay path in LabeledEdge (CSS keyframe),
    // not via React Flow's built-in edge.animated — that would add its own CSS dash
    // animation and conflict with our overlay.
    animated: false,
    markerEnd: { type: MarkerType.ArrowClosed, color: 'var(--np-edge-idle, var(--color-outline-variant))' },
  }), []);
  const selectedNode = selected?.type === 'node' ? nodes.find((n) => n.id === selected.id) ?? null : null;
  const selectedEdge = selected?.type === 'edge' ? edges.find((e) => e.id === selected.id) ?? null : null;
  const isStartWorkflowSelected = !!selectedNode
    && (selectedNode.data as Record<string, unknown>).activityType === 'startWorkflow';

  const { displayedNodes, displayedEdges } = useDisplayedGraph({
    nodes, edges, edgesAnimated, hiddenActivityTypes, dataFlowOverlayEnabled,
    simulation, revealIndex, lintResult, failureHeatmapEnabled, criticalPath,
    annotations: nodeAnnotations, coverage, varFlowEdgeIds,
    detachedEdgeId: edgeDetach.detachedEdgeId,
    dockTargetNodeId: edgeDetach.dockTargetNodeId,
  });

  const detachedEdgeLabel = ((detachedEdge?.data as Record<string, unknown> | undefined)?.label as string) || '';

  const { setPreviewSubWorkflowRef } = overlays;
  const subWorkflowPreviewContextValue = useMemo(
    () => ({ onPreviewSubWorkflow: setPreviewSubWorkflowRef }),
    [setPreviewSubWorkflowRef],
  );

  const edgeInsertContextValue = useMemo(
    () => ({
      onInsertRequest: requestInsert,
      canWrite,
      beginEdgeReshape,
      updateEdgeShape,
      resetEdgeShape,
    }),
    [requestInsert, canWrite, beginEdgeReshape, updateEdgeShape, resetEdgeShape],
  );

  const updateGroupNode = useCallback((nodeId: string, updater: (node: Node) => Node) => {
    commitHistory('Edit group');
    markDirty();
    setNodes((nds: Node[]) => nds.map((n) => n.id === nodeId ? updater(n) : n));
  }, [commitHistory, setNodes, markDirty]);
  const groupNodeEditContextValue = useMemo(
    () => ({ updateGroupNode }),
    [updateGroupNode],
  );

  // Resolved here rather than inside an IIFE in the JSX below. The `(() => { ... })()` form
  // executes during render, so the React Compiler treated the context-menu callbacks created
  // inside it as render-phase code and rejected their (indirect) ref access
  // (react-hooks/refs). Plain conditional rendering keeps the callbacks out of that path.
  const contextMenuNode = contextMenu
    ? nodes.find((n) => n.id === contextMenu.nodeId)
    : undefined;
  const contextMenuData = (contextMenuNode?.data ?? {}) as Record<string, unknown>;

  // Opens another workflow and passes this one along, so the header's back button can name it.
  const openWorkflow = (workflowId: string) => {
    const state = id && workflow ? { fromWorkflow: { id, name: workflow.name } } : undefined;
    navigate(`/workflows/${workflowId}`, { state });
  };

  return (
    <AgentCanvasExecutionContext.Provider value={{ executionId: effectiveCanvasExecution?.executionId ?? replayExecutionId,
      active: effectiveCanvasExecution?.status === 'Running', scrubTimeMs }}>
    <SubWorkflowPreviewContext.Provider value={subWorkflowPreviewContextValue}>
    <div className="np-designer wd-atelier h-screen flex flex-col bg-surface overflow-hidden">
      {/* The header stays visible in fullscreen (F11): only the side panels, the bottom panel and
          the banners are hidden, so the toolbar (save, run, edit, publish) stays reachable. */}
      <EditorHeader
        workflowId={id} workflow={workflow}
        name={name} onRename={rename} nodes={nodes}
        lifecycle={lifecycle}
        history={{ undo, redo, historyPast, historyFuture }}
        layout={layout}
        overlays={overlays}
        simulation={sim}
        run={{ liveExecution, run, cancelRun }}
        lintResult={lintResult}
        hiddenActivityTypes={hiddenActivityTypes} setHiddenActivityTypes={setHiddenActivityTypes}
        zoomToSelection={zoomToSelection} exportPng={exportPng}
        aiChatOpen={aiChatOpen} onToggleAiChat={() => setAiChatOpen(!aiChatOpen)}
      />

      {!fullscreen && <MaintenanceWindowBadge workflowId={id} />}

      <EditorStatusBanners
        replayExecutionId={replayExecutionId}
        replaySteps={replaySteps}
        clearReplay={clearReplay}
        designerCanvasRunIsTerminal={designerCanvasRunIsTerminal}
        designerCanvasRunShortId={designerCanvasRunShortId}
        clearDesignerCanvasHighlight={clearDesignerCanvasHighlight}
        fullscreen={fullscreen}
        roleCanWrite={roleCanWrite}
        isLockedByMe={lifecycle.isLockedByMe}
        isLockedByOther={lifecycle.isLockedByOther}
        isAdmin={isAdmin}
        workflow={workflow}
        onForceUnlock={lifecycle.forceUnlock} isForceUnlocking={lifecycle.isForceUnlocking}
        nodes={nodes}
      />

      {/* Main workspace */}
      <main className="flex flex-1 overflow-hidden relative">
        {/* Fullscreen exit pill — only visible in distraction-free mode */}
        {fullscreen && (
          <button
            type="button"
            onClick={toggleFullscreen}
            className="absolute top-3 right-3 z-30 bg-surface-lowest/90 backdrop-blur border border-outline-variant/30 rounded-full px-3 py-1 text-xs font-label font-semibold text-on-surface-variant hover:bg-surface-high shadow-md transition-colors flex items-center gap-1.5"
            title={t('editor:exitFullscreen')}
          >
            <Minimize size={12} /> {t('editor:exitFullscreen')}
          </button>
        )}
        <EditorSidebar
          fullscreen={fullscreen}
          workflowId={id}
          canWrite={canWrite}
          leftTab={leftTab}
          setLeftTab={setLeftTab}
          leftCollapsed={leftCollapsed}
          setLeftCollapsed={setLeftCollapsed}
          panelSize={leftPanel.size}
          panelHandleProps={leftPanel.handleProps}
          addNode={addNode}
          addSnippet={addSnippet}
          isStartWorkflowSelected={isStartWorkflowSelected}
          onOpenWorkflow={(w) => openWorkflow(w.id)}
          onEmbedWorkflow={(w) => {
            if (selected?.type !== 'node') return;
            const node = nodes.find((n) => n.id === selected.id);
            if (!node) return;
            const data = node.data as Record<string, unknown>;
            const cfg = (data.config as Record<string, unknown>) ?? {};
            handleNodeDataUpdate(node.id, { ...data, config: { ...cfg, workflowNameOrId: w.id } });
          }}
        />

        {/* Center: Canvas */}
        <section
          ref={canvasRef}
          className={`np-canvas flex-1 relative bg-surface overflow-hidden${premiumCanvas ? ' np-premium' : ''}${detachedEdge ? ' np-canvas--detaching' : ''}`}
          onPointerMove={handleCanvasPointerMove}
          onPointerLeave={handleCanvasPointerLeave}
        >
          {premiumCanvas && <NpEdgeMarkerDefs />}
          <EdgeInsertContext.Provider value={edgeInsertContextValue}>
          <GroupNodeEditContext.Provider value={groupNodeEditContextValue}>
          <GroupDropTargetContext.Provider value={dropTargetGroupId}>
          <ReactFlow
            nodes={displayedNodes}
            edges={displayedEdges}
            nodeTypes={nodeTypes}
            edgeTypes={edgeTypes}
            defaultEdgeOptions={defaultEdgeOptions}
            nodesDraggable={canWrite}
            nodesConnectable={canWrite}
            edgesReconnectable={canWrite}
            onReconnect={onReconnect}
            onNodesChange={onNodesChangeDirty}
            onEdgesChange={onEdgesChangeDirty}
            onConnect={onConnect}
            onConnectEnd={onConnectEnd}
            connectionMode={ConnectionMode.Loose}
            connectionRadius={connectionRadiusForZoom(zoomStep)}
            isValidConnection={isValidConnection}
            onNodeContextMenu={handleNodeContextMenu}
            onEdgeContextMenu={handleEdgeContextMenu}
            // Consume the pane menu only to cancel an active detach.
            onPaneContextMenu={consumeContextMenuAsDetachCancel}
            onNodeDoubleClick={canWrite ? onNodeDoubleClick : undefined}
            onDragOver={(e) => {
              if (!canWrite) return;
              e.preventDefault();
              e.dataTransfer.dropEffect = 'copy';
            }}
            onDrop={(e) => {
              if (!canWrite) return;
              e.preventDefault();
              const raw = e.dataTransfer.getData('application/nodepilot-activity');
              if (!raw) return;
              const { type, label } = JSON.parse(raw) as { type: string; label: string };
              const position = screenToFlowPosition({ x: e.clientX, y: e.clientY });
              const isNote = type === 'note';
              // Smart Defaults — same rules as addNode (machine/cred for remote, base URL
              // for restApi, etc.). Drop-and-keep-typing is the most common path, so this
              // is where the win actually shows up most often.
              const smart = isNote ? {} : getSmartDefaults(type, nodes);
              const newNode: Node = isNote
                ? { id: `note-${randomUuid()}`, type: 'stickyNote', position, data: { label: 'Note', activityType: 'note', text: 'Double-click to edit…', disabled: true } }
                : {
                    id: `step-${randomUuid()}`,
                    type: 'activity',
                    position,
                    data: {
                      label,
                      activityType: type,
                      targetMachineId: smart.targetMachineId ?? null,
                      credentialId: smart.credentialId ?? null,
                      config: newActivityConfig(type, smart.config),
                    },
                  };
              commitHistory('Add node');
              markDirty();
              setNodes((nds: Node[]) => [...nds, newNode]);
              setSelected({ type: 'node', id: newNode.id });
            }}
            onSelectionChange={onSelectionChange}
            onNodeClick={onNodeClick}
            onPaneClick={() => { cancelEdgeDetach(); setSelected(null); }}
            onEdgeClick={(_event, edge) => setSelected({ type: 'edge', id: edge.id })}
            onNodeDragStart={() => commitHistory('Move nodes')}
            onNodeDrag={canWrite ? onNodeDrag : undefined}
            onNodeDragStop={canWrite ? onNodeDragStop : undefined}
            onBeforeDelete={async ({ nodes: toDelete, edges: toDeleteEdges }) => {
              const groupIds = new Set(toDelete.filter((n) => n.type === 'group').map((n) => n.id));
              if (groupIds.size > 0) {
                // Ungroup children and restore absolute positions before React Flow deletes groups.
                const groupPos = new Map(nodes.filter((n) => groupIds.has(n.id)).map((n) => [n.id, n.position]));
                setNodes((nds: Node[]) => nds.map((n) => {
                  if (!n.parentId || !groupIds.has(n.parentId)) return n;
                  const gp = groupPos.get(n.parentId) ?? { x: 0, y: 0 };
                  const { parentId: _p, ...rest } = n;
                  void _p;
                  return { ...rest, position: { x: n.position.x + gp.x, y: n.position.y + gp.y } };
                }));
                // Delete only groups; remove child edges from React Flow's automatic deletion set.
                const childIds = new Set(nodes.filter((n) => n.parentId && groupIds.has(n.parentId)).map((n) => n.id));
                return {
                  nodes: toDelete.filter((n) => !childIds.has(n.id)),
                  edges: toDeleteEdges.filter((e) => !childIds.has(e.source) && !childIds.has(e.target)),
                };
              }
              return { nodes: toDelete, edges: toDeleteEdges };
            }}
            onNodesDelete={(deleted) => {
              commitHistory('Delete');
              const ids = new Set(deleted.map((n) => n.id));
              setEdges((eds: Edge[]) => eds.filter((e) => !ids.has(e.source) && !ids.has(e.target)));
              if (selected?.type === 'node' && ids.has(selected.id)) setSelected(null);
            }}
            onEdgesDelete={(deleted) => {
              commitHistory('Delete edge');
              const ids = new Set(deleted.map((e) => e.id));
              if (selected?.type === 'edge' && ids.has(selected.id)) setSelected(null);
            }}
            deleteKeyCode={canWrite ? ['Delete', 'Backspace'] : null}
            minZoom={0.15}
            connectOnClick={false}
            elevateEdgesOnSelect
            // Mount only visible nodes to keep pan and zoom responsive on large graphs.
            onlyRenderVisibleElements
            // Left-drag selects every node touched by the marquee. Middle and right drag pan.
            selectionOnDrag
            panOnDrag={[1, 2]}
            selectionMode={SelectionMode.Partial}
            multiSelectionKeyCode={['Shift', 'Control', 'Meta']}
            snapToGrid={snapToGrid}
            snapGrid={[snapGridSize, snapGridSize]}
            connectionLineComponent={NpConnectionLine}
          >
            {/* Controls chrome comes entirely from the token-fed `.react-flow__controls` block
                in index.css (--xy-controls-*). */}
            <Controls />
            {detachedEdge && (
              <EdgeDetachPreview
                sourceNodeId={detachedEdge.source}
                sourcePort={edgeSourcePort(detachedEdge)}
                targetPort={edgeTargetPort(detachedEdge)}
                canvasRef={canvasRef}
                canDockTo={edgeDetach.canDockTo}
                onDockTargetChange={edgeDetach.setDockTargetNodeId}
              />
            )}
            {snapToGrid ? (
              // Snap-to-grid mode: single Lines background aligned to the grid pitch.
              <Background
                id="np-bg-snap"
                variant={BackgroundVariant.Lines}
                gap={snapGridSize}
                size={0.5}
                color="var(--color-outline-variant)"
                className="opacity-25"
              />
            ) : (
              // Free mode uses the same token-based dot grid in Premium and Classic themes.
              <Background
                id="np-bg-dots"
                variant={BackgroundVariant.Dots}
                gap={24}
                size={1.9}
                color="var(--np-canvas-dot)"
              />
            )}
            {machineColoringEnabled && legendMachines.length > 0 && (
              <Panel position="bottom-left" style={{ marginBottom: '8px', marginLeft: '8px' }}>
                <div className="bg-surface-lowest/90 backdrop-blur rounded-lg border border-outline-variant/20 shadow-md px-3 py-2 flex flex-col gap-1">
                  <span className="font-label text-[10px] font-bold uppercase tracking-wide text-on-surface-variant">{t('editor:machinesShort')}</span>
                  {legendMachines.map(({ id, name, colorIdx }) => (
                    <div key={id} className="flex items-center gap-2">
                      <div className="w-3 h-3 rounded-sm shrink-0" style={{ backgroundColor: MACHINE_COLORS[colorIdx % MACHINE_COLORS.length].stripe }} />
                      <span className="font-label text-xs text-on-surface truncate max-w-[160px]">{name}</span>
                    </div>
                  ))}
                </div>
              </Panel>
            )}
            <MiniMap
              className="!bg-surface-lowest/90 !backdrop-blur-md !border !border-outline-variant/40 !rounded-xl !shadow-lg"
              pannable
              zoomable
              nodeStrokeWidth={3}
              maskColor="var(--np-minimap-mask)"
              nodeColor={minimapNodeColor}
            />
          </ReactFlow>
          </GroupDropTargetContext.Provider>
          </GroupNodeEditContext.Provider>
          </EdgeInsertContext.Provider>
          {!fullscreen && workflow && (
            <div className="absolute top-2 left-2 z-30 max-w-[60%]">
              <FolderPathBreadcrumb
                workflow={workflow}
                currentWorkflowId={id}
                onOpenWorkflow={(w) => openWorkflow(w.id)}
              />
            </div>
          )}
          {/* Viewers get no create path (same roleCanWrite gate as Save/Publish). */}
          {roleCanWrite && workflow && (
            <NewWorkflowButton folderId={workflow.folderId} fullscreen={fullscreen} />
          )}
          {connectionNotice && (
            <div className="absolute top-4 left-1/2 -translate-x-1/2 z-30 max-w-[520px] px-4 py-2 rounded-md bg-amber-100 text-amber-950 border border-amber-300 shadow-lg text-sm font-label font-medium">
              {connectionNotice}
            </div>
          )}
          {/* Edge detach: stays while the end hangs on the cursor, unlike the notice above, which
              fades. Moves down when both are visible. */}
          {detachedEdge && (
            <div
              data-testid="edge-detach-hint"
              className={`absolute ${connectionNotice ? 'top-16' : 'top-4'} left-1/2 -translate-x-1/2 z-30 max-w-[520px] px-4 py-2 rounded-md bg-surface-lowest text-on-surface border border-primary/50 shadow-lg text-sm font-label font-medium`}
            >
              {detachedEdgeLabel
                ? t('editor:edgeDetach.hint', { edge: detachedEdgeLabel })
                : t('editor:edgeDetach.hintUnlabeled')}
            </div>
          )}
          {insertAt && (
            <EdgeInserter
              x={insertAt.x}
              y={insertAt.y}
              onPick={insertOnEdge}
              onClose={() => setInsertAt(null)}
            />
          )}
          {contextMenu && contextMenuNode && (
            <NodeContextMenu
              x={contextMenu.x}
              y={contextMenu.y}
              isDisabled={!!(contextMenuData.disabled)}
              hasBreakpoint={!!(contextMenuData.breakpoint)}
              onDuplicate={() => duplicateNode(contextMenu.nodeId)}
              onToggleDisabled={() => handleNodeDataUpdate(contextMenu.nodeId, { disabled: !contextMenuData.disabled })}
              onToggleBreakpoint={() => handleNodeDataUpdate(contextMenu.nodeId, { breakpoint: !contextMenuData.breakpoint })}
              onDelete={() => deleteNodeById(contextMenu.nodeId)}
              onClose={() => setContextMenu(null)}
            />
          )}
          {edgeContextMenu && (() => {
            const edge = edges.find((e) => e.id === edgeContextMenu.edgeId);
            if (!edge) return null;
            const ed = (edge.data as Record<string, unknown>) ?? {};
            return (
              <EdgeContextMenu
                x={edgeContextMenu.x}
                y={edgeContextMenu.y}
                isDisabled={!!ed.disabled}
                hasCustomShape={ed.controlPoints != null}
                onToggleDisabled={() => handleEdgeUpdate(edgeContextMenu.edgeId, {
                  data: { ...ed, disabled: !ed.disabled },
                })}
                onDetachTarget={() => edgeDetach.beginEdgeDetach(edgeContextMenu.edgeId)}
                onSwapSourceTarget={() => handleEdgeUpdate(edgeContextMenu.edgeId, {
                  source: edge.target,
                  target: edge.source,
                  sourceHandle: normalizePort(edge.targetHandle, DEFAULT_SOURCE_PORT),
                  targetHandle: normalizePort(edge.sourceHandle, DEFAULT_TARGET_PORT),
                })}
                onResetShape={() => resetEdgeShape(edgeContextMenu.edgeId)}
                onDelete={() => handleEdgeDelete(edgeContextMenu.edgeId)}
                onClose={() => setEdgeContextMenu(null)}
              />
            );
          })()}
          {quickConnect && (
            <QuickConnectPicker
              x={quickConnect.screenX}
              y={quickConnect.screenY}
              onPick={handleQuickConnectPick}
              onClose={() => setQuickConnect(null)}
            />
          )}
          {simulation && (
            <SimulationBanner simulation={simulation} revealIndex={revealIndex} onClear={clearSimulation} />
          )}
        </section>

        {aiChatOpen && !fullscreen ? (
          <>
            <ResizeHandle direction="horizontal" {...rightPanel.handleProps} />
            <div
              style={{ width: rightPanel.size }}
              className="wd-side-card shrink-0 border-l border-outline-variant/20"
            >
              <AiWorkflowChatPanel
                workflowId={id}
                getCurrentDefinition={getCurrentDefinition}
                applyDefinition={applyAiDefinition}
                canApply={canWrite}
                isViewer={isViewer}
                onClose={() => setAiChatOpen(false)}
                onUndo={undo}
                onAutoLayout={layout.tidyLayout}
                selection={aiSelection}
                getDiagnostics={getAiDiagnostics}
              />
            </div>
          </>
        ) : (
          <EditorRightPanel
            fullscreen={fullscreen}
            nodes={nodes}
            edges={edges}
            selectedNode={selectedNode}
            selectedEdge={selectedEdge}
            machines={machines}
            credentials={credentials}
            workflowId={id}
            canWrite={canWrite}
            panelSize={rightPanel.size}
            panelHandleProps={rightPanel.handleProps}
            setNodes={setNodes}
            setSelected={setSelected}
            setLeftTab={setLeftTab}
            setLeftCollapsed={setLeftCollapsed}
            handleBulkApply={handleBulkApply}
            handleNodeDataUpdate={handleNodeDataUpdate}
            handleEdgeUpdate={handleEdgeUpdate}
            handleEdgeDelete={handleEdgeDelete}
            onVarHover={handleVarHover}
          />
        )}
      </main>

      {/* Execution Panel (bottom) — hidden in distraction-free fullscreen */}
      {id && !fullscreen && (
        <>
          <ResizeHandle direction="vertical" {...bottomPanel.handleProps} />
          <ExecutionPanel
            workflowId={id}
            liveExecution={liveExecution}
            liveExecutions={liveExecutions}
            liveActiveCount={liveActiveCount}
            connected={connected}
            height={bottomPanel.size}
            nodes={nodes}
            onJoinExecution={joinExecution}
            onLeaveExecution={leaveExecution}
            onReplay={toggleReplay}
            activeReplayId={replayExecutionId}
            onScrubTime={scrubTo}
            simulation={simulation ? {
              order: simulation.order,
              skipped: [...simulation.skipped],
              nodeLabels: Object.fromEntries(nodes.map((n) => [n.id, ((n.data as Record<string, unknown>)?.label as string) ?? n.id])),
              nodeTypes: Object.fromEntries(nodes.map((n) => [n.id, ((n.data as Record<string, unknown>)?.activityType as string) ?? 'unknown'])),
              revealIndex,
            } : null}
          />
        </>
      )}

      <EditorOverlays
        workflowId={id}
        name={name}
        workflow={workflow}
        graph={{ ...graphSource, commitHistory, markDirty }}
        overlays={overlays}
        lifecycle={lifecycle}
        execution={{ showRunDialog, confirmRunWithParams, closeRunDialog, lastExecutionList, run, cancelRun, isRunning }}
        actions={{
          undo, redo, copySelection: copyToClipboard, pasteBuffer: pasteFromClipboard, duplicateSelection,
          groupSelection, selected, deleteNodeById, selectAll, navigateNode, handleQuickEditSave, jumpToNode, jumpToEdge, toggleFullscreen, zoomToSelection,
          fitViewAll, exportPng, exportJson,
        }}
        layout={layout}
        simulation={sim}
        lintResult={lintResult}
        hiddenActivityTypes={hiddenActivityTypes}
        setHiddenActivityTypes={setHiddenActivityTypes}
        onConfirmPrePublish={confirmPrePublish}
        openWorkflow={openWorkflow}
        navigate={navigate}
      />
    </div>
    </SubWorkflowPreviewContext.Provider>
    </AgentCanvasExecutionContext.Provider>
  );
}

/** Minimap colour: live status first, then the latest historical outcome, then a type group. */
function minimapNodeColor(n: Node): string {
  const d = n.data as Record<string, unknown>;
  const live = d.__liveStatus as string | undefined;
  const health = d.__health as Array<{ status: string }> | undefined;
  const last = health && health.length > 0 ? health.at(-1) : undefined;
  const type = d.activityType as string | undefined;
  if (live) return STATUS_COLOR_VAR[npStatusFromExecution(live)];
  // Container tints keep the historical outcome calmer than a live status.
  if (last?.status === 'Succeeded') return 'var(--color-success-container)';
  if (last?.status === 'Failed') return 'var(--color-error-container)';
  if (type === 'note') return 'var(--color-warning-container)';
  if (type?.endsWith('Trigger')) return 'var(--color-success-container)';
  if (type === 'junction' || type === 'startWorkflow' || type === 'forEach' || type === 'returnData')
    return 'var(--color-info-container)';
  return 'var(--color-primary-fixed)';
}
