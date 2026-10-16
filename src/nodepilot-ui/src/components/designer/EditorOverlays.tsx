import { lazy, Suspense } from 'react';
import type { Node, Edge } from '@xyflow/react';
import { api } from '../../api/client';
import { useDesignStore } from '../../stores/designStore';
import { useAiScriptStream } from '../../hooks/useAiScriptStream';
import { buildCommandPaletteCommands, filterCommandsForDesignerMode } from '../../lib/editorCommandPalette';
import { getUpstreamVariables } from '../../lib/upstreamVariables';
import type { StepTestResult } from '../../types/api';
import type { LintResult } from '../../lib/workflowLint';
import type { Workflow } from '../../types/api';
import type { EditorOverlayControls } from '../../hooks/useEditorOverlays';
import type { EditorLifecycle } from '../../hooks/useEditorLifecycle';
import type { TidyLayoutApi } from '../../hooks/useTidyLayout';
import type { WorkflowGraphSource } from '../../hooks/useWorkflowHistory';
import type { useWorkflowSimulation } from '../../hooks/useWorkflowSimulation';

import { RunWorkflowDialog, extractManualTriggerConfig } from '../common/RunWorkflowDialog';
import { QuickEditPopup } from './overlays/QuickEditPopup';
import { FindReplaceOverlay } from './overlays/FindReplaceOverlay';
import { WorkflowQuickSwitcher } from './overlays/WorkflowQuickSwitcher';
import { CommandPalette } from './overlays/CommandPalette';
import { SearchOverlay } from './overlays/SearchOverlay';
import { HelpOverlay } from './overlays/HelpOverlay';
import { WorkflowDiffModal } from './overlays/WorkflowDiffModal';
import { LintPanel } from './overlays/LintPanel';
import { PrePublishChecklistModal } from './overlays/PrePublishChecklistModal';
import { SubWorkflowPreviewModal } from './overlays/SubWorkflowPreviewModal';

// Monaco editor (~1 MB) lives in this dialog — lazy-loaded so the workflow editor
// initial bundle stays unchanged. The chunk is fetched only when the user
// double-clicks a runScript node or opens it via the properties panel.
const ScriptEditorDialog = lazy(() => import('./ScriptEditorDialog'));

/** Graph state plus history and dirty tracking, for overlays that edit the graph. */
export interface EditorGraphEditing extends WorkflowGraphSource {
  commitHistory: (label?: string) => void;
  markDirty: () => void;
}

/** Running the workflow: the parameter dialog and the run controls the palette offers. */
export interface EditorRunControls {
  showRunDialog: boolean;
  confirmRunWithParams: (params: Record<string, string>) => void;
  closeRunDialog: () => void;
  lastExecutionList: Array<{ inputParametersJson?: string | null }> | undefined;
  run: (debug?: boolean) => void;
  cancelRun: () => void;
  isRunning: boolean;
}

/** Editor commands the command palette and the overlays trigger. */
export interface EditorActions {
  undo: () => void;
  redo: () => void;
  copySelection: () => void;
  pasteBuffer: () => void;
  groupSelection: () => void;
  selected: { type: 'node' | 'edge'; id: string } | null;
  deleteNodeById: (id: string) => void;
  selectAll: () => void;
  navigateNode: (dir: 'next' | 'prev') => void;
  handleQuickEditSave: (nodeId: string, configPatch: Record<string, unknown>) => void;
  jumpToNode: (n: Node) => void;
  jumpToEdge: (e: Edge) => void;
  toggleFullscreen: () => void;
  zoomToSelection: () => void;
  fitViewAll: () => void;
  exportPng: () => void;
  exportJson: () => void;
}

export interface EditorOverlaysProps {
  workflowId: string | undefined;
  name: string;
  workflow: Workflow | undefined;
  graph: EditorGraphEditing;
  overlays: EditorOverlayControls;
  lifecycle: EditorLifecycle;
  execution: EditorRunControls;
  actions: EditorActions;
  layout: TidyLayoutApi;
  simulation: ReturnType<typeof useWorkflowSimulation>;
  lintResult: LintResult;
  hiddenActivityTypes: Set<string>;
  setHiddenActivityTypes: (v: Set<string>) => void;
  /** Fires the publish or enable once the pre-publish checklist is confirmed. */
  onConfirmPrePublish: () => void;
  /** Opens another workflow in the editor, remembering this one for the back button. */
  openWorkflow: (workflowId: string) => void;
  navigate: (to: string) => void;
}

/**
 * Renders every overlay, modal and dialog that floats over the workflow canvas. It owns no state:
 * the open flags live in {@link EditorOverlayControls}, which the page also hands to the header
 * and the keyboard shortcuts.
 */
