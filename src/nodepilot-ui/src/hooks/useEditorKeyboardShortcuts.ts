import { useEffect } from 'react';
import type { DesignerMode } from '../stores/designStore';

const SCRIPT_EDITOR_DIALOG_SELECTOR = '[data-nodepilot-script-editor-dialog="true"]';

interface EditorShortcutsOptions {
  designerMode: DesignerMode;
  undo: () => void;
  redo: () => void;
  copySelection: () => void;
  pasteBuffer: () => void;
  groupSelection: () => void;
  selectAll: () => void;
  zoomToSelection: () => void;
  navigateNode: (direction: 'next' | 'prev') => void;
  searchOpen: boolean;
  setSearchOpen: (open: boolean) => void;
  setSearchInput: (input: string) => void;
  helpOpen: boolean;
  setHelpOpen: (open: boolean | ((o: boolean) => boolean)) => void;
  findReplaceOpen: boolean;
  setFindReplaceOpen: (open: boolean) => void;
  /** True while an edge's target end is detached and waiting for a node click. */
  edgeDetachActive: boolean;
  cancelEdgeDetach: () => void;
  toggleFullscreen: () => void;
  toggleQuickSwitcher: () => void;
  toggleCommandPalette: () => void;
  // Lifecycle, save and run shortcuts. Each callback self-gates against the current state
  // (`triggerSave` does nothing unless this user holds the lock and the workflow is dirty),
  // so the hook only routes the key combo instead of repeating those conditions.
  triggerSave: () => void;
  triggerLock: () => void;
  triggerUnlock: () => void;
  triggerForceUnlock: () => void;
  triggerPublish: () => void;
  triggerTest: () => void;
  triggerDebug: () => void;
  triggerCancel: () => void;
  triggerTidy: () => void;
  toggleLintPanel: () => void;
  // Layout
  restoreOrigLayout: () => void;
  setDiffOpen: (open: boolean) => void;
  triggerSimulation: () => void;
  clearActivityTypeFilter: () => void;
  // Style and canvas-view toggles, bound to single letters: A animation, R routing,
  // M machines, H heatmap, G grid. They fire only when no input or textarea has focus, so
  // typing into a search or label field never triggers them by accident.
  toggleEdgesAnimated: () => void;
  cycleEdgeRouting: () => void;
  edgeWidthInc: () => void;
  edgeWidthDec: () => void;
  toggleNodeStyle: () => void;
  nodeSizeInc: () => void;
  nodeSizeDec: () => void;
  labelFontInc: () => void;
  labelFontDec: () => void;
  toggleMachineColoring: () => void;
  toggleFailureHeatmap: () => void;
  toggleCriticalPath: () => void;
  toggleSnapToGrid: () => void;
  // Per-node quick toggles applied to the currently selected Activity nodes: `D` flips
  // `disabled`, `B` flips `breakpoint`. Both do nothing when no Activity node is selected,
  // because the WorkflowEditor implementation gates on the selection and keeps this hook
  // stateless. M (mute) is not bound: it would collide with the machine-coloring toggle, and
  // mute has no meaning in the engine beyond `disabled`.
  toggleSelectedDisabled: () => void;
  toggleSelectedBreakpoint: () => void;
  // Arrow-key nudge: moves all selected nodes by the given pixel delta. Shift = fine (1px).
  nudgeSelectedNodes: (dx: number, dy: number) => void;
  // Fit all nodes into view.
  fitViewAll: () => void;
  // Export
  exportJson: () => void;
  exportPng: () => void;
  // Navigate
  navigate: (to: string) => void;
}

