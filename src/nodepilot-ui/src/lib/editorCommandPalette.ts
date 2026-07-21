import type { PaletteCommand } from '../components/designer/overlays/CommandPalette';
import type { DesignerMode } from '../stores/designStore';
import { confirmDialog } from '../stores/confirmStore';
import i18n from '../i18n';

export function filterCommandsForDesignerMode(
  commands: PaletteCommand[],
  mode: DesignerMode,
): PaletteCommand[] {
  if (mode === 'expert') return commands;
  return commands.filter((command) => !command.expertOnly && command.group !== 'Style');
}

/**
 * Aggregates every designer action with a stable handle into a flat list for the Command
 * Palette. Kept as a pure builder so the palette overlay stays decoupled from the editor's
 * state shape. Disabled-states surface as muted rows rather than missing entries —
 * discoverability over noise. The intent is "everything the toolbar / keyboard can do".
 */
export interface CommandPaletteContext {
  // Role + lock state
  roleCanWrite: boolean;
  canWrite: boolean;            // role-can-write AND lock-by-me
  isAdmin: boolean;
  isLockedByMe: boolean;
  isLockedByOther: boolean;
  workflowIsEnabled: boolean;
  hasWorkflow: boolean;
  // Save / Run
  isDirty: boolean;
  save: () => void;
  isSaving: boolean;
  handleRunClick: (debug?: boolean) => void;
  liveExecutionRunning: boolean;
  cancelRunningExecution: () => void;
  // Edit-Lock lifecycle. Commands + pending flags (no raw mutations) — see useWorkflowLock.
  lock: () => void;
  isLocking: boolean;
  unlock: () => void;
  isUnlocking: boolean;
  isPublishing: boolean;
  isEnabling: boolean;
  isDisabling: boolean;
  forceUnlock: () => void;
  isForceUnlocking: boolean;
  /** Routes Publish/Enable through the pre-publish checklist (page-level). Disable still
   *  goes direct via the page handler — same rule as the toolbar toggle. */
  requestPublish: () => void;
  // Edit operations
  undo: () => void;
  redo: () => void;
  copySelection: () => void;
  pasteBuffer: () => void;
  groupSelection: () => void;
  deleteSelected: () => void;
  selectAll: () => void;
  navigateNode: (dir: 'next' | 'prev') => void;
  // Overlays / panels
  setSearchOpen: (open: boolean) => void;
  setFindReplaceOpen: (open: boolean) => void;
  setHelpOpen: (v: boolean | ((p: boolean) => boolean)) => void;
  setDiffOpen: (open: boolean) => void;
  setLintPanelOpen: (fn: (open: boolean) => boolean) => void;
  // Layout
  tidyLayout: () => void;
  isTidying: boolean;
  layoutMode: string;
  restoreOrigLayout: () => void;
  hasOrigLayout: boolean;
  // Simulation
  simulationActive: boolean;
  runSimulation: () => void;
  clearSimulation: () => void;
  // Filter
  hiddenActivityTypesCount: number;
  clearActivityTypeFilter: () => void;
  // View
  toggleFullscreen: () => void;
  toggleQuickSwitcher: () => void;
  zoomToSelection: () => void;
  machineColoringEnabled: boolean;
  failureHeatmapEnabled: boolean;
  toggleMachineColoring: () => void;
  toggleFailureHeatmap: () => void;
  criticalPathEnabled: boolean;
  toggleCriticalPath: () => void;
  fitViewAll: () => void;
  // Designer style (DesignStore)
  edgesAnimated: boolean;
  toggleEdgesAnimated: () => void;
  edgeRouting: string;
  cycleEdgeRouting: () => void;
  edgeWidthInc: () => void;
  edgeWidthDec: () => void;
  nodeStyle: string;
  toggleNodeStyle: () => void;
  zoomIn: () => void;
  zoomOut: () => void;
  labelFontInc: () => void;
  labelFontDec: () => void;
  snapToGrid: boolean;
  setSnapToGrid: (v: boolean) => void;
  // Persistence / Export
  exportPng: () => void;
  exportJson: () => void;
  // Navigate
  navigate: (to: string) => void;
}

