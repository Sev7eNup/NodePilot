import { lazy, Suspense } from 'react';
import type { Node, Edge } from '@xyflow/react';
import { api } from '../../api/client';
import { useDesignStore } from '../../stores/designStore';
import { useAiScriptStream } from '../../hooks/useAiScriptStream';
import { buildCommandPaletteCommands, filterCommandsForDesignerMode } from '../../lib/editorCommandPalette';
import { getUpstreamVariables } from '../../lib/upstreamVariables';
import type { StepTestResult } from '../../types/api';
import type { LintResult } from '../../lib/workflowLint';
import type { SimulationResult } from '../../hooks/useWorkflowSimulation';
import type { Workflow } from '../../types/api';

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

// Monaco editor (~1 MB) lives in this dialog — lazy-loaded so the workflow editor
// initial bundle stays unchanged. The chunk is fetched only when the user
// double-clicks a runScript node or opens it via the properties panel.
const ScriptEditorDialog = lazy(() => import('./ScriptEditorDialog'));

/**
 * Renders every overlay/modal/dialog that floats over the workflow canvas. Lifted out
 * of <c>WorkflowEditorPage.tsx</c> to keep the page focused on canvas + sidebar layout.
 *
 * <para>This component is intentionally a "dumb" portal — it owns no state of its own;
 * all open/close flags + handlers come in as props. The page composes its own state
 * setters into the relevant fields.</para>
 */
export interface EditorOverlaysProps {
  // ----- Graph + identity -----
  id: string | undefined;
  name: string;
  workflow: Workflow | undefined;
  nodes: Node[];
  edges: Edge[];
  setNodes: (updater: (nds: Node[]) => Node[]) => void;
  setEdges: (updater: (eds: Edge[]) => Edge[]) => void;

  // ----- History / dirty -----
  commitHistory: (label?: string) => void;
  markDirty: () => void;

  // ----- Run dialog -----
  showRunDialog: boolean;
  onRunWithParams: (params: Record<string, string>) => void;
  onCloseRunDialog: () => void;
  lastExecutionList: Array<{ inputParametersJson?: string | null }> | undefined;

  // ----- Quick edit + script editor -----
  quickEdit: { node: Node; x: number; y: number } | null;
  setQuickEdit: (v: { node: Node; x: number; y: number } | null) => void;
  handleQuickEditSave: (nodeId: string, configPatch: Record<string, unknown>) => void;
  scriptEditNodeId: string | null;
  setScriptEditNodeId: (v: string | null) => void;

  // ----- Find/Replace + Search -----
  findReplaceOpen: boolean;
  setFindReplaceOpen: (v: boolean) => void;
  searchOpen: boolean;
  setSearchOpen: (v: boolean) => void;
  searchInput: string;
  setSearchInput: (v: string) => void;
  searchResults: Node[];
  jumpToNode: (n: Node) => void;
  jumpToEdge: (e: Edge) => void;

  // ----- Quick switcher / Command palette -----
  quickSwitcherOpen: boolean;
  setQuickSwitcherOpen: (v: boolean) => void;
  commandPaletteOpen: boolean;
  setCommandPaletteOpen: (v: boolean) => void;

  // ----- Help / Diff / Lint -----
  helpOpen: boolean;
  setHelpOpen: (v: boolean | ((p: boolean) => boolean)) => void;
  diffOpen: boolean;
  setDiffOpen: (v: boolean) => void;
  lintPanelOpen: boolean;
  setLintPanelOpen: (v: boolean | ((p: boolean) => boolean)) => void;
  lintResult: LintResult;

  // ----- Pre-publish checklist -----
  prePublishOpen: boolean;
  prePublishLint: LintResult;
  onConfirmPrePublish: () => void;
  onCancelPrePublish: () => void;
  /** Routes Publish/Enable through the modal (parent decides whether to open). */
  onRequestPublish: () => void;

  // ----- Command-Palette context (everything the palette needs to know) -----
  // Role + lock state
  roleCanWrite: boolean;
  canWrite: boolean;
  isAdmin: boolean;
  isLockedByMe: boolean;
  isLockedByOther: boolean;

  // Save / Run
  isDirty: boolean;
  onSave: () => void;
  isSaving: boolean;
  handleRunClick: (debug?: boolean) => void;
  liveExecution: { executionId?: string; status?: string } | null | undefined;

  // Edit-Lock lifecycle mutations
  onLock: () => void;
  isLocking: boolean;
  onUnlock: () => void;
  isUnlocking: boolean;
  isPublishing: boolean;
  isEnabling: boolean;
  isDisabling: boolean;
  onForceUnlock: () => void;
  isForceUnlocking: boolean;

  // Edit operations
  undo: () => void;
  redo: () => void;
  copySelection: () => void;
  pasteBuffer: () => void;
  groupSelection: () => void;
  selected: { type: 'node' | 'edge'; id: string } | null;
  deleteNodeById: (id: string) => void;
  selectAll: () => void;
  navigateNode: (dir: 'next' | 'prev') => void;

  // Layout / Inspect
  tidyLayout: () => void;
  isTidying: boolean;
  layoutMode: string;
  restoreOrigLayout: () => void;
  hasOrigLayout: boolean;