export function EditorOverlays({
  workflowId: id, name, workflow, graph, overlays, lifecycle, execution, actions, layout,
  simulation: sim, lintResult, hiddenActivityTypes, setHiddenActivityTypes,
  onConfirmPrePublish, openWorkflow, navigate,
}: Readonly<EditorOverlaysProps>) {
  const designerMode = useDesignStore((s) => s.designerMode);
  const machineColoringEnabled = useDesignStore((s) => s.machineColoringEnabled);
  const failureHeatmapEnabled = useDesignStore((s) => s.failureHeatmapEnabled);
  const { nodes, edges, setNodes, setEdges, commitHistory, markDirty } = graph;
  const {
    quickEdit, setQuickEdit, scriptEditNodeId, setScriptEditNodeId,
    findReplaceOpen, setFindReplaceOpen,
    searchOpen, setSearchOpen, searchInput, setSearchInput,
    quickSwitcherOpen, setQuickSwitcherOpen, toggleQuickSwitcher,
    commandPaletteOpen, setCommandPaletteOpen,
    helpOpen, setHelpOpen, diffOpen, setDiffOpen,
    lintPanelOpen, setLintPanelOpen,
    prePublishLint, setPrePublishLint,
    previewSubWorkflowRef, setPreviewSubWorkflowRef,
  } = overlays;
  const { canWrite, isDirty, isPublishing, isEnabling } = lifecycle;
  const { showRunDialog, confirmRunWithParams, closeRunDialog, lastExecutionList } = execution;
  const {
    undo, redo, copySelection, pasteBuffer, groupSelection, selected, deleteNodeById, selectAll,
    navigateNode, handleQuickEditSave, jumpToNode, jumpToEdge, toggleFullscreen, zoomToSelection,
    fitViewAll, exportPng, exportJson,
  } = actions;
  const { simulation, runSimulation, clearSimulation } = sim;
  const closePrePublish = () => setPrePublishLint(null);

  return (
    <>
      {/* Run Workflow Dialog (shows when ManualTrigger has parameters) */}
      {showRunDialog && (() => {
        const triggerConfig = extractManualTriggerConfig(JSON.stringify({ nodes, edges }));
        if (!triggerConfig) return null;
        // Last-run prefill: take the most recent execution's input parameters as initial values.
        // Empty if no prior runs or the JSON is malformed.
        let lastRunParams: Record<string, string> | undefined;
        try {
          const lastExec = lastExecutionList?.[0];
          if (lastExec?.inputParametersJson) {
            const parsed = JSON.parse(lastExec.inputParametersJson) as Record<string, unknown>;
            lastRunParams = Object.fromEntries(
              Object.entries(parsed).map(([k, v]) => [k, typeof v === 'string' ? v : String(v)]),
            );
          }
        } catch { /* malformed last-run JSON — just skip prefill */ }
        return (
          <RunWorkflowDialog
            workflowName={name}
            triggerTitle={triggerConfig.title}
            triggerDescription={triggerConfig.description}
            parameters={triggerConfig.parameters}
            lastRunParams={lastRunParams}
            onExecute={confirmRunWithParams}
            onCancel={closeRunDialog}
          />
        );
      })()}

      {/* Quick-edit popup (double-click on a node) */}
      {quickEdit && (
        <QuickEditPopup
          node={quickEdit.node}
          screenX={quickEdit.x}
          screenY={quickEdit.y}
          onSave={handleQuickEditSave}
          onClose={() => setQuickEdit(null)}
        />
      )}

      {/* ScriptEditorDialog for double-clicking a runScript node — mirrors the RunScriptConfig logic. */}
      {scriptEditNodeId && (
        <ScriptDoubleClickEditor
          nodeId={scriptEditNodeId}
          nodes={nodes}
          edges={edges}
          workflowId={id}
          setNodes={setNodes}
          onClose={() => { setScriptEditNodeId(null); commitHistory('Edit script'); }}
        />
      )}

      {/* Find & Replace Overlay (Ctrl+H) */}
      {findReplaceOpen && (
        <FindReplaceOverlay
          canWrite={canWrite}
          nodes={nodes}
          edges={edges}
          onApply={(newNodes, newEdges) => {
            if (!canWrite) return;
            commitHistory('Find & replace');
            setNodes(() => newNodes);
            setEdges(() => newEdges);
            markDirty();
          }}
          onClose={() => setFindReplaceOpen(false)}
        />
      )}

      {/* Workflow Quick Switcher (Ctrl+P) */}
      {quickSwitcherOpen && <WorkflowQuickSwitcher onClose={() => setQuickSwitcherOpen(false)} isDirty={isDirty} />}

      {/* Command Palette (Ctrl+Shift+P) */}
      {commandPaletteOpen && (
        <CommandPalette
          onClose={() => setCommandPaletteOpen(false)}
          commands={filterCommandsForDesignerMode(buildCommandPaletteCommands({
            // Role + lock state
            roleCanWrite: lifecycle.roleCanWrite,
            canWrite,
            isAdmin: lifecycle.isAdmin,
            isLockedByMe: lifecycle.isLockedByMe,
            isLockedByOther: lifecycle.isLockedByOther,
            workflowIsEnabled: !!workflow?.isEnabled,
            hasWorkflow: !!id,
            // Save / Run
            isDirty,
            save: lifecycle.save, isSaving: lifecycle.isSaving,
            handleRunClick: execution.run,
            liveExecutionRunning: execution.isRunning,
            cancelRunningExecution: execution.cancelRun,
            // Edit-Lock lifecycle
            lock: lifecycle.lock, isLocking: lifecycle.isLocking,
            unlock: lifecycle.unlock, isUnlocking: lifecycle.isUnlocking, isPublishing,
            isEnabling, isDisabling: lifecycle.isDisabling,
            forceUnlock: lifecycle.forceUnlock, isForceUnlocking: lifecycle.isForceUnlocking,
            requestPublish: lifecycle.requestPublish,
            // Edit operations
            undo, redo,
            copySelection, pasteBuffer, groupSelection,
            deleteSelected: () => {
              if (!selected) return;
              if (selected.type === 'node') deleteNodeById(selected.id);
              else setEdges((eds) => eds.filter((e) => e.id !== selected.id));
            },
            selectAll, navigateNode,
            // Overlays / panels
            setSearchOpen, setFindReplaceOpen, setHelpOpen,
            setDiffOpen, setLintPanelOpen,
            // Layout
            tidyLayout: layout.tidyLayout, isTidying: layout.isTidying, layoutMode: layout.layoutMode,
            restoreOrigLayout: layout.restoreOrigLayout, hasOrigLayout: layout.hasOrigLayout,
            // Simulation
            simulationActive: !!simulation,
            runSimulation, clearSimulation,
            // Activity-type filter
            hiddenActivityTypesCount: hiddenActivityTypes.size,
            clearActivityTypeFilter: () => setHiddenActivityTypes(new Set()),
            // View
            toggleFullscreen, toggleQuickSwitcher, zoomToSelection, fitViewAll,
            machineColoringEnabled,
            failureHeatmapEnabled,
            toggleMachineColoring: () => useDesignStore.getState().toggleMachineColoring(),
            toggleFailureHeatmap: () => useDesignStore.getState().toggleFailureHeatmap(),
            criticalPathEnabled: useDesignStore.getState().criticalPathEnabled,
            toggleCriticalPath: () => useDesignStore.getState().toggleCriticalPath(),
            // Designer style — DesignStore mutators read fresh state on each invocation so
            // the action is always against the current value (e.g. cycling routing).
            edgesAnimated: useDesignStore.getState().edgesAnimated,
            toggleEdgesAnimated: () => useDesignStore.getState().toggleEdgesAnimated(),
            edgeRouting: useDesignStore.getState().edgeRouting,
            cycleEdgeRouting: () => {
              const order = ['smart', 'curved', 'straight'] as const;
              const cur = useDesignStore.getState().edgeRouting;
              const next = order[(order.indexOf(cur as typeof order[number]) + 1) % order.length];
              useDesignStore.getState().setEdgeRouting(next);
            },
            edgeWidthInc: () => useDesignStore.getState().edgeWidthInc(),
            edgeWidthDec: () => useDesignStore.getState().edgeWidthDec(),
            nodeStyle: useDesignStore.getState().nodeStyle,
            toggleNodeStyle: () => useDesignStore.getState().toggleNodeStyle(),
            zoomIn: () => useDesignStore.getState().zoomIn(),
            zoomOut: () => useDesignStore.getState().zoomOut(),
            labelFontInc: () => useDesignStore.getState().labelFontInc(),
            labelFontDec: () => useDesignStore.getState().labelFontDec(),
            snapToGrid: useDesignStore.getState().snapToGrid,
            setSnapToGrid: (v: boolean) => useDesignStore.getState().setSnapToGrid(v),
            // Persistence / Export
            exportPng,
            exportJson,
            // Navigate
            navigate,
          }), designerMode)}
        />
      )}

      {/* Search & Jump Overlay */}
      {searchOpen && (
        <SearchOverlay
          value={searchInput}
          onChange={setSearchInput}
          results={findNodes(nodes, searchInput)}
          onPick={jumpToNode}
          onClose={() => { setSearchOpen(false); setSearchInput(''); }}
        />
      )}

      {/* Keyboard Shortcut Help */}
      {helpOpen && <HelpOverlay onClose={() => setHelpOpen(false)} />}

      {/* Workflow-Diff Modal */}
      {diffOpen && id && (
        <WorkflowDiffModal
          workflowId={id}
          currentDefinition={{ nodes, edges }}
          canRestore={canWrite}
          onClose={() => setDiffOpen(false)}
        />
      )}

      {/* Lint Panel — floating over canvas, open via Badge */}
      {lintPanelOpen && (lintResult.errors.length + lintResult.warnings.length) > 0 && (
        <LintPanel
          result={lintResult}
          nodes={nodes}
          edges={edges}
          onJump={(nodeId) => {
            const n = nodes.find((x) => x.id === nodeId);
            if (n) jumpToNode(n);
          }}
          onJumpEdge={(edgeId) => {
            const e = edges.find((x) => x.id === edgeId);
            if (e) jumpToEdge(e);
          }}
          onClose={() => setLintPanelOpen(false)}
        />
      )}

      {/* Pre-Publish Checklist — gates the Publish click. Opened by `requestPublish` in
          the page when the lint is non-empty; clean publishes skip the modal entirely. */}
      {prePublishLint && (
        <PrePublishChecklistModal
          result={prePublishLint}
          nodes={nodes}
          workflowName={name}
          isPending={isPublishing || isEnabling}
          onConfirm={onConfirmPrePublish}
          onCancel={closePrePublish}
          onJumpToNode={(nodeId) => {
            const n = nodes.find((x) => x.id === nodeId);
            if (n) {
              jumpToNode(n);
              closePrePublish();
            }
          }}
          onJumpToEdge={(edgeId) => {
            const e = edges.find((x) => x.id === edgeId);
            if (e) {
              jumpToEdge(e);
              closePrePublish();
            }
          }}
        />
      )}

      {previewSubWorkflowRef !== null && (
        <SubWorkflowPreviewModal
          workflowNameOrId={previewSubWorkflowRef}
          onClose={() => setPreviewSubWorkflowRef(null)}
          // The modal's onClose runs after this and clears the preview.
          onOpenInEditor={openWorkflow}
        />
      )}
    </>
  );
}