export function buildCommandPaletteCommands(ctx: CommandPaletteContext): PaletteCommand[] {
  // `group` stays a literal English logic key (drives mode filtering + icon/header lookup);
  // only the user-visible title/subtitle are translated.
  const t = (key: string, opts?: Record<string, unknown>) => i18n.t(`editor:${key}`, opts ?? {}) as string;
  // Smart Publish/Disable button mirrors the toolbar toggle: same routing, same disabled rules.
  const publishBusy = ctx.isPublishing || ctx.isEnabling;
  return [
    // -------- Lifecycle (Edit-Lock + Run-State) --------
    {
      id: 'lock-edit', group: 'Lifecycle',
      title: t('palette.lockEditTitle'),
      subtitle: t('palette.lockEditSubtitle'),
      shortcut: 'Ctrl+E',
      disabled: !ctx.roleCanWrite || ctx.isLockedByMe || ctx.isLockedByOther || ctx.isLocking,
      run: () => ctx.lock(),
    },
    {
      id: 'lock-end', group: 'Lifecycle',
      title: t('palette.lockEndTitle'),
      subtitle: t('palette.lockEndSubtitle'),
      shortcut: 'Ctrl+U',
      disabled: !ctx.isLockedByMe || ctx.isUnlocking,
      run: () => ctx.unlock(),
    },
    {
      id: 'publish', group: 'Lifecycle',
      title: ctx.workflowIsEnabled
        ? t('palette.publishDisableTitle')
        : ctx.isLockedByMe
          ? t('palette.publishAtomicTitle')
          : t('palette.publishReenableTitle'),
      subtitle: ctx.isLockedByOther
        ? t('palette.publishLockedSubtitle')
        : ctx.workflowIsEnabled
          ? t('palette.publishDisableSubtitle')
          : ctx.isLockedByMe
            ? t('palette.publishAtomicSubtitle')
            : t('palette.publishReenableSubtitle'),
      shortcut: 'Ctrl+Shift+S',
      disabled: !ctx.roleCanWrite || ctx.isLockedByOther || publishBusy || ctx.isDisabling,
      // Routed via requestPublish so the pre-publish modal intercepts non-clean lints. The
      // page-level handler also owns the Disable confirm for parity with the toolbar.
      run: () => ctx.requestPublish(),
    },
    {
      id: 'force-unlock', group: 'Lifecycle', expertOnly: true,
      title: t('palette.forceUnlockTitle'),
      subtitle: t('palette.forceUnlockSubtitle'),
      shortcut: 'Ctrl+Shift+U',
      disabled: !ctx.isAdmin || !ctx.isLockedByOther || ctx.isForceUnlocking,
      // PaletteCommand.run is fire-and-forget (`() => void`): the palette closes itself
      // right after invoking, and the store-driven confirmDialog then carries the decision.
      run: () => {
        void confirmDialog(t('palette.forceUnlockConfirm')).then((ok) => {
          if (ok) ctx.forceUnlock();
        });
      },
    },

    // -------- Run --------
    { id: 'run-test', group: 'Run', title: t('palette.runTestTitle'), subtitle: t('palette.runTestSubtitle'), shortcut: 'Ctrl+Enter', disabled: !ctx.roleCanWrite || ctx.liveExecutionRunning, run: () => ctx.handleRunClick(false) },
    { id: 'run-debug', group: 'Run', expertOnly: true, title: t('palette.runDebugTitle'), subtitle: t('palette.runDebugSubtitle'), shortcut: 'Ctrl+Shift+Enter', disabled: !ctx.roleCanWrite || ctx.liveExecutionRunning, run: () => ctx.handleRunClick(true) },
    { id: 'run-cancel', group: 'Run', title: t('palette.runCancelTitle'), subtitle: t('palette.runCancelSubtitle'), shortcut: 'Ctrl+Shift+X', disabled: !ctx.liveExecutionRunning, run: () => ctx.cancelRunningExecution() },
    { id: 'save', group: 'Run', title: t('palette.saveTitle'), subtitle: t('palette.saveSubtitle'), shortcut: 'Ctrl+S', disabled: !ctx.canWrite || !ctx.isDirty || ctx.isSaving, run: () => ctx.save() },

    // -------- Edit / Clipboard / Selection --------
    { id: 'undo', group: 'Edit', title: t('palette.undoTitle'), shortcut: 'Ctrl+Z', run: ctx.undo },
    { id: 'redo', group: 'Edit', title: t('palette.redoTitle'), shortcut: 'Ctrl+Y', run: ctx.redo },
    { id: 'copy', group: 'Edit', title: t('palette.copyTitle'), subtitle: t('palette.copySubtitle'), shortcut: 'Ctrl+C', run: ctx.copySelection },
    { id: 'paste', group: 'Edit', title: t('palette.pasteTitle'), subtitle: t('palette.pasteSubtitle'), shortcut: 'Ctrl+V', disabled: !ctx.canWrite, run: ctx.pasteBuffer },
    { id: 'duplicate', group: 'Edit', title: t('palette.duplicateTitle'), subtitle: t('palette.duplicateSubtitle'), shortcut: 'Ctrl+D', disabled: !ctx.canWrite, run: () => { ctx.copySelection(); ctx.pasteBuffer(); } },
    { id: 'group', group: 'Edit', expertOnly: true, title: t('palette.groupTitle'), subtitle: t('palette.groupSubtitle'), shortcut: 'Ctrl+G', disabled: !ctx.canWrite, run: ctx.groupSelection },
    { id: 'select-all', group: 'Edit', title: t('palette.selectAllTitle'), shortcut: 'Ctrl+A', run: ctx.selectAll },
    { id: 'delete-sel', group: 'Edit', title: t('palette.deleteTitle'), subtitle: t('palette.deleteSubtitle'), shortcut: 'Del', disabled: !ctx.canWrite, run: ctx.deleteSelected },
    { id: 'nav-next', group: 'Edit', expertOnly: true, title: t('palette.navNextTitle'), shortcut: 'Tab', run: () => ctx.navigateNode('next') },
    { id: 'nav-prev', group: 'Edit', expertOnly: true, title: t('palette.navPrevTitle'), shortcut: 'Shift+Tab', run: () => ctx.navigateNode('prev') },
    { id: 'find', group: 'Edit', title: t('palette.findTitle'), subtitle: t('palette.findSubtitle'), shortcut: 'Ctrl+F', run: () => ctx.setSearchOpen(true) },
    { id: 'find-replace', group: 'Edit', expertOnly: true, title: t('palette.findReplaceTitle'), subtitle: t('palette.findReplaceSubtitle'), shortcut: 'Ctrl+H', run: () => ctx.setFindReplaceOpen(true) },

    // -------- Layout / Inspect --------
    { id: 'tidy', group: 'Layout', title: t('palette.tidyTitle', { mode: ctx.layoutMode }), subtitle: t('palette.tidySubtitle'), shortcut: 'Ctrl+Shift+T', disabled: !ctx.canWrite || ctx.isTidying, run: ctx.tidyLayout },
    { id: 'orig', group: 'Layout', expertOnly: true, title: t('palette.origTitle'), subtitle: t('palette.origSubtitle'), shortcut: 'Ctrl+Shift+O', disabled: !ctx.hasOrigLayout, run: ctx.restoreOrigLayout },
    { id: 'diff', group: 'Layout', expertOnly: true, title: t('palette.diffTitle'), subtitle: t('palette.diffSubtitle'), shortcut: 'Ctrl+Shift+D', disabled: !ctx.hasWorkflow, run: () => ctx.setDiffOpen(true) },
    {
      id: 'sim', group: 'Layout', expertOnly: true,
      title: ctx.simulationActive ? t('palette.simClearTitle') : t('palette.simRunTitle'),
      subtitle: ctx.simulationActive ? t('palette.simClearSubtitle') : t('palette.simRunSubtitle'),
      shortcut: 'Ctrl+Shift+R',
      run: () => ctx.simulationActive ? ctx.clearSimulation() : ctx.runSimulation(),
    },
    { id: 'lint', group: 'Layout', title: t('palette.lintTitle'), subtitle: t('palette.lintSubtitle'), shortcut: 'Ctrl+Shift+L', run: () => ctx.setLintPanelOpen((o) => !o) },
    { id: 'filter-clear', group: 'Layout', expertOnly: true, title: t('palette.filterClearTitle'), subtitle: t('palette.filterClearSubtitle', { count: ctx.hiddenActivityTypesCount }), shortcut: 'Ctrl+Alt+X', disabled: ctx.hiddenActivityTypesCount === 0, run: ctx.clearActivityTypeFilter },

    // -------- Designer style (DesignStore-driven) --------
    { id: 'style-edges-anim', group: 'Style', title: ctx.edgesAnimated ? t('palette.edgesAnimDisable') : t('palette.edgesAnimEnable'), shortcut: 'A', run: ctx.toggleEdgesAnimated },
    { id: 'style-edge-routing', group: 'Style', title: t('palette.edgeRoutingTitle', { routing: ctx.edgeRouting }), subtitle: t('palette.edgeRoutingSubtitle'), shortcut: 'R', run: ctx.cycleEdgeRouting },
    { id: 'style-edge-thicker', group: 'Style', title: t('palette.edgeThickerTitle'), shortcut: 'Ctrl+]', run: ctx.edgeWidthInc },
    { id: 'style-edge-thinner', group: 'Style', title: t('palette.edgeThinnerTitle'), shortcut: 'Ctrl+[', run: ctx.edgeWidthDec },
    { id: 'style-node-toggle', group: 'Style', title: ctx.nodeStyle === 'classic' ? t('palette.nodeToCard') : t('palette.nodeToClassic'), subtitle: t('palette.nodeToggleSubtitle'), shortcut: 'Ctrl+Shift+N', run: ctx.toggleNodeStyle },
    { id: 'style-node-larger', group: 'Style', title: t('palette.nodeLargerTitle'), shortcut: 'Ctrl+Shift+>', disabled: ctx.nodeStyle !== 'classic', run: ctx.zoomIn },
    { id: 'style-node-smaller', group: 'Style', title: t('palette.nodeSmallerTitle'), shortcut: 'Ctrl+Shift+<', disabled: ctx.nodeStyle !== 'classic', run: ctx.zoomOut },
    { id: 'style-label-larger', group: 'Style', title: t('palette.labelLargerTitle'), shortcut: 'Ctrl+Alt+.', disabled: ctx.nodeStyle !== 'classic', run: ctx.labelFontInc },
    { id: 'style-label-smaller', group: 'Style', title: t('palette.labelSmallerTitle'), shortcut: 'Ctrl+Alt+,', disabled: ctx.nodeStyle !== 'classic', run: ctx.labelFontDec },
    { id: 'style-grid-snap', group: 'Style', title: ctx.snapToGrid ? t('palette.gridSnapDisable') : t('palette.gridSnapEnable'), shortcut: 'G', run: () => ctx.setSnapToGrid(!ctx.snapToGrid) },
    { id: 'style-machines', group: 'Style', title: ctx.machineColoringEnabled ? t('palette.machinesHide') : t('palette.machinesShow'), subtitle: t('palette.machinesSubtitle'), shortcut: 'M', run: ctx.toggleMachineColoring },
    { id: 'style-heatmap', group: 'Style', title: ctx.failureHeatmapEnabled ? t('palette.heatmapHide') : t('palette.heatmapShow'), subtitle: t('palette.heatmapSubtitle'), shortcut: 'H', run: ctx.toggleFailureHeatmap },
    { id: 'style-critical-path', group: 'Style', title: ctx.criticalPathEnabled ? t('palette.criticalPathHide') : t('palette.criticalPathShow'), subtitle: t('palette.criticalPathSubtitle'), shortcut: 'C', run: ctx.toggleCriticalPath },

    // -------- View --------
    { id: 'fullscreen', group: 'View', title: t('palette.fullscreenTitle'), subtitle: t('palette.fullscreenSubtitle'), shortcut: 'F11', run: ctx.toggleFullscreen },
    { id: 'zoom-sel', group: 'View', expertOnly: true, title: t('palette.zoomSelTitle'), subtitle: t('palette.zoomSelSubtitle'), shortcut: 'Ctrl+Shift+E', run: ctx.zoomToSelection },
    { id: 'zoom-fit', group: 'View', title: t('palette.zoomFitTitle'), subtitle: t('palette.zoomFitSubtitle'), shortcut: 'Home', run: ctx.fitViewAll },

    // -------- Persist / Export --------
    { id: 'export-json', group: 'Export', title: t('palette.exportJsonTitle'), subtitle: t('palette.exportJsonSubtitle'), shortcut: 'Ctrl+Shift+J', disabled: !ctx.hasWorkflow, run: ctx.exportJson },
    { id: 'export-png', group: 'Export', title: t('palette.exportPngTitle'), subtitle: t('palette.exportPngSubtitle'), shortcut: 'Ctrl+Alt+P', run: ctx.exportPng },

    // -------- Navigate --------
    { id: 'switcher', group: 'Navigate', title: t('palette.switcherTitle'), subtitle: t('palette.switcherSubtitle'), shortcut: 'Ctrl+P', run: ctx.toggleQuickSwitcher },
    { id: 'nav-workflows', group: 'Navigate', title: t('palette.navWorkflowsTitle'), shortcut: 'Ctrl+Shift+1', run: () => ctx.navigate('/workflows') },
    { id: 'nav-executions', group: 'Navigate', expertOnly: true, title: t('palette.navExecutionsTitle'), shortcut: 'Ctrl+Shift+2', run: () => ctx.navigate('/executions') },
    { id: 'nav-machines', group: 'Navigate', expertOnly: true, title: t('palette.navMachinesTitle'), shortcut: 'Ctrl+Shift+3', run: () => ctx.navigate('/machines') },
    { id: 'nav-globals', group: 'Navigate', expertOnly: true, title: t('palette.navGlobalsTitle'), shortcut: 'Ctrl+Shift+4', run: () => ctx.navigate('/global-variables') },
    { id: 'nav-audit', group: 'Navigate', expertOnly: true, title: t('palette.navAuditTitle'), shortcut: 'Ctrl+Shift+5', run: () => ctx.navigate('/audit') },

    // -------- Help --------
    { id: 'help', group: 'Help', title: t('palette.helpTitle'), shortcut: '?', run: () => ctx.setHelpOpen(true) },
  ];
}