export function useEditorKeyboardShortcuts({
  designerMode,
  undo, redo, copySelection, pasteBuffer, groupSelection,
  selectAll, zoomToSelection, navigateNode,
  searchOpen, setSearchOpen, setSearchInput,
  helpOpen, setHelpOpen,
  findReplaceOpen, setFindReplaceOpen,
  edgeDetachActive, cancelEdgeDetach,
  toggleFullscreen, toggleQuickSwitcher, toggleCommandPalette,
  triggerSave, triggerLock, triggerUnlock, triggerForceUnlock,
  triggerPublish, triggerTest, triggerDebug, triggerCancel,
  triggerTidy, toggleLintPanel,
  restoreOrigLayout, setDiffOpen, triggerSimulation, clearActivityTypeFilter,
  toggleEdgesAnimated, cycleEdgeRouting, edgeWidthInc, edgeWidthDec,
  toggleNodeStyle, nodeSizeInc, nodeSizeDec, labelFontInc, labelFontDec,
  toggleMachineColoring, toggleFailureHeatmap, toggleCriticalPath, toggleSnapToGrid,
  toggleSelectedDisabled, toggleSelectedBreakpoint,
  nudgeSelectedNodes, fitViewAll,
  exportJson, exportPng,
  navigate,
}: EditorShortcutsOptions) {
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (typeof document !== 'undefined' && document.querySelector(SCRIPT_EDITOR_DIALOG_SELECTOR)) {
        return;
      }

      const isExpert = designerMode === 'expert';
      const target = e.target as HTMLElement | null;
      const tag = target?.tagName;
      const editable = tag === 'INPUT' || tag === 'TEXTAREA' || target?.isContentEditable;

      // Global '?' opens Help (Shift+/ on US/DE layout). Pass through in input fields.
      if (!editable && e.key === '?' && !e.ctrlKey && !e.metaKey) {
        setHelpOpen((o) => !o);
        e.preventDefault();
        return;
      }
      // Single-letter view toggles. They fire only when no input field has focus and no
      // modifier is held, so typing 'a' into a label or search input does not trigger them.
      // Letters held with Ctrl, Shift or Alt fall through to the modifier shortcuts below.
      if (isExpert && !editable && !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey) {
        switch (e.key) {
          case 'a': case 'A': toggleEdgesAnimated(); e.preventDefault(); return;
          case 'r': case 'R': cycleEdgeRouting(); e.preventDefault(); return;
          case 'm': case 'M': toggleMachineColoring(); e.preventDefault(); return;
          case 'h': case 'H': toggleFailureHeatmap(); e.preventDefault(); return;
          case 'c': case 'C': toggleCriticalPath(); e.preventDefault(); return;
          case 'g': case 'G': toggleSnapToGrid(); e.preventDefault(); return;
          // Per-node toggles. They do nothing when nothing is selected; the callbacks self-gate.
          case 'd': case 'D': toggleSelectedDisabled(); e.preventDefault(); return;
          case 'b': case 'B': toggleSelectedBreakpoint(); e.preventDefault(); return;
        }
      }
      // Escape closes open overlays. A detached edge end takes priority over every overlay:
      // it is a canvas-wide modal state, and leaving it armed while Escape closes something
      // else would strand the user mid-operation.
      if (e.key === 'Escape') {
        if (edgeDetachActive) { cancelEdgeDetach(); e.preventDefault(); return; }
        if (findReplaceOpen) { setFindReplaceOpen(false); e.preventDefault(); return; }
        if (searchOpen) { setSearchOpen(false); setSearchInput(''); e.preventDefault(); return; }
        if (helpOpen) { setHelpOpen(false); e.preventDefault(); return; }
      }
      // Arrow keys: nudge selected nodes. Shift = fine (1px), normal = grid step (10px).
      // Only when no input/textarea has focus.
      if (isExpert && !editable && ['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(e.key)) {
        const step = e.shiftKey ? 1 : 10;
        const dx = e.key === 'ArrowLeft' ? -step : e.key === 'ArrowRight' ? step : 0;
        const dy = e.key === 'ArrowUp' ? -step : e.key === 'ArrowDown' ? step : 0;
        nudgeSelectedNodes(dx, dy);
        e.preventDefault();
        return;
      }
      // Home key: fit all nodes into view.
      if (!editable && e.key === 'Home') {
        fitViewAll();
        e.preventDefault();
        return;
      }
      // Tab / Shift+Tab: navigate to next/prev connected node (only when not in an input field).
      if (isExpert && e.key === 'Tab' && !editable) {
        e.preventDefault();
        navigateNode(e.shiftKey ? 'prev' : 'next');
        return;
      }
      // F11 toggles in-app fullscreen, hiding sidebar and header. It replaces browser fullscreen
      // because the designer gains more from the extra horizontal space.
      if (e.key === 'F11' && !editable) {
        toggleFullscreen();
        e.preventDefault();
        return;
      }
      const mod = e.ctrlKey || e.metaKey;
      if (!mod) return;
      // Ctrl+Shift+P opens the command palette; Ctrl+P without shift opens the quick switcher.
      if (e.key === 'p' || e.key === 'P') {
        if (e.altKey) { exportPng(); e.preventDefault(); return; }
        if (e.shiftKey) toggleCommandPalette();
        else toggleQuickSwitcher();
        e.preventDefault();
        return;
      }
      // Ctrl+F opens search even from input fields and suppresses the browser find bar.
      if ((e.key === 'f' || e.key === 'F') && !e.shiftKey) {
        setSearchOpen(true);
        e.preventDefault();
        return;
      }
      // Ctrl+H opens find and replace.
      if (isExpert && (e.key === 'h' || e.key === 'H') && !e.shiftKey) {
        setFindReplaceOpen(true);
        e.preventDefault();
        return;
      }
      // Ctrl+S saves (an interim save while this user holds the lock). Ctrl+Shift+S publishes,
      // switching between publish, enable and disable depending on workflow and lock state.
      // Both suppress the browser save dialog and fire from input fields too, so an edit can be
      // saved without defocusing first.
      if (e.key === 's' || e.key === 'S') {
        if (e.shiftKey) triggerPublish(); else triggerSave();
        e.preventDefault();
        return;
      }
      if (editable) return;
      if (e.key === 'z' || e.key === 'Z') {
        if (e.shiftKey) redo(); else undo();
        e.preventDefault();
        return;
      }
      if (e.key === 'y' || e.key === 'Y') { redo(); e.preventDefault(); return; }
      if (isExpert && (e.key === 'g' || e.key === 'G')) { groupSelection(); e.preventDefault(); return; }
      if (e.key === 'a' || e.key === 'A') { selectAll(); e.preventDefault(); return; }
      // Ctrl+Shift+E zooms to the selection. Ctrl+E starts editing: it claims the edit lock and
      // disables the workflow in one step, and does nothing when a lock is already held.
      if (e.key === 'e' || e.key === 'E') {
        if (e.shiftKey && isExpert) zoomToSelection(); else if (!e.shiftKey) triggerLock();
        e.preventDefault();
        return;
      }
      // Ctrl+U releases the edit lock. Ctrl+Shift+U force-unlocks, which requires Admin.
      if (e.key === 'u' || e.key === 'U') {
        if (e.shiftKey && isExpert) triggerForceUnlock(); else if (!e.shiftKey) triggerUnlock();
        e.preventDefault();
        return;
      }
      // Ctrl+Enter starts a test run, Ctrl+Shift+Enter a debug run. Both do nothing while a run
      // is already in flight; the handler checks liveExecution itself.
      if (e.key === 'Enter') {
        if (e.shiftKey && isExpert) triggerDebug(); else if (!e.shiftKey) triggerTest();
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+X cancels the running execution.
      if ((e.key === 'x' || e.key === 'X') && e.shiftKey && !e.altKey) {
        triggerCancel();
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+T runs the tidy auto-layout, Ctrl+Shift+L toggles the lint panel. Both
      // suppress the browser defaults, restore-tab and focus-search-bar.
      if ((e.key === 't' || e.key === 'T') && e.shiftKey) {
        triggerTidy();
        e.preventDefault();
        return;
      }
      if ((e.key === 'l' || e.key === 'L') && e.shiftKey) {
        toggleLintPanel();
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+O restores the original layout.
      if (isExpert && (e.key === 'o' || e.key === 'O') && e.shiftKey) {
        restoreOrigLayout();
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+D opens the diff against a version.
      if (isExpert && (e.key === 'd' || e.key === 'D') && e.shiftKey) {
        setDiffOpen(true);
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+R runs or clears the dry-run simulation.
      if (isExpert && (e.key === 'r' || e.key === 'R') && e.shiftKey) {
        triggerSimulation();
        e.preventDefault();
        return;
      }
      // Ctrl+Alt+X clears the activity-type filter.
      if (isExpert && (e.key === 'x' || e.key === 'X') && e.altKey) {
        clearActivityTypeFilter();
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+N switches between the Classic and Card node view.
      if (isExpert && (e.key === 'n' || e.key === 'N') && e.shiftKey) {
        toggleNodeStyle();
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+J exports the workflow as JSON.
      if (isExpert && (e.key === 'j' || e.key === 'J') && e.shiftKey) {
        exportJson();
        e.preventDefault();
        return;
      }
      // Ctrl+] and Ctrl+[ increase and decrease the edge width.
      if (isExpert && e.key === ']') { edgeWidthInc(); e.preventDefault(); return; }
      if (isExpert && e.key === '[') { edgeWidthDec(); e.preventDefault(); return; }
      // Ctrl+Shift+. (> key) and Ctrl+Shift+, (< key) increase and decrease the node size.
      if (isExpert && e.shiftKey && e.key === '>') { nodeSizeInc(); e.preventDefault(); return; }
      if (isExpert && e.shiftKey && e.key === '<') { nodeSizeDec(); e.preventDefault(); return; }
      // Ctrl+Alt+. and Ctrl+Alt+, increase and decrease the label font size.
      if (isExpert && e.altKey && e.key === '.') { labelFontInc(); e.preventDefault(); return; }
      if (isExpert && e.altKey && e.key === ',') { labelFontDec(); e.preventDefault(); return; }
      // Ctrl+Shift+1..5 navigate to Workflows, Executions, Machines, Globals and Audit.
      if (isExpert && e.shiftKey && e.key >= '1' && e.key <= '5') {
        const routes = ['/workflows', '/executions', '/machines', '/global-variables', '/audit'];
        const idx = Number(e.key) - 1;
        if (idx < routes.length) navigate(routes[idx]);
        e.preventDefault();
        return;
      }
      if (e.key === 'c' || e.key === 'C') { copySelection(); }
      else if (e.key === 'v' || e.key === 'V') { pasteBuffer(); e.preventDefault(); }
      else if ((e.key === 'd' || e.key === 'D') && !e.shiftKey) { copySelection(); pasteBuffer(); e.preventDefault(); }
    };
    globalThis.addEventListener('keydown', onKeyDown);
    return () => globalThis.removeEventListener('keydown', onKeyDown);
  }, [
    designerMode, undo, redo, copySelection, pasteBuffer, groupSelection, selectAll, zoomToSelection,
    navigateNode, searchOpen, setSearchOpen, setSearchInput, helpOpen, setHelpOpen,
    findReplaceOpen, setFindReplaceOpen, edgeDetachActive, cancelEdgeDetach,
    toggleFullscreen, toggleQuickSwitcher, toggleCommandPalette,
    triggerSave, triggerLock, triggerUnlock, triggerForceUnlock,
    triggerPublish, triggerTest, triggerDebug, triggerCancel,
    triggerTidy, toggleLintPanel,
    restoreOrigLayout, setDiffOpen, triggerSimulation, clearActivityTypeFilter,
    toggleEdgesAnimated, cycleEdgeRouting, edgeWidthInc, edgeWidthDec,
    toggleNodeStyle, nodeSizeInc, nodeSizeDec, labelFontInc, labelFontDec,
    toggleMachineColoring, toggleFailureHeatmap, toggleCriticalPath, toggleSnapToGrid,
    toggleSelectedDisabled, toggleSelectedBreakpoint,
    nudgeSelectedNodes, fitViewAll,
    exportJson, exportPng,
    navigate,
  ]);
}
