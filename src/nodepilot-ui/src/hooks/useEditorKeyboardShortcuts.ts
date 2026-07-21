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
  toggleFullscreen: () => void;
  toggleQuickSwitcher: () => void;
  toggleCommandPalette: () => void;
  // Lifecycle / save / run shortcuts. Each callback self-gates against the current state
  // (e.g. `triggerSave` does nothing if no lock-by-me + not dirty) — the hook just routes
  // the key combo without re-implementing the same conditions a second time.
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
  // Style / canvas-view toggles. Single-letter shortcuts (Figma-style) — fire only when no
  // input/textarea has focus, so a user typing into a search or label field never triggers
  // them by accident. Bound to A / R / M / H / G for animation / routing / machines /
  // heatmap / grid respectively.
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
  // Per-node quick toggles applied to whichever Activity nodes are currently selected.
  // `D` flips `disabled`, `B` flips `breakpoint`. Both no-op when no Activity nodes are
  // selected — the WorkflowEditor implementation self-gates against the selection set so
  // the hook stays stateless. M (mute) is intentionally NOT bound here because it would
  // collide with the existing M=machine-coloring toggle and "mute" has no semantic distinct
  // from `disabled` in the engine.
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
      // Single-letter view toggles (Figma-style). Fire only when no input field has focus
      // and no modifier is held — a user typing 'a' into a label or search input must never
      // trigger these. Ctrl/Shift/Alt-modified letters fall through to the modifier-based
      // shortcuts below.
      if (isExpert && !editable && !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey) {
        switch (e.key) {
          case 'a': case 'A': toggleEdgesAnimated(); e.preventDefault(); return;
          case 'r': case 'R': cycleEdgeRouting(); e.preventDefault(); return;
          case 'm': case 'M': toggleMachineColoring(); e.preventDefault(); return;
          case 'h': case 'H': toggleFailureHeatmap(); e.preventDefault(); return;
          case 'c': case 'C': toggleCriticalPath(); e.preventDefault(); return;
          case 'g': case 'G': toggleSnapToGrid(); e.preventDefault(); return;
          // Per-node toggles. No-op when nothing's selected (self-gated in the implementation).
          case 'd': case 'D': toggleSelectedDisabled(); e.preventDefault(); return;
          case 'b': case 'B': toggleSelectedBreakpoint(); e.preventDefault(); return;
        }
      }
      // Escape closes open overlays first.
      if (e.key === 'Escape') {
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
      // F11: in-app fullscreen (hide sidebar/header). Replaces browser fullscreen on purpose —
      // designer benefits more from extra horizontal space than from OS-level fullscreen.
      if (e.key === 'F11' && !editable) {
        toggleFullscreen();
        e.preventDefault();
        return;
      }
      const mod = e.ctrlKey || e.metaKey;
      if (!mod) return;
      // Ctrl+Shift+P opens Command Palette; Ctrl+P (without shift) opens Quick-Switcher.
      if (e.key === 'p' || e.key === 'P') {
        if (e.altKey) { exportPng(); e.preventDefault(); return; }
        if (e.shiftKey) toggleCommandPalette();
        else toggleQuickSwitcher();
        e.preventDefault();
        return;
      }
      // Ctrl+F opens Search even from input fields (suppresses browser find).
      if ((e.key === 'f' || e.key === 'F') && !e.shiftKey) {
        setSearchOpen(true);
        e.preventDefault();
        return;
      }
      // Ctrl+H opens Find & Replace.
      if (isExpert && (e.key === 'h' || e.key === 'H') && !e.shiftKey) {
        setFindReplaceOpen(true);
        e.preventDefault();
        return;
      }
      // Ctrl+S → Save (interim-save while lock-by-me). Ctrl+Shift+S → Publish (smart toggle:
      // Publish/Enable/Disable depending on workflow + lock state). Both prevent the browser's
      // "save page as…" dialog. Fire even from input fields so a user mid-edit can save without
      // first defocusing.
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
      // Ctrl+Shift+E → Zoom to selection (existing). Ctrl+E → Bearbeiten (new): claim the
      // edit lock + atomic Disable. Self-gated; no-op if already locked by me/other.
      if (e.key === 'e' || e.key === 'E') {
        if (e.shiftKey && isExpert) zoomToSelection(); else if (!e.shiftKey) triggerLock();
        e.preventDefault();
        return;
      }
      // Ctrl+U → Unlock (release edit lock). Ctrl+Shift+U → Force unlock (Admin).
      if (e.key === 'u' || e.key === 'U') {
        if (e.shiftKey && isExpert) triggerForceUnlock(); else if (!e.shiftKey) triggerUnlock();
        e.preventDefault();
        return;
      }
      // Ctrl+Enter → Test run; Ctrl+Shift+Enter → Debug run. Both no-op when a run is
      // already in flight (handler self-checks against liveExecution).
      if (e.key === 'Enter') {
        if (e.shiftKey && isExpert) triggerDebug(); else if (!e.shiftKey) triggerTest();
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+X → Cancel running execution.
      if ((e.key === 'x' || e.key === 'X') && e.shiftKey && !e.altKey) {
        triggerCancel();
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+T → Tidy auto-layout. Ctrl+Shift+L → Lint panel toggle. Both prevent
      // the browser defaults (restore-tab / focus-search-bar respectively).
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
      // Ctrl+Shift+O → Restore original layout.
      if (isExpert && (e.key === 'o' || e.key === 'O') && e.shiftKey) {
        restoreOrigLayout();
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+D → Diff against version.
      if (isExpert && (e.key === 'd' || e.key === 'D') && e.shiftKey) {
        setDiffOpen(true);
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+R → Run/clear dry-run simulation.
      if (isExpert && (e.key === 'r' || e.key === 'R') && e.shiftKey) {
        triggerSimulation();
        e.preventDefault();
        return;
      }
      // Ctrl+Alt+X → Clear activity-type filter.
      if (isExpert && (e.key === 'x' || e.key === 'X') && e.altKey) {
        clearActivityTypeFilter();
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+N → Toggle Classic/Card node view.
      if (isExpert && (e.key === 'n' || e.key === 'N') && e.shiftKey) {
        toggleNodeStyle();
        e.preventDefault();
        return;
      }
      // Ctrl+Shift+J → Export workflow as JSON.
      if (isExpert && (e.key === 'j' || e.key === 'J') && e.shiftKey) {
        exportJson();
        e.preventDefault();
        return;
      }
      // Ctrl+] / Ctrl+[ → Edge width increase / decrease.
      if (isExpert && e.key === ']') { edgeWidthInc(); e.preventDefault(); return; }
      if (isExpert && e.key === '[') { edgeWidthDec(); e.preventDefault(); return; }
      // Ctrl+Shift+. (> key) / Ctrl+Shift+, (< key) → Node size increase / decrease.
      if (isExpert && e.shiftKey && e.key === '>') { nodeSizeInc(); e.preventDefault(); return; }
      if (isExpert && e.shiftKey && e.key === '<') { nodeSizeDec(); e.preventDefault(); return; }
      // Ctrl+Alt+. / Ctrl+Alt+, → Label font increase / decrease.
      if (isExpert && e.altKey && e.key === '.') { labelFontInc(); e.preventDefault(); return; }
      if (isExpert && e.altKey && e.key === ',') { labelFontDec(); e.preventDefault(); return; }
      // Ctrl+Shift+1..5 → Navigate to Workflows / Executions / Machines / Globals / Audit.
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
    findReplaceOpen, setFindReplaceOpen, toggleFullscreen, toggleQuickSwitcher, toggleCommandPalette,
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