/** Search & jump: nodes whose label, activity type or id contains the query. */
function findNodes(nodes: Node[], input: string): Node[] {
  const q = input.trim().toLowerCase();
  if (!q) return [];
  return nodes.filter((n) => {
    const d = n.data as Record<string, unknown>;
    const label = ((d?.label as string) || '').toLowerCase();
    const type = ((d?.activityType as string) || '').toLowerCase();
    return label.includes(q) || type.includes(q) || n.id.toLowerCase().includes(q);
  });
}


/**
 * runScript double-click editor — its own component so the `useAiScriptStream` hook can
 * always be called unconditionally (Rules of Hooks). Mirrors the RunScriptConfig logic,
 * including the streaming AI generation.
 */
function ScriptDoubleClickEditor({
  nodeId, nodes, edges, workflowId, setNodes, onClose,
}: Readonly<{
  nodeId: string;
  nodes: Node[];
  edges: Edge[];
  workflowId: string | undefined;
  setNodes: (updater: (nds: Node[]) => Node[]) => void;
  onClose: () => void;
}>) {
  const node = nodes.find((n) => n.id === nodeId);
  const data = (node?.data as Record<string, unknown>) ?? {};
  const config = (data.config as Record<string, unknown>) ?? {};
  const upstreamVars = node ? getUpstreamVariables(node.id, nodes, edges) : [];
  // Hook unconditionally before any early return (Rules of Hooks).
  const aiScript = useAiScriptStream({ workflowId, stepId: node?.id, upstreamVars });

  if (!node) return null;

  const psVars = upstreamVars
    .filter((v) => v.expression.includes('.param.'))
    .map((v) => ({ name: '$' + v.variable.split('.').pop()!, label: v.label }));
  const upstreamRefs = upstreamVars.map((v) => ({ expression: v.expression, label: v.label }));
  const outputVariableName = (data.outputVariable as string) || undefined;

  // Live (possibly unsaved) config so the step-test reflects the editor, not the last-saved DB
  // state.
  const runStepTest = async (currentScript: string): Promise<StepTestResult> =>
    api.post<StepTestResult>(`/workflows/${workflowId}/steps/${node.id}/test`, { configOverride: { ...config, script: currentScript } });

  return (
    <Suspense fallback={null}>
      <ScriptEditorDialog
        value={(config.script as string) ?? ''}
        onChange={(v) => {
          setNodes((nds) => nds.map((n) => {
            if (n.id !== nodeId) return n;
            const cfg = (n.data as Record<string, unknown>).config as Record<string, unknown> ?? {};
            return { ...n, data: { ...n.data, config: { ...cfg, script: v } } };
          }));
        }}
        onClose={onClose}
        availableVars={psVars}
        upstreamRefs={upstreamRefs}
        outputVariableName={outputVariableName}
        onRun={workflowId ? runStepTest : undefined}
        onAiGenerate={aiScript?.generate}
        aiTargetHost={aiScript?.targetHost}
      />
    </Suspense>
  );
}
