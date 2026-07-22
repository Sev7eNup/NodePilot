import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api, downloadFromApi } from '../api/client';
import type { Workflow, ManagedMachine, Credential } from '../types/api';
import { Add, Chemistry, CircleDash, Close, Minimize } from '@carbon/icons-react';
import { toPng } from 'html-to-image';
import { autoLayout, autoLayoutTB, autoLayoutCompact, autoLayoutELK } from '../lib/autoLayout';
import { useState, useCallback, useEffect, useMemo, useRef } from 'react';
import {
  ReactFlow,
  ReactFlowProvider,
  useReactFlow,
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
import { getPrePublishLint } from '../lib/prePublishChecks';
import { getSmartDefaults } from '../lib/lastSimilarNode';
import { reparentDraggedNodes, findDropTargetGroupId } from '../lib/groupReparenting';
import { useDesignStore, LAYOUT_MODES, MACHINE_COLORS } from '../stores/designStore';
import { usePointerFlowPosition } from '../stores/pointerFlowPositionStore';
import { useThemeStore } from '../stores/themeStore';
import { LabeledEdge, EdgeInsertContext } from '../components/designer/edges/LabeledEdge';
import { NpEdgeMarkerDefs } from '../components/designer/edges/NpEdgeMarkerDefs';
import { NpConnectionLine } from '../components/designer/edges/NpConnectionLine';
import { npStatusFromExecution, STATUS_COLOR_VAR } from '../lib/statusTokens';
import { SubWorkflowPreviewContext } from '../components/designer/nodes/ActivityNode';
import { SubWorkflowPreviewModal } from '../components/designer/overlays/SubWorkflowPreviewModal';
import { EdgeInserter } from '../components/designer/overlays/EdgeInserter';
import { NodeContextMenu } from '../components/designer/overlays/NodeContextMenu';
import { EdgeContextMenu } from '../components/designer/overlays/EdgeContextMenu';
import { QuickConnectPicker } from '../components/designer/overlays/QuickConnectPicker';
import { pushRecentWorkflow } from '../components/designer/overlays/WorkflowQuickSwitcher';
import {
  getDownstreamNodeIds,
  renameVariableInNodeData,
  renameVariableInEdge,
} from '../lib/editorGraphHelpers';
import {
  DEFAULT_SOURCE_PORT,
  DEFAULT_TARGET_PORT,
  normalizePort,
} from '../lib/edgePorts';
import { FolderPathBreadcrumb } from '../components/designer/FolderPathBreadcrumb';
import { ResizeHandle } from '../components/designer/library/NodeLibrary';
import { EditorOverlays } from '../components/designer/EditorOverlays';
import { ExecutionPanel } from '../components/designer/ExecutionPanel';
import { useWorkflowSignalR } from '../hooks/useSignalR';
import { useResizable } from '../hooks/useResizable';
import { toast } from '../stores/toastStore';
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
import { EditorHeader } from '../components/designer/EditorHeader';
import { MaintenanceWindowBadge } from '../components/designer/MaintenanceWindowBadge';
import { EditorStatusBanners } from '../components/designer/EditorStatusBanners';
import { EditorRightPanel } from '../components/designer/EditorRightPanel';
import { AiWorkflowChatPanel } from '../components/ai/AiWorkflowChatPanel';
import { stripRuntimeDefinition } from '../lib/workflowDefinitionSanitizer';
import { EditorSidebar } from '../components/designer/EditorSidebar';
import { useRole } from '../lib/rbac';
import { useAuthStore } from '../stores/authStore';
import { ErrorBoundary } from '../components/ErrorBoundary';




/* ---- Editor Page ---- */

type SelectedItem = { type: 'node'; id: string } | { type: 'edge'; id: string } | null;

export function WorkflowEditorPage() {
  const { t } = useTranslation(['editor']);
  // Local boundary so a SignalR-init crash, a malformed workflow JSON, or a React Flow
  // render-time exception only blanks the editor area — the AppLayout sidebar / header
  // / navigation remain intact, and the user can switch to another page without a full
  // browser reload. The outer App.tsx boundary still catches anything that escapes here.
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
  const { screenToFlowPosition, fitView } = useReactFlow();
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
  // On unmount, cancel any pending frame and clear the shared pointer store so a stale editor cursor
  // can't leak port-reveals into other ReactFlow instances (mobile / sub-workflow preview) that reuse
  // ActivityNode. getState() keeps this effect dependency-free.
  useEffect(() => () => {
    if (pointerRafRef.current != null) cancelAnimationFrame(pointerRafRef.current);
    usePointerFlowPosition.getState().set(null, null);
  }, []);
  const navigate = useNavigate();
  const { canWrite: roleCanWrite, isAdmin, isViewer } = useRole();
  const currentUserId = useAuthStore((s) => s.userId);
  const designerMode = useDesignStore((s) => s.designerMode);
  const designerTheme = useDesignStore((s) => s.designerTheme);
  const isAtelier = designerTheme === 'atelier';
  // Atelier marker on <html>: body-portaled designer UI (activity tooltips via
  // .np-tooltip-portal) escapes the .np-designer token scope — the root marker lets
  // designer-atelier.css re-assert the Atelier surface tokens on those portals too.
  useEffect(() => {
    const rootEl = document.documentElement;
    if (isAtelier) rootEl.classList.add('wd-atelier-on');
    else rootEl.classList.remove('wd-atelier-on');
    return () => rootEl.classList.remove('wd-atelier-on');
  }, [isAtelier]);
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const [selected, setSelected] = useState<SelectedItem>(null);
  const [connectionNotice, setConnectionNotice] = useState('');
  const [searchQuery, setSearchQuery] = useState('');
  const [leftTab, setLeftTab] = useState<'nodes' | 'workflows'>('workflows');
  const [leftCollapsed, setLeftCollapsed] = useState(false);
  // Node-library categories are collapsible/expandable. Persisted to localStorage so the
  // user doesn't have to re-expand their preferred categories next time they open a workflow.
  const [collapsedCategories, setCollapsedCategories] = useState<Record<string, boolean>>(() => {
    if (typeof window === 'undefined') return {};
    try {
      const raw = globalThis.localStorage.getItem('nodepilot.designer.collapsedCategories');
      return raw ? (JSON.parse(raw) as Record<string, boolean>) : { Actions: true };
    } catch { return {}; }
  });
  useEffect(() => {
    try {
      globalThis.localStorage.setItem('nodepilot.designer.collapsedCategories', JSON.stringify(collapsedCategories));
    } catch { /* storage full / disabled — non-fatal */ }
  }, [collapsedCategories]);
  const toggleCategory = useCallback((name: string) => {
    setCollapsedCategories((prev) => ({ ...prev, [name]: !prev[name] }));
  }, []);
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
  // Default panel sizes tuned for 1440-and-up screens: big enough that the common
  // content (workflow names, node-property labels, history rows) fits without needing
  // a first-time drag. Upper bounds relaxed so users on wider screens can push further.
  const leftPanel = useResizable({ initialSize: 350, minSize: 160, maxSize: 500, direction: 'horizontal' });
  const rightPanel = useResizable({ initialSize: 450, minSize: 280, maxSize: 640, direction: 'horizontal', reverse: true });
  const bottomPanel = useResizable({ initialSize: 340, minSize: 100, maxSize: 700, direction: 'vertical', reverse: true });

  const { data: workflow } = useQuery({
    queryKey: ['workflow', id],
    queryFn: () => api.get<Workflow>(`/workflows/${id}`),
    enabled: !!id,
  });

  const {
    isLockedByMe, isLockedByOther, canWrite,
    lock, unlock, forceUnlock, disable, enable,
    isLocking, isUnlocking, isForceUnlocking, isDisabling, isEnabling,
  } = useWorkflowLock({ workflowId: id, workflow, currentUserId, roleCanWrite });
  const {
    name, isDirty, isSaving, isPublishing,
    rename, markDirty, save, saveAsync, publish, syncFromServer,
  } = useWorkflowPersistence({ workflowId: id, workflow, nodes, edges });

  const { data: allWorkflows } = useQuery({
    queryKey: ['workflows'],
    queryFn: () => api.get<Array<{ id: string; name: string }>>('/workflows'),
    staleTime: 60_000,
  });

  // Create-new-workflow from the canvas: mirrors WorkflowsPage.createMutation, except the
  // target folder comes from the currently open workflow (RBAC R3 — otherwise defaulting to
  // Root would 403 for folder-scoped users). onSuccess invalidates the workflow list and
  // navigates into the new empty workflow; the header allows inline renaming there.
  const queryClient = useQueryClient();
  const [newWorkflowOpen, setNewWorkflowOpen] = useState(false);
  const [newWorkflowName, setNewWorkflowName] = useState('');
  const createWorkflowMutation = useMutation({
    mutationFn: (name: string) =>
      api.post<Workflow>('/workflows', {
        name,
        description: '',
        definitionJson: JSON.stringify({ nodes: [], edges: [] }),
        folderId: workflow?.folderId ?? undefined,
      }),
    onSuccess: (created) => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      setNewWorkflowOpen(false);
      setNewWorkflowName('');
      navigate(`/workflows/${created.id}`);
    },
  });
  const submitNewWorkflow = useCallback(() => {
    const trimmed = newWorkflowName.trim();
    if (!trimmed || createWorkflowMutation.isPending) return;
    createWorkflowMutation.mutate(trimmed);
  }, [newWorkflowName, createWorkflowMutation]);

  const { data: machines = [] } = useQuery({ queryKey: ['machines'], queryFn: () => api.get<ManagedMachine[]>('/machines') });
  const { data: credentials = [] } = useQuery({ queryKey: ['credentials'], queryFn: () => api.get<Credential[]>('/credentials') });

  // ---- Undo / Redo History ------------------------------------------------
  const { historyPast, historyFuture, commitHistory, undo, redo } = useWorkflowHistory(id);

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

  // Tracks which workflow id we've already fit-viewed. Prevents lifecycle
  // refetches (lock/unlock/publish/disable) from re-fitting and stealing the
  // user's current zoom/pan.
  const fittedWorkflowIdRef = useRef<string | null>(null);

  // ---- Copy / Paste -------------------------------------------------------
  const { copySelection, pasteBuffer, resetPasteCount, updateSelection } = useWorkflowClipboard(commitHistory);

  useEffect(() => {
    if (workflow) {
      syncFromServer(workflow.name);
      resetPasteCount();
      pushRecentWorkflow(workflow.id);
      try {
        const def = JSON.parse(workflow.definitionJson);
        // Groups must come before their children in the array (React Flow renders in array order).
        const rawNodes: Node[] = (def.nodes || []);
        setNodes([...rawNodes].sort((a) => (a.type === 'group' ? -1 : 1)));
        setEdges(def.edges || []);
        // Call fitView explicitly after loading — only if the workflow actually has
        // nodes. The setTimeout gives React Flow time to render the nodes into its
        // internal store before the viewport calculation runs.
        // We deliberately do NOT use the fitView prop on <ReactFlow>, since it re-fires
        // on every 0→1 node transition and would then distort both the zoom level and
        // the drop position during the very first drag-and-drop.
        // fitView only runs on the *first* load of a workflow — not on refetches
        // triggered by lock/unlock/publish/disable, which only change the lifecycle
        // status and must respect the user's current zoom.
        if (rawNodes.length > 0 && fittedWorkflowIdRef.current !== workflow.id) {
          fittedWorkflowIdRef.current = workflow.id;
          setTimeout(() => fitView({ padding: 0.15 }), 50);
        }
      } catch { setNodes([]); setEdges([]); }
    }
  }, [workflow, setNodes, setEdges, resetPasteCount, fitView, syncFromServer]);

  const { legendMachines, handleVarHover } = useNodeAnnotations({
    workflowId: id,
    workflowIsEnabled: !!workflow?.isEnabled,
    nodes, setNodes, edges, setEdges,
    selected,
    liveExecution: effectiveCanvasExecution,
    replayExecutionId, replaySteps, scrubTimeMs,
    machines,
  });
  const isDark = useThemeStore((s) => s.resolvedTheme === 'dark');
  const machineColoringEnabled = useDesignStore((s) => s.machineColoringEnabled);
  const coverageHeatmapEnabled = useDesignStore((s) => s.coverageHeatmapEnabled);
  const coverageWindowDays = useDesignStore((s) => s.coverageWindowDays);
  const criticalPathEnabled = useDesignStore((s) => s.criticalPathEnabled);
  useCoverageHeatmap({
    workflowId: id,
    enabled: coverageHeatmapEnabled,
    windowDays: coverageWindowDays,
    setNodes,
  });
  useCriticalPath(id, criticalPathEnabled, nodes, edges);

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
    (connection) => !hasDuplicateConnection(connection.source, connection.target),
    [hasDuplicateConnection],
  );

  const onConnect = useCallback(
    (params: Connection) => {
      if (hasDuplicateConnection(params.source, params.target)) {
        showConnectionNotice('Diese Verbindung existiert bereits. Bearbeite die bestehende Edge oder passe deren Bedingung an.');
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
    [setEdges, commitHistory, hasDuplicateConnection, showConnectionNotice, markDirty],
  );

  // Endpunkt einer existierenden Edge auf andere Source/Target ziehen — Detach-und-Reattach.
  // edge.data (label, condition, disabled, sourceHandle/targetHandle-Defaults) bleiben erhalten.
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
    [edges, setEdges, commitHistory, showConnectionNotice, markDirty],
  );

  const onSelectionChange = useCallback(({ nodes: sn, edges: se }: OnSelectionChangeParams) => {
    updateSelection({ nodeIds: sn.map((n) => n.id), edgeIds: se.map((e) => e.id) });
    if (se.length === 1 && sn.length === 0) setSelected({ type: 'edge', id: se[0].id });
    else if (sn.length === 1 && se.length === 0) setSelected({ type: 'node', id: sn[0].id });
    else setSelected(null);
  }, [updateSelection]);

  // ---- Quick-Edit Popup (Doppelklick auf Node) ----------------------------
  const [quickEdit, setQuickEdit] = useState<{ node: Node; x: number; y: number } | null>(null);
  const [scriptEditNodeId, setScriptEditNodeId] = useState<string | null>(null);

  const onNodeDoubleClick = useCallback((_e: React.MouseEvent, node: Node) => {
    if (node.type !== 'activity') return;
    const activityType = (node.data as Record<string, unknown>)?.activityType as string | undefined;
    // RunScript bekommt den vollen ScriptEditorDialog (CodeMirror, PowerShell-Lint, Run-Button)
    // statt des Quick-Edit-Popups — die Mini-Textarea ist dafür völlig unzureichend.
    if (activityType === 'runScript') {
      setScriptEditNodeId(node.id);
      return;
    }
    setQuickEdit({ node, x: _e.clientX, y: _e.clientY });
  }, []);

  // Live drop-target highlight: id of the group the dragged node currently hovers over (and would
  // be reparented into on drop). Null clears the highlight. Only flips when the target actually
  // changes, so re-renders stay rare even though onNodeDrag fires on every move.
  const [dropTargetGroupId, setDropTargetGroupId] = useState<string | null>(null);

  const onNodeDrag = useCallback((_e: React.MouseEvent, node: Node) => {
    if (node.type === 'group') { setDropTargetGroupId(null); return; }
    const over = findDropTargetGroupId(nodes, node);
    // Highlight only when dropping there would change the parent (skip the node's current group).
    const next = over && over !== (node.parentId ?? null) ? over : null;
    setDropTargetGroupId((prev) => (prev === next ? prev : next));
  }, [nodes]);

  // n8n-style drag-into-group: when a drag ends, a node whose center landed inside a group frame
  // becomes that group's child (and one dragged out is detached). History is already committed by
  // onNodeDragStart('Move nodes'), so the reparent rides the same undo step; the move itself
  // already marked the workflow dirty. The third arg carries every node of a multi-select drag.
  const onNodeDragStop = useCallback((_e: React.MouseEvent, node: Node, draggedNodes: Node[]) => {
    setDropTargetGroupId(null);
    const ids = (draggedNodes.length > 0 ? draggedNodes : [node]).map((n) => n.id);
    markDirty();
    setNodes((nds) => reparentDraggedNodes(nds, ids) ?? nds);
  }, [setNodes, markDirty]);

  const handleQuickEditSave = useCallback((nodeId: string, configPatch: Record<string, unknown>) => {
    // Reihenfolge analog zum Delete-Handler: commitHistory() + markDirty() MÜSSEN vor
    // setNodes feuern, sonst snapshottet useWorkflowHistory den bereits mutierten State und
    // Undo ist kaputt. Programmatisches setNodes triggert onNodesChange nicht, also wird
    // die Auto-Dirty-Logik in onNodesChangeDirty hier nicht aktiv — setIsDirty explizit.
    commitHistory('Quick edit');
    markDirty();
    setNodes((nds) => nds.map((n) => {
      if (n.id !== nodeId) return n;
      const config = (n.data as Record<string, unknown>).config as Record<string, unknown> ?? {};
      return { ...n, data: { ...n.data, config: { ...config, ...configPatch } } };
    }));
  }, [setNodes, commitHistory, markDirty]);

  // ---- Find & Replace (Ctrl+H) -------------------------------------------
  const [findReplaceOpen, setFindReplaceOpen] = useState(false);

  // ---- Fullscreen / Distraction-free (F11) -------------------------------
  const [fullscreen, setFullscreen] = useState(false);

  // KI-Workflow-Assistent (angedocktes rechtes Panel)
  const [aiChatOpen, setAiChatOpen] = useState(false);
  const getCurrentDefinition = useCallback(
    () => stripRuntimeDefinition({ nodes, edges }),
    [nodes, edges],
  );
  const applyAiDefinition = useCallback((def: { nodes: Node[]; edges: Edge[] }) => {
    // Reihenfolge wie bei jeder programmatischen Graph-Mutation: History + Dirty VOR setNodes,
    // damit Undo den Vor-Zustand snapshottet. group-Nodes vor Children (wie beim Initial-Load).
    commitHistory('AI assistant change');
    markDirty();
    setNodes([...def.nodes].sort((a) => (a.type === 'group' ? -1 : 1)));
    setEdges(def.edges);
  }, [commitHistory, markDirty, setNodes, setEdges]);
  // Aktuelle Canvas-Selektion (Labels) für das „Auswahl"-Scoping im KI-Chat.
  const aiSelection = useMemo(() => ({
    nodeLabels: nodes.filter((n) => n.selected).map((n) => (n.data?.label as string) || n.id),
    edgeCount: edges.filter((e) => e.selected).length,
  }), [nodes, edges]);
  const toggleFullscreen = useCallback(() => setFullscreen((f) => !f), []);

  // ---- Quick-Switcher / Recent Workflows (Ctrl+P) -------------------------
  const [quickSwitcherOpen, setQuickSwitcherOpen] = useState(false);
  const toggleQuickSwitcher = useCallback(() => setQuickSwitcherOpen((o) => !o), []);

  // ---- Command Palette (Ctrl+Shift+P) -------------------------------------
  const [commandPaletteOpen, setCommandPaletteOpen] = useState(false);
  const toggleCommandPalette = useCallback(() => setCommandPaletteOpen((o) => !o), []);

  // ---- Activity-Type-Filter (transient — not persisted, resets on workflow switch) ----
  const [hiddenActivityTypes, setHiddenActivityTypes] = useState<Set<string>>(new Set());

  // ---- Failure heatmap (toggled in the overlays menu, applied per-node via __failureTint) ----
  const failureHeatmapEnabled = useDesignStore((s) => s.failureHeatmapEnabled);

  // ---- Search & Jump (Ctrl+F) --------------------------------------------
  const [searchOpen, setSearchOpen] = useState(false);
  const [searchInput, setSearchInput] = useState('');
  const { setCenter } = useReactFlow();
  const searchResults = useMemo(() => {
    const q = searchInput.trim().toLowerCase();
    if (!q) return [] as Node[];
    return nodes.filter((n) => {
      const d = n.data as Record<string, unknown>;
      const label = ((d?.label as string) || '').toLowerCase();
      const type = ((d?.activityType as string) || '').toLowerCase();
      return label.includes(q) || type.includes(q) || n.id.toLowerCase().includes(q);
    });
  }, [nodes, searchInput]);

  const jumpToNode = useCallback((n: Node) => {
    const x = (n.position?.x ?? 0) + ((n.measured?.width ?? 140) / 2);
    const y = (n.position?.y ?? 0) + ((n.measured?.height ?? 100) / 2);
    setCenter(x, y, { zoom: 1.1, duration: 400 });
    setSelected({ type: 'node', id: n.id });
    setSearchOpen(false);
    setSearchInput('');
  }, [setCenter]);

  const jumpToEdge = useCallback((e: Edge) => {
    const src = nodes.find((n) => n.id === e.source);
    const tgt = nodes.find((n) => n.id === e.target);
    if (!src || !tgt) return;
    const x = ((src.position.x + (src.measured?.width ?? 140) / 2) + (tgt.position.x + (tgt.measured?.width ?? 140) / 2)) / 2;
    const y = ((src.position.y + (src.measured?.height ?? 100) / 2) + (tgt.position.y + (tgt.measured?.height ?? 100) / 2)) / 2;
    setCenter(x, y, { zoom: 1.1, duration: 400 });
    setSelected({ type: 'edge', id: e.id });
  }, [nodes, setCenter]);

  // ---- Help overlay -------------------------------------------------------
  const [helpOpen, setHelpOpen] = useState(false);

  // ---- Auto-Layout (Tidy) — cycles through LR → TB → Compact → ELK --------
  const layoutMode = useDesignStore((s) => s.layoutMode);
  const setLayoutMode = useDesignStore((s) => s.setLayoutMode);
  const [isTidying, setIsTidying] = useState(false);
  // Snapshot of positions before the first auto-layout in this session.
  const origLayoutRef = useRef<Node[] | null>(null);
  const [hasOrigLayout, setHasOrigLayout] = useState(false);
  const tidyLayout = useCallback(async () => {
    if (nodes.length === 0 || isTidying) return;
    if (origLayoutRef.current === null) {
      origLayoutRef.current = nodes.map((n) => ({ ...n }));
      setHasOrigLayout(true);
    }
    commitHistory('Tidy layout');
    markDirty();
    const modeToApply = designerMode === 'standard' ? 'LR' : layoutMode;
    const next = LAYOUT_MODES[(LAYOUT_MODES.indexOf(layoutMode) + 1) % LAYOUT_MODES.length];
    if (modeToApply === 'LR') {
      setNodes(autoLayout(nodes, edges));
    } else if (modeToApply === 'TB') {
      setNodes(autoLayoutTB(nodes, edges));
    } else if (modeToApply === 'Compact') {
      setNodes(autoLayoutCompact(nodes, edges));
    } else {
      setIsTidying(true);
      try { setNodes(await autoLayoutELK(nodes, edges)); }
      finally { setIsTidying(false); }
    }
    if (designerMode === 'expert') setLayoutMode(next);
  }, [nodes, edges, designerMode, layoutMode, isTidying, commitHistory, markDirty, setNodes, setLayoutMode]);
  const restoreOrigLayout = useCallback(() => {
    if (!origLayoutRef.current) return;
    commitHistory('Restore layout');
    markDirty();
    setNodes(origLayoutRef.current);
    origLayoutRef.current = null;
    setHasOrigLayout(false);
  }, [commitHistory, markDirty, setNodes]);

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

  // ---- Node Alignment (multi-select) --------------------------------------

  // ---- Export as PNG ------------------------------------------------------
  // WYSIWYG: capture the entire `.react-flow` element at its current size and
  // current viewport transform, so the PNG matches what the user sees on screen
  // (background pattern, edges, nodes, panels). Editor chrome (Controls,
  // MiniMap, attribution) is filtered out — that's UI, not workflow content.
  const exportPng = useCallback(async () => {
    const flow = canvasRef.current?.querySelector('.react-flow') as HTMLElement | null;
    if (!flow) return;
    const surfaceBg = canvasRef.current
      ? getComputedStyle(canvasRef.current).backgroundColor
      : '#ffffff';
    try {
      const dataUrl = await toPng(flow, {
        backgroundColor: surfaceBg,
        pixelRatio: Math.max(globalThis.devicePixelRatio || 1, 2),
        cacheBust: true,
        filter: (node) => {
          if (!(node instanceof HTMLElement)) return true;
          const cls = node.classList;
          if (!cls) return true;
          if (cls.contains('react-flow__controls')) return false;
          if (cls.contains('react-flow__minimap')) return false;
          if (cls.contains('react-flow__attribution')) return false;
          return true;
        },
      });
      const a = document.createElement('a');
      a.href = dataUrl;
      a.download = `${name || 'workflow'}.png`;
      a.click();
    } catch { /* ignore render errors */ }
  }, [name]);

  // ---- Dry-Run / Simulate -------------------------------------------------
  const { simulation, revealIndex, runSimulation, clearSimulation } = useWorkflowSimulation(nodes, edges);

  // ---- Node Context Menu --------------------------------------------------
  const [contextMenu, setContextMenu] = useState<{ nodeId: string; x: number; y: number } | null>(null);

  const handleNodeContextMenu = useCallback((e: React.MouseEvent, node: Node) => {
    if (node.type !== 'activity') return;
    e.preventDefault();
    const rect = canvasRef.current?.getBoundingClientRect();
    if (!rect) return;
    setContextMenu({ nodeId: node.id, x: e.clientX - rect.left, y: e.clientY - rect.top });
  }, []);

  // ---- Edge Context Menu --------------------------------------------------
  const [edgeContextMenu, setEdgeContextMenu] = useState<{ edgeId: string; x: number; y: number } | null>(null);

  const handleEdgeContextMenu = useCallback((e: React.MouseEvent, edge: Edge) => {
    if (!canWrite) return;  // Viewers / read-only mode get no edit-actions menu
    e.preventDefault();
    const rect = canvasRef.current?.getBoundingClientRect();
    if (!rect) return;
    setEdgeContextMenu({ edgeId: edge.id, x: e.clientX - rect.left, y: e.clientY - rect.top });
    setSelected({ type: 'edge', id: edge.id });
  }, [canWrite]);

  // ---- Quick-Connect (drag handle → empty canvas) + Edge-Insert (`+` on edge) ----
  const {
    quickConnect, setQuickConnect, handleConnectEnd, handleQuickConnectPick,
    insertAt, setInsertAt, requestInsert, insertOnEdge,
  } = useCanvasConnect({
    edges, setNodes, setEdges, setSelected,
    commitHistory, canvasRef, screenToFlowPosition,
  });

  const onConnectEnd = useCallback((event: MouseEvent | TouchEvent, connectionState: FinalConnectionState) => {
    if (connectionState.toNode && hasDuplicateConnection(connectionState.fromNode?.id, connectionState.toNode.id)) {
      showConnectionNotice('Diese Verbindung existiert bereits. Bearbeite die bestehende Edge oder passe deren Bedingung an.');
      return;
    }
    handleConnectEnd(event, connectionState);
  }, [handleConnectEnd, hasDuplicateConnection, showConnectionNotice]);

  // ---- Workflow-Diff ------------------------------------------------------
  const [diffOpen, setDiffOpen] = useState(false);

  const { addNode, addSnippet, duplicateNode, deleteNodeById, groupSelection } = useNodeOperations({
    nodes, setNodes, edges, setEdges,
    selected, setSelected,
    commitHistory,
    canvasRef, screenToFlowPosition,
  });

  // useEditorKeyboardShortcuts is set up later in the file, AFTER all the mutations,
  // tidyLayout, handleRunClick and lintPanel state it depends on are declared. See the
  // call-site near `handleRunClick` for the actual binding.

  const handleNodeDataUpdate = useCallback(
    (nodeId: string, newData: Record<string, unknown>) => {
      const isStructural = 'disabled' in newData || 'breakpoint' in newData || 'outputVariable' in newData || 'breakpointCondition' in newData;
      if (isStructural) commitHistory('Edit property'); else commitHistoryForPropertyEdit();
      markDirty();
      // Merge onto the existing node data — never replace it. Callers that pass a full
      // data object (PropertiesPanel, "Embed Workflow") are unaffected, but partial-patch
      // callers (the right-click context menu's disable/breakpoint toggles) would otherwise
      // wipe activityType/config/label and produce an invalid definition that the backend
      // rejects on save (400 "data.activityType is required").
      setNodes((nds: Node[]) => {
        const oldNode = nds.find((n) => n.id === nodeId);
        const oldAlias = (oldNode?.data as Record<string, unknown>)?.outputVariable as string | undefined;
        const newAlias = newData.outputVariable as string | undefined;
        if (oldAlias && newAlias && oldAlias !== newAlias) {
          const downstream = getDownstreamNodeIds(nodeId, edges);
          // Auto-refactor edges too — legacy condition shortcuts + conditionExpression variable refs.
          // Done as a side effect on the same alias change so node + edge stay consistent.
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
        }
        return { ...n, data: newData };
      }));
    },
    [setNodes, commitHistory, markDirty],
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

  // Reshape actions for cubic-Bezier edge handles. History/Dirty centralized here
  // (commit BEFORE mutation so the snapshot captures the pre-shape state — undo restores
  // correctly). Programmatic setEdges bypasses onEdgesChangeDirty, so setIsDirty is explicit.
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

  // Mark dirty on any graph or name change (after initial load).
  const onNodesChangeDirty = useCallback((changes: Parameters<typeof onNodesChange>[0]) => {
    onNodesChange(changes);
    if (changes.some((c) => c.type !== 'select' && c.type !== 'dimensions')) markDirty();
  }, [onNodesChange, markDirty]);
  const onEdgesChangeDirty = useCallback((changes: Parameters<typeof onEdgesChange>[0]) => {
    onEdgesChange(changes);
    if (changes.some((c) => c.type !== 'select')) markDirty();
  }, [onEdgesChange, markDirty]);

  // "Cannot execute" is now surfaced entirely by the always-on lint: roots are trigger-only, so a
  // graph with nodes but no (enabled) trigger — including a cycle-only graph — yields the
  // `no-trigger` lint error (which also blocks publish). No separate cycle banner needed.

  // Statische Validierung — läuft auf jeder Graph-Änderung, Ergebnis rendert im Header-Badge
  // und (bei Click) im Lint-Panel über der Canvas. Keine Blockade: der User kann trotzdem
  // speichern, aber sieht die Warnungen sofort.
  const lintResult = useMemo(() => lintWorkflow(nodes, edges, allWorkflows), [nodes, edges, allWorkflows]);
  const [lintPanelOpen, setLintPanelOpen] = useState(false);

  // Pre-publish lint folds the standard lint with extra publish-time-only checks (no trigger,
  // trigger without out-edge, missing description). Computed lazily so the modal sees a fresh
  // snapshot at the moment the user clicks Publish.
  const prePublishLint = useMemo(
    () => getPrePublishLint(lintResult, nodes, edges, workflow),
    [lintResult, nodes, edges, workflow],
  );
  const [prePublishOpen, setPrePublishOpen] = useState(false);

  const {
    run, confirmRunWithParams, closeRunDialog, showRunDialog, lastExecutionList,
  } = useWorkflowExecution({
    workflowId: id, workflow, canWrite, isDirty, nodes, edges,
    saveAsync, pinCanvasExecution, clearReplay,
  });

  // Lifecycle / save / run shortcut bindings. Each one self-gates against current state so
  // the keyboard hook can fire blindly without re-implementing the same conditions surfaced
  // in the toolbar buttons + command palette. Declared here (not earlier) because they
  // reference saveMutation/lockMutation/handleRunClick/etc., which are declared above.
  const triggerSave = useCallback(() => {
    if (canWrite && isDirty && !isSaving) save();
  }, [canWrite, isDirty, isSaving, save]);
  const triggerLock = useCallback(() => {
    if (roleCanWrite && !isLockedByMe && !isLockedByOther && !isLocking) lock();
  }, [roleCanWrite, isLockedByMe, isLockedByOther, isLocking, lock]);
  const triggerUnlock = useCallback(() => {
    if (isLockedByMe && !isUnlocking) unlock();
  }, [isLockedByMe, isUnlocking, unlock]);
  const triggerForceUnlock = useCallback(async () => {
    if (isAdmin && isLockedByOther && !isForceUnlocking) {
      if (await confirmDialog(t('editor:banners.forceUnlockConfirm', { user: workflow?.checkedOutByUserName ?? t('common:unknown') }))) {
        forceUnlock();
      }
    }
  }, [isAdmin, isLockedByOther, isForceUnlocking, forceUnlock, workflow?.checkedOutByUserName, t]);
  // Routes a publish/enable click (toolbar, command palette, keyboard) through the
  // pre-publish checklist. If the workflow is currently Productive (isEnabled), this is
  // really a Disable — the kill-switch path stays direct (no modal) because stopping
  // production is not a "is everything ready"-question.
  const requestPublish = useCallback(async () => {
    if (!roleCanWrite || isLockedByOther) return;
    if (workflow?.isEnabled) {
      if (await confirmDialog(t('editor:stopWorkflowConfirm'))) {
        disable();
      }
      return;
    }
    if (isPublishing || isEnabling) return;
    // Clean lint → straight through. Otherwise gate behind the modal so the user sees the
    // outstanding issues exactly once before going live.
    if (prePublishLint.errors.length === 0 && prePublishLint.warnings.length === 0) {
      if (isLockedByMe) publish();
      else enable();
      return;
    }
    setPrePublishOpen(true);
  }, [roleCanWrite, isLockedByOther, workflow?.isEnabled, isLockedByMe,
      isPublishing, publish, isEnabling, enable, disable, prePublishLint]);

  // Modal "Trotzdem publizieren" / "Publizieren" callback — fires the right mutation.
  // Errors block the button at render time, so we don't re-check here.
  const confirmPrePublish = useCallback(() => {
    setPrePublishOpen(false);
    if (isLockedByMe) publish();
    else enable();
  }, [isLockedByMe, publish, enable]);

  // Keyboard shortcut (Ctrl+Shift+S) reuses the same gate so power users see the modal too.
  const triggerPublish = requestPublish;
  const triggerTest = useCallback(() => {
    if (roleCanWrite && liveExecution?.status !== 'Running') run(false);
  }, [roleCanWrite, liveExecution?.status, run]);
  const triggerDebug = useCallback(() => {
    if (roleCanWrite && liveExecution?.status !== 'Running') run(true);
  }, [roleCanWrite, liveExecution?.status, run]);
  const triggerCancel = useCallback(() => {
    if (liveExecution?.status === 'Running' && liveExecution.executionId) {
      api.post(`/executions/${liveExecution.executionId}/cancel`, {}).catch(() => { /* ignore */ });
    }
   
  }, [liveExecution?.status, liveExecution?.executionId]);
  const triggerTidy = useCallback(() => {
    if (canWrite && !isTidying) tidyLayout();
  }, [canWrite, isTidying, tidyLayout]);
  const toggleLintPanel = useCallback(() => setLintPanelOpen((o) => !o), [setLintPanelOpen]);
  const triggerSimulation = useCallback(() => {
    if (simulation) clearSimulation(); else runSimulation();
  }, [simulation, runSimulation, clearSimulation]);
  const clearActivityTypeFilterCallback = useCallback(() => setHiddenActivityTypes(new Set()), []);
  const exportJson = useCallback(async () => {
    if (!id) return;
    try {
      await downloadFromApi(`/workflows/${id}/export`, `${name || 'workflow'}.workflow.json`);
    } catch (err) {
      toast.error(t('common:exportFailed', { message: (err as Error).message }));
    }
  }, [id, name, t]);

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
  const toggleSelectedDisabled = useCallback(() => toggleSelectedFlag('disabled'), [toggleSelectedFlag]);
  const toggleSelectedBreakpoint = useCallback(() => toggleSelectedFlag('breakpoint'), [toggleSelectedFlag]);

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
    undo, redo, copySelection, pasteBuffer, groupSelection,
    selectAll, zoomToSelection, navigateNode,
    searchOpen, setSearchOpen, setSearchInput,
    helpOpen, setHelpOpen,
    findReplaceOpen, setFindReplaceOpen,
    toggleFullscreen, toggleQuickSwitcher, toggleCommandPalette,
    triggerSave, triggerLock, triggerUnlock, triggerForceUnlock,
    triggerPublish, triggerTest, triggerDebug, triggerCancel,
    triggerTidy, toggleLintPanel,
    // Layout
    restoreOrigLayout,
    setDiffOpen,
    triggerSimulation,
    clearActivityTypeFilter: clearActivityTypeFilterCallback,
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
    toggleSelectedDisabled, toggleSelectedBreakpoint,
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

  // Sub-workflow preview — held outside the per-edit-action state because it can be opened
  // from any selected startWorkflow node, not from a specific user action like Save.
  const [previewSubWorkflowRef, setPreviewSubWorkflowRef] = useState<string | null>(null);

  const defaultEdgeOptions = useMemo(() => ({
    type: 'labeled',
    // Animation is handled by the np-edge-flow overlay path in LabeledEdge (CSS keyframe),
    // not via React Flow's built-in edge.animated — that would add its own CSS dash
    // animation and conflict with our overlay.
    animated: false,
    markerEnd: { type: MarkerType.ArrowClosed, color: 'var(--color-outline-variant)' },
  }), []);
  const selectedNode = selected?.type === 'node' ? nodes.find((n) => n.id === selected.id) ?? null : null;
  const selectedEdge = selected?.type === 'edge' ? edges.find((e) => e.id === selected.id) ?? null : null;
  const isStartWorkflowSelected = !!selectedNode
    && (selectedNode.data as Record<string, unknown>).activityType === 'startWorkflow';

  const { displayedNodes, displayedEdges } = useDisplayedGraph({
    nodes, edges, edgesAnimated, hiddenActivityTypes, dataFlowOverlayEnabled,
    simulation, revealIndex, lintResult, failureHeatmapEnabled,
  });

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

  return (
    <SubWorkflowPreviewContext.Provider value={subWorkflowPreviewContextValue}>
    <div className={`np-designer${isAtelier ? ' wd-atelier' : ''} h-screen flex flex-col bg-surface overflow-hidden`}>
      {/* Header — bleibt im Fullscreen (F11) sichtbar; nur Sidebar / Properties / Bottom-Panel
          und die optionalen Banner werden versteckt, damit der Canvas-Workspace maximiert
          wird, der User aber die Toolbar (Save / Test / Run / Bearbeiten / Publish …) weiterhin
          erreichen kann. */}
      <EditorHeader
        workflowId={id} workflow={workflow}
        name={name} onRename={rename} isDirty={isDirty}
        canWrite={canWrite} nodes={nodes}
        aiChatOpen={aiChatOpen} onToggleAiChat={() => setAiChatOpen((o) => !o)}
        undo={undo} redo={redo} historyPast={historyPast} historyFuture={historyFuture}
        tidyLayout={tidyLayout} isTidying={isTidying} layoutMode={layoutMode}
        restoreOrigLayout={restoreOrigLayout} hasOrigLayout={hasOrigLayout}
        setSearchOpen={setSearchOpen} setFindReplaceOpen={setFindReplaceOpen}
        zoomToSelection={zoomToSelection} setDiffOpen={setDiffOpen}
        simulation={simulation} runSimulation={runSimulation} clearSimulation={clearSimulation}
        lintResult={lintResult} setLintPanelOpen={setLintPanelOpen} setHelpOpen={setHelpOpen}
        hiddenActivityTypes={hiddenActivityTypes} setHiddenActivityTypes={setHiddenActivityTypes}
        liveExecution={liveExecution} handleRunClick={run}
        exportPng={exportPng} onSave={save} isPublishing={isPublishing}
        onRequestPublish={requestPublish}
        roleCanWrite={roleCanWrite}
        isLockedByMe={isLockedByMe}
        isLockedByOther={isLockedByOther}
        onLock={lock} isLocking={isLocking} onUnlock={unlock} isUnlocking={isUnlocking}
        onDisable={disable} isDisabling={isDisabling} isEnabling={isEnabling}
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
        isLockedByMe={isLockedByMe}
        isLockedByOther={isLockedByOther}
        isAdmin={isAdmin}
        workflow={workflow}
        onForceUnlock={forceUnlock} isForceUnlocking={isForceUnlocking}
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
          searchQuery={searchQuery}
          setSearchQuery={setSearchQuery}
          collapsedCategories={collapsedCategories}
          toggleCategory={toggleCategory}
          panelSize={leftPanel.size}
          panelHandleProps={leftPanel.handleProps}
          addNode={addNode}
          addSnippet={addSnippet}
          isStartWorkflowSelected={isStartWorkflowSelected}
          selected={selected}
          nodes={nodes}
          onOpenWorkflow={(w) => {
            // Pass the current workflow as fromWorkflow in location state so the
            // EditorHeader back button can show where we came from.
            const state = id && workflow ? { fromWorkflow: { id, name: workflow.name } } : undefined;
            navigate(`/workflows/${w.id}`, { state });
          }}
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
          className={`flex-1 relative bg-surface overflow-hidden${premiumCanvas ? ' np-premium' : ''}`}
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
            isValidConnection={isValidConnection}
            onNodeContextMenu={handleNodeContextMenu}
            onEdgeContextMenu={handleEdgeContextMenu}
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
                ? { id: `note-${crypto.randomUUID()}`, type: 'stickyNote', position, data: { label: 'Note', activityType: 'note', text: 'Double-click to edit…', disabled: true } }
                : {
                    id: `step-${crypto.randomUUID()}`,
                    type: 'activity',
                    position,
                    data: {
                      label,
                      activityType: type,
                      targetMachineId: smart.targetMachineId ?? null,
                      credentialId: smart.credentialId ?? null,
                      config: smart.config ?? {},
                    },
                  };
              commitHistory('Add node');
              markDirty();
              setNodes((nds: Node[]) => [...nds, newNode]);
              setSelected({ type: 'node', id: newNode.id });
            }}
            onSelectionChange={onSelectionChange}
            onPaneClick={() => setSelected(null)}
            onEdgeClick={(_event, edge) => setSelected({ type: 'edge', id: edge.id })}
            onNodeDragStart={() => commitHistory('Move nodes')}
            onNodeDrag={canWrite ? onNodeDrag : undefined}
            onNodeDragStop={canWrite ? onNodeDragStop : undefined}
            onBeforeDelete={async ({ nodes: toDelete, edges: toDeleteEdges }) => {
              const groupIds = new Set(toDelete.filter((n) => n.type === 'group').map((n) => n.id));
              if (groupIds.size > 0) {
                // Kinder der zu löschenden Gruppen ungroupen: absolute Positionen wiederherstellen
                // und parentId entfernen, bevor RF die Nodes löscht.
                const groupPos = new Map(nodes.filter((n) => groupIds.has(n.id)).map((n) => [n.id, n.position]));
                setNodes((nds: Node[]) => nds.map((n) => {
                  if (!n.parentId || !groupIds.has(n.parentId)) return n;
                  const gp = groupPos.get(n.parentId) ?? { x: 0, y: 0 };
                  const { parentId: _p, ...rest } = n;
                  void _p;
                  return { ...rest, position: { x: n.position.x + gp.x, y: n.position.y + gp.y } };
                }));
                // Nur die Gruppen selbst löschen — Kinder und ihre Edges bleiben erhalten.
                // RF packt Kinder-Edges automatisch in toDeleteEdges, deshalb explizit rausfiltern.
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
            // Viewport-Virtualisierung: Nodes außerhalb des sichtbaren Bereichs werden
            // nicht als DOM-Elemente gemountet. Greift spürbar ab ~50+ Nodes; für die
            // üblichen 5–20-Node-Workflows neutral, für große Graphen (tech-demo+) ein
            // deutlicher Pan/Zoom-Gewinn. Null-Cost-Flip, daher immer an.
            onlyRenderVisibleElements
            // Marquee-Selection: linker Maus-Drag auf leerer Canvas zieht ein Auswahl-
            // Rechteck. Mittlere/rechte Maustaste pant weiterhin (panOnDrag=[1,2]).
            // SelectionMode.Partial wählt alles, was das Rechteck *berührt* — Full würde
            // nur vollständig enthaltene Nodes treffen und verlangt in der Praxis ein
            // frustrierend großes Rechteck über breite Workflow-Graphen.
            selectionOnDrag
            panOnDrag={[1, 2]}
            selectionMode={SelectionMode.Partial}
            multiSelectionKeyCode={['Shift', 'Control', 'Meta']}
            snapToGrid={snapToGrid}
            snapGrid={[snapGridSize, snapGridSize]}
            connectionLineComponent={NpConnectionLine}
          >
            {/* Controls-Chrome kommt vollständig aus dem token-gespeisten
                `.react-flow__controls`-Block in index.css (--xy-controls-*) —
                keine per-JSX !important-Overrides mehr. */}
            <Controls />
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
              // Free mode (Premium UND Classic): ein einzelnes Punktraster (Dot grid),
              // für beide Modi identisch — alleine, ohne Karomuster. Deutlich sichtbar,
              // aber nicht aufdringlich. Die hellen Skins tragen einen spürbar stärkeren
              // Alpha als die dunklen: auf hellem Grund braucht es mehr Kontrast, damit das
              // Raster klar liest; auf dunklem Grund reicht ein moderaterer Wert (weiß liest
              // ohnehin lauter), damit es präsent, aber nicht dominant wirkt. Etwas größere
              // Dots unterstützen die Sichtbarkeit, ohne das Raster schwer wirken zu lassen.
              <Background
                id="np-bg-dots"
                variant={BackgroundVariant.Dots}
                gap={24}
                size={1.6}
                color={isDark ? 'rgba(255,255,255,.22)' : 'rgba(0,0,0,.42)'}
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
              className={isAtelier
                ? '!bg-surface-lowest/90 !backdrop-blur-md !border !border-outline-variant/40 !rounded-xl !shadow-lg'
                : '!bg-surface-lowest/80 !backdrop-blur-md !border !border-outline-variant/20 !rounded-lg !shadow-sm'}
              pannable
              zoomable
              nodeStrokeWidth={3}
              maskColor={isAtelier
                ? (isDark ? 'rgba(14,16,19,0.62)' : 'rgba(234,231,224,0.65)')
                : (isDark ? 'rgba(17,18,20,0.55)' : 'rgba(220,222,230,0.6)')}
              // Node-Farbe im Mini-Map spiegelt den Activity-Typ grob via `borderColor` —
              // sonst sieht der MiniMap für große Graphen aus wie ein grauer Fleck und man
              // kann seinen Target-Node nicht ausfindig machen. Farben aus den semantischen
              // Status-Tokens (dark folgt automatisch).
              nodeColor={(n) => {
                const d = n.data as Record<string, unknown>;
                const live = d.__liveStatus as string | undefined;
                const health = d.__health as Array<{ status: string }> | undefined;
                const last = health && health.length > 0 ? health.at(-1) : undefined;
                const type = d.activityType as string | undefined;
                // Live execution status has highest priority — gives instant visual feedback
                if (live) return STATUS_COLOR_VAR[npStatusFromExecution(live)];
                // Fall back to most recent historical outcome (container tint = calmer)
                if (last?.status === 'Succeeded') return 'var(--color-success-container)';
                if (last?.status === 'Failed')    return 'var(--color-error-container)';
                // Default: coarse type grouping via tokens
                if (type === 'note')           return 'var(--color-warning-container)';
                if (type?.endsWith('Trigger')) return 'var(--color-success-container)';
                if (type === 'junction' || type === 'startWorkflow' || type === 'forEach' || type === 'returnData')
                                               return 'var(--color-info-container)';
                return 'var(--color-primary-fixed)';
              }}
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
                onOpenWorkflow={(w) => {
                  const state = id && workflow ? { fromWorkflow: { id, name: workflow.name } } : undefined;
                  navigate(`/workflows/${w.id}`, { state });
                }}
              />
            </div>
          )}
          {/* Neuen Workflow anlegen — schwebende Pill oben rechts (einzige freie Canvas-Ecke).
              Im Fullscreen unter den Exit-Pill (top-3 right-3 in <main>) gestapelt, damit keine
              Kollision. Viewer sehen keinen Create-Pfad (roleCanWrite-Gate wie Save/Publish). */}
          {roleCanWrite && workflow && (
            <div className={`absolute ${fullscreen ? 'top-14' : 'top-3'} right-3 z-40 flex flex-col items-end`}>
              <button
                type="button"
                onClick={() => setNewWorkflowOpen((v) => !v)}
                className="bg-surface-lowest/90 backdrop-blur border border-outline-variant/30 rounded-full px-3 py-1.5 text-xs font-label font-semibold text-on-surface-variant hover:bg-surface-high shadow-md transition-colors flex items-center gap-1.5"
                title={t('editor:newWorkflow')}
              >
                <Add size={14} /> {t('editor:newWorkflow')}
              </button>
              {newWorkflowOpen && (
                <div className="mt-1.5 w-64 bg-surface-lowest border border-outline-variant/40 rounded-lg shadow-xl p-3 flex flex-col gap-2">
                  <label className="text-xs font-label font-medium text-on-surface-variant">{t('editor:newWorkflow')}</label>
                  <input
                    autoFocus
                    type="text"
                    data-testid="new-workflow-name-input"
                    value={newWorkflowName}
                    onChange={(e) => setNewWorkflowName(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') { e.preventDefault(); submitNewWorkflow(); }
                      else if (e.key === 'Escape') { e.preventDefault(); setNewWorkflowOpen(false); setNewWorkflowName(''); }
                    }}
                    placeholder={t('workflows:workflowName')}
                    className="w-full px-2.5 py-1.5 text-sm rounded border border-outline-variant/50 bg-surface text-on-surface focus:outline-none focus:border-primary"
                  />
                  <div className="flex justify-end gap-2">
                    <button
                      type="button"
                      onClick={() => { setNewWorkflowOpen(false); setNewWorkflowName(''); }}
                      className="px-2.5 py-1 text-xs rounded border border-outline text-on-surface hover:bg-surface-container-high"
                    >
                      {t('common:cancel')}
                    </button>
                    <button
                      type="button"
                      onClick={submitNewWorkflow}
                      disabled={!newWorkflowName.trim() || createWorkflowMutation.isPending}
                      className="px-2.5 py-1 text-xs rounded bg-primary text-on-primary hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed flex items-center gap-1.5"
                    >
                      {createWorkflowMutation.isPending && <CircleDash size={12} className="animate-spin" />}
                      {t('workflows:create')}
                    </button>
                  </div>
                </div>
              )}
            </div>
          )}
          {connectionNotice && (
            <div className="absolute top-4 left-1/2 -translate-x-1/2 z-30 max-w-[520px] px-4 py-2 rounded-md bg-amber-100 text-amber-950 border border-amber-300 shadow-lg text-sm font-label font-medium">
              {connectionNotice}
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
          {contextMenu && (() => {
            const node = nodes.find((n) => n.id === contextMenu.nodeId);
            if (!node) return null;
            const d = node.data as Record<string, unknown>;
            return (
              <NodeContextMenu
                x={contextMenu.x}
                y={contextMenu.y}
                isDisabled={!!(d.disabled)}
                hasBreakpoint={!!(d.breakpoint)}
                onDuplicate={() => duplicateNode(contextMenu.nodeId)}
                onToggleDisabled={() => handleNodeDataUpdate(contextMenu.nodeId, { disabled: !d.disabled })}
                onToggleBreakpoint={() => handleNodeDataUpdate(contextMenu.nodeId, { breakpoint: !d.breakpoint })}
                onDelete={() => deleteNodeById(contextMenu.nodeId)}
                onClose={() => setContextMenu(null)}
              />
            );
          })()}
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
          {simulation && (() => {
            const total = simulation.order.length;
            const done = Math.min(revealIndex, total);
            const playing = done < total;
            return (
              <div className="absolute top-4 left-1/2 -translate-x-1/2 z-30 flex items-center gap-3 px-4 py-2 rounded-full bg-indigo-600 text-white shadow-xl border border-indigo-400/50">
                <Chemistry size={14} className={`shrink-0 ${playing ? 'animate-pulse' : ''}`} />
                <span className="font-label text-sm font-semibold">
                  {playing ? t('editor:simulation.simulating') : t('editor:simulation.complete')}
                </span>
                {playing ? (
                  <>
                    <span className="font-mono text-xs tabular-nums bg-white/20 rounded-full px-2 py-0.5">
                      {t('editor:simulation.step', { n: done + 1, total })}
                    </span>
                    <div className="w-32 h-1.5 rounded-full bg-white/20 overflow-hidden">
                      <div
                        className="h-full bg-amber-300 transition-all duration-200"
                        style={{ width: `${((done + 1) / total) * 100}%` }}
                      />
                    </div>
                  </>
                ) : (
                  <span className="font-mono text-xs tabular-nums bg-white/20 rounded-full px-2 py-0.5">
                    {t('editor:simulation.willRun', { count: simulation.reachable.size })} · {t('editor:simulation.skipped', { count: simulation.skipped.size })}
                  </span>
                )}
                <button
                  onClick={() => clearSimulation()}
                  className="ml-1 p-1 rounded-full hover:bg-white/20 transition-colors"
                  title={t('editor:simulationClear')}
                >
                  <Close size={14} />
                </button>
              </div>
            );
          })()}
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
                onAutoLayout={tidyLayout}
                selection={aiSelection}
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
        // Graph + identity
        id={id}
        name={name}
        workflow={workflow}
        nodes={nodes}
        edges={edges}
        setNodes={setNodes as (updater: (nds: Node[]) => Node[]) => void}
        setEdges={setEdges as (updater: (eds: Edge[]) => Edge[]) => void}
        // History / dirty
        commitHistory={commitHistory}
        markDirty={markDirty}
        // Run dialog
        showRunDialog={showRunDialog}
        onRunWithParams={confirmRunWithParams}
        onCloseRunDialog={closeRunDialog}
        lastExecutionList={lastExecutionList}
        // Quick edit + script editor
        quickEdit={quickEdit}
        setQuickEdit={setQuickEdit}
        handleQuickEditSave={handleQuickEditSave}
        scriptEditNodeId={scriptEditNodeId}
        setScriptEditNodeId={setScriptEditNodeId}
        // Find/Replace + Search
        findReplaceOpen={findReplaceOpen}
        setFindReplaceOpen={setFindReplaceOpen}
        searchOpen={searchOpen}
        setSearchOpen={setSearchOpen}
        searchInput={searchInput}
        setSearchInput={setSearchInput}
        searchResults={searchResults}
        jumpToNode={jumpToNode}
        jumpToEdge={jumpToEdge}
        // Quick switcher / Command palette
        quickSwitcherOpen={quickSwitcherOpen}
        setQuickSwitcherOpen={setQuickSwitcherOpen}
        commandPaletteOpen={commandPaletteOpen}
        setCommandPaletteOpen={setCommandPaletteOpen}
        // Help / Diff / Lint
        helpOpen={helpOpen}
        setHelpOpen={setHelpOpen}
        diffOpen={diffOpen}
        setDiffOpen={setDiffOpen}
        lintPanelOpen={lintPanelOpen}
        setLintPanelOpen={setLintPanelOpen}
        lintResult={lintResult}
        // Pre-publish checklist
        prePublishOpen={prePublishOpen}
        prePublishLint={prePublishLint}
        onConfirmPrePublish={confirmPrePublish}
        onCancelPrePublish={() => setPrePublishOpen(false)}
        onRequestPublish={requestPublish}
        // Command-Palette context
        roleCanWrite={roleCanWrite}
        canWrite={canWrite}
        isAdmin={isAdmin}
        isLockedByMe={isLockedByMe}
        isLockedByOther={isLockedByOther}
        isDirty={isDirty}
        onSave={save} isSaving={isSaving}
        handleRunClick={run}
        liveExecution={liveExecution}
        onLock={lock}
        isLocking={isLocking}
        onUnlock={unlock}
        isUnlocking={isUnlocking}
        isPublishing={isPublishing}
        isEnabling={isEnabling}
        isDisabling={isDisabling}
        onForceUnlock={forceUnlock}
        isForceUnlocking={isForceUnlocking}
        undo={undo}
        redo={redo}
        copySelection={copySelection}
        pasteBuffer={pasteBuffer}
        groupSelection={groupSelection}
        selected={selected}
        deleteNodeById={deleteNodeById}
        selectAll={selectAll}
        navigateNode={navigateNode}
        tidyLayout={tidyLayout}
        isTidying={isTidying}
        layoutMode={layoutMode}
        restoreOrigLayout={restoreOrigLayout}
        hasOrigLayout={hasOrigLayout}
        simulation={simulation}
        runSimulation={runSimulation}
        clearSimulation={clearSimulation}
        hiddenActivityTypes={hiddenActivityTypes}
        setHiddenActivityTypes={setHiddenActivityTypes}
        toggleFullscreen={toggleFullscreen}
        toggleQuickSwitcher={toggleQuickSwitcher}
        zoomToSelection={zoomToSelection}
        fitViewAll={fitViewAll}
        machineColoringEnabled={machineColoringEnabled}
        failureHeatmapEnabled={failureHeatmapEnabled}
        exportPng={exportPng}
        exportJson={exportJson}
        navigate={navigate}
      />

      {previewSubWorkflowRef !== null && (
        <SubWorkflowPreviewModal
          workflowNameOrId={previewSubWorkflowRef}
          onClose={() => setPreviewSubWorkflowRef(null)}
          onOpenInEditor={(wfId) => {
            // Caller (this page) passes a wfId — navigate the user to the dedicated editor
            // route. setPreviewSubWorkflowRef(null) is already done by the modal's onClose
            // callback (runs after onOpenInEditor inside the modal).
            const state = id && workflow ? { fromWorkflow: { id, name: workflow.name } } : undefined;
            navigate(`/workflows/${wfId}`, { state });
          }}
        />
      )}
    </div>
    </SubWorkflowPreviewContext.Provider>
  );
}