  // Simulation
  simulation: SimulationResult | null;
  runSimulation: () => void;
  clearSimulation: () => void;

  // Activity-type filter
  hiddenActivityTypes: Set<string>;
  setHiddenActivityTypes: (v: Set<string>) => void;

  // View
  toggleFullscreen: () => void;
  toggleQuickSwitcher: () => void;
  zoomToSelection: () => void;
  fitViewAll: () => void;
  machineColoringEnabled: boolean;
  failureHeatmapEnabled: boolean;

  // Persistence / Export
  exportPng: () => void;
  exportJson: () => void;

  // Navigation
  navigate: (to: string) => void;
}

export function EditorOverlays(props: Readonly<EditorOverlaysProps>) {
  const designerMode = useDesignStore((s) => s.designerMode);
  const {
    id, name, workflow, nodes, edges, setNodes, setEdges,
    commitHistory, markDirty,
    showRunDialog, onRunWithParams, onCloseRunDialog, lastExecutionList,
    quickEdit, setQuickEdit, handleQuickEditSave,
    scriptEditNodeId, setScriptEditNodeId,
    findReplaceOpen, setFindReplaceOpen,
    searchOpen, setSearchOpen, searchInput, setSearchInput, searchResults, jumpToNode, jumpToEdge,
    quickSwitcherOpen, setQuickSwitcherOpen,
    commandPaletteOpen, setCommandPaletteOpen,
    helpOpen, setHelpOpen,
    diffOpen, setDiffOpen,
    lintPanelOpen, setLintPanelOpen, lintResult,
    prePublishOpen, prePublishLint, onConfirmPrePublish, onCancelPrePublish, onRequestPublish,
    roleCanWrite, canWrite, isAdmin, isLockedByMe, isLockedByOther,
    isDirty, onSave, isSaving, handleRunClick, liveExecution,
    onLock, isLocking, onUnlock, isUnlocking, isPublishing,
    isEnabling, isDisabling, onForceUnlock, isForceUnlocking,
    undo, redo, copySelection, pasteBuffer, groupSelection,
    selected, deleteNodeById, selectAll, navigateNode,
    tidyLayout, isTidying, layoutMode, restoreOrigLayout, hasOrigLayout,
    simulation, runSimulation, clearSimulation,
    hiddenActivityTypes, setHiddenActivityTypes,
    toggleFullscreen, toggleQuickSwitcher, zoomToSelection, fitViewAll,
    machineColoringEnabled, failureHeatmapEnabled,
    exportPng, exportJson, navigate,
  } = props;

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
            onExecute={onRunWithParams}
            onCancel={onCloseRunDialog}
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
          nodes={nodes}
          edges={edges}
          onApply={(newNodes, newEdges) => {
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
            roleCanWrite,
            canWrite,
            isAdmin,
            isLockedByMe,
            isLockedByOther,
            workflowIsEnabled: !!workflow?.isEnabled,
            hasWorkflow: !!id,
            // Save / Run
            isDirty,
            save: onSave, isSaving,
            handleRunClick,
            liveExecutionRunning: liveExecution?.status === 'Running',
            cancelRunningExecution: () => {
              if (liveExecution?.executionId) {
                api.post(`/executions/${liveExecution.executionId}/cancel`, {}).catch(() => { /* ignore */ });
              }
            },
            // Edit-Lock lifecycle
            lock: onLock, isLocking, unlock: onUnlock, isUnlocking, isPublishing,
            isEnabling, isDisabling, forceUnlock: onForceUnlock, isForceUnlocking,
            requestPublish: onRequestPublish,
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
            tidyLayout, isTidying, layoutMode,
            restoreOrigLayout, hasOrigLayout,
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
          results={searchResults}
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
      {prePublishOpen && (
        <PrePublishChecklistModal
          result={prePublishLint}
          nodes={nodes}
          workflowName={name}
          isPending={isPublishing || isEnabling}
          onConfirm={onConfirmPrePublish}
          onCancel={onCancelPrePublish}
          onJumpToNode={(nodeId) => {
            const n = nodes.find((x) => x.id === nodeId);
            if (n) {
              jumpToNode(n);
              onCancelPrePublish();
            }
          }}
          onJumpToEdge={(edgeId) => {
            const e = edges.find((x) => x.id === edgeId);
            if (e) {
              jumpToEdge(e);
              onCancelPrePublish();
            }
          }}
        />
      )}
    </>
  );
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
  const handleAiGenerate = useAiScriptStream({ workflowId, stepId: node?.id, upstreamVars });

  if (!node) return null;

  const psVars = upstreamVars
    .filter((v) => v.expression.includes('.param.'))
    .map((v) => ({ name: '$' + v.variable.split('.').pop()!, label: v.label }));
  const upstreamRefs = upstreamVars.map((v) => ({ expression: v.expression, label: v.label }));
  const outputVariableName = (data.outputVariable as string) || undefined;

  // Live (possibly unsaved) config so the step-test reflects the editor, not the last-saved DB state.
  const runStepTest = async (): Promise<StepTestResult> =>
    api.post<StepTestResult>(`/workflows/${workflowId}/steps/${node.id}/test`, { configOverride: config });

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
        onAiGenerate={handleAiGenerate}
      />
    </Suspense>
  );
}
