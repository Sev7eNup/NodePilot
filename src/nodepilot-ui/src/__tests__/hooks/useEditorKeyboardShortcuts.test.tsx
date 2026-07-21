import { describe, it, expect, vi, beforeEach, type Mock } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useEditorKeyboardShortcuts } from '../../hooks/useEditorKeyboardShortcuts';
import type { DesignerMode } from '../../stores/designStore';

interface ShortcutMocks {
  undo: Mock<() => void>;
  redo: Mock<() => void>;
  copySelection: Mock<() => void>;
  pasteBuffer: Mock<() => void>;
  groupSelection: Mock<() => void>;
  selectAll: Mock<() => void>;
  zoomToSelection: Mock<() => void>;
  navigateNode: Mock<(direction: 'next' | 'prev') => void>;
  setSearchOpen: Mock<(open: boolean) => void>;
  setSearchInput: Mock<(input: string) => void>;
  setHelpOpen: Mock<(open: boolean | ((o: boolean) => boolean)) => void>;
  setFindReplaceOpen: Mock<(open: boolean) => void>;
  toggleFullscreen: Mock<() => void>;
  toggleQuickSwitcher: Mock<() => void>;
  toggleCommandPalette: Mock<() => void>;
  triggerSave: Mock<() => void>;
  triggerLock: Mock<() => void>;
  triggerUnlock: Mock<() => void>;
  triggerForceUnlock: Mock<() => void>;
  triggerPublish: Mock<() => void>;
  triggerTest: Mock<() => void>;
  triggerDebug: Mock<() => void>;
  triggerCancel: Mock<() => void>;
  triggerTidy: Mock<() => void>;
  toggleLintPanel: Mock<() => void>;
  restoreOrigLayout: Mock<() => void>;
  setDiffOpen: Mock<(open: boolean) => void>;
  triggerSimulation: Mock<() => void>;
  clearActivityTypeFilter: Mock<() => void>;
  toggleEdgesAnimated: Mock<() => void>;
  cycleEdgeRouting: Mock<() => void>;
  edgeWidthInc: Mock<() => void>;
  edgeWidthDec: Mock<() => void>;
  toggleNodeStyle: Mock<() => void>;
  nodeSizeInc: Mock<() => void>;
  nodeSizeDec: Mock<() => void>;
  labelFontInc: Mock<() => void>;
  labelFontDec: Mock<() => void>;
  toggleMachineColoring: Mock<() => void>;
  toggleFailureHeatmap: Mock<() => void>;
  toggleCriticalPath: Mock<() => void>;
  toggleSnapToGrid: Mock<() => void>;
  toggleSelectedDisabled: Mock<() => void>;
  toggleSelectedBreakpoint: Mock<() => void>;
  nudgeSelectedNodes: Mock<(dx: number, dy: number) => void>;
  fitViewAll: Mock<() => void>;
  exportJson: Mock<() => void>;
  exportPng: Mock<() => void>;
  navigate: Mock<(to: string) => void>;
}

function newMocks(): ShortcutMocks {
  return {
    undo: vi.fn<() => void>(),
    redo: vi.fn<() => void>(),
    copySelection: vi.fn<() => void>(),
    pasteBuffer: vi.fn<() => void>(),
    groupSelection: vi.fn<() => void>(),
    selectAll: vi.fn<() => void>(),
    zoomToSelection: vi.fn<() => void>(),
    navigateNode: vi.fn<(direction: 'next' | 'prev') => void>(),
    setSearchOpen: vi.fn<(open: boolean) => void>(),
    setSearchInput: vi.fn<(input: string) => void>(),
    setHelpOpen: vi.fn<(open: boolean | ((o: boolean) => boolean)) => void>(),
    setFindReplaceOpen: vi.fn<(open: boolean) => void>(),
    toggleFullscreen: vi.fn<() => void>(),
    toggleQuickSwitcher: vi.fn<() => void>(),
    toggleCommandPalette: vi.fn<() => void>(),
    triggerSave: vi.fn<() => void>(),
    triggerLock: vi.fn<() => void>(),
    triggerUnlock: vi.fn<() => void>(),
    triggerForceUnlock: vi.fn<() => void>(),
    triggerPublish: vi.fn<() => void>(),
    triggerTest: vi.fn<() => void>(),
    triggerDebug: vi.fn<() => void>(),
    triggerCancel: vi.fn<() => void>(),
    triggerTidy: vi.fn<() => void>(),
    toggleLintPanel: vi.fn<() => void>(),
    restoreOrigLayout: vi.fn<() => void>(),
    setDiffOpen: vi.fn<(open: boolean) => void>(),
    triggerSimulation: vi.fn<() => void>(),
    clearActivityTypeFilter: vi.fn<() => void>(),
    toggleEdgesAnimated: vi.fn<() => void>(),
    cycleEdgeRouting: vi.fn<() => void>(),
    edgeWidthInc: vi.fn<() => void>(),
    edgeWidthDec: vi.fn<() => void>(),
    toggleNodeStyle: vi.fn<() => void>(),
    nodeSizeInc: vi.fn<() => void>(),
    nodeSizeDec: vi.fn<() => void>(),
    labelFontInc: vi.fn<() => void>(),
    labelFontDec: vi.fn<() => void>(),
    toggleMachineColoring: vi.fn<() => void>(),
    toggleFailureHeatmap: vi.fn<() => void>(),
    toggleCriticalPath: vi.fn<() => void>(),
    toggleSnapToGrid: vi.fn<() => void>(),
    toggleSelectedDisabled: vi.fn<() => void>(),
    toggleSelectedBreakpoint: vi.fn<() => void>(),
    nudgeSelectedNodes: vi.fn<(dx: number, dy: number) => void>(),
    fitViewAll: vi.fn<() => void>(),
    exportJson: vi.fn<() => void>(),
    exportPng: vi.fn<() => void>(),
    navigate: vi.fn<(to: string) => void>(),
  };
}

function mount(mocks: ShortcutMocks, overrides: { searchOpen?: boolean; helpOpen?: boolean; findReplaceOpen?: boolean; designerMode?: DesignerMode } = {}) {
  return renderHook(() =>
    useEditorKeyboardShortcuts({
      designerMode: overrides.designerMode ?? 'expert',
      undo: mocks.undo,
      redo: mocks.redo,
      copySelection: mocks.copySelection,
      pasteBuffer: mocks.pasteBuffer,
      groupSelection: mocks.groupSelection,
      selectAll: mocks.selectAll,
      zoomToSelection: mocks.zoomToSelection,
      navigateNode: mocks.navigateNode,
      searchOpen: overrides.searchOpen ?? false,
      setSearchOpen: mocks.setSearchOpen,
      setSearchInput: mocks.setSearchInput,
      helpOpen: overrides.helpOpen ?? false,
      setHelpOpen: mocks.setHelpOpen,
      findReplaceOpen: overrides.findReplaceOpen ?? false,
      setFindReplaceOpen: mocks.setFindReplaceOpen,
      toggleFullscreen: mocks.toggleFullscreen,
      toggleQuickSwitcher: mocks.toggleQuickSwitcher,
      toggleCommandPalette: mocks.toggleCommandPalette,
      triggerSave: mocks.triggerSave,
      triggerLock: mocks.triggerLock,
      triggerUnlock: mocks.triggerUnlock,
      triggerForceUnlock: mocks.triggerForceUnlock,
      triggerPublish: mocks.triggerPublish,
      triggerTest: mocks.triggerTest,
      triggerDebug: mocks.triggerDebug,
      triggerCancel: mocks.triggerCancel,
      triggerTidy: mocks.triggerTidy,
      toggleLintPanel: mocks.toggleLintPanel,
      restoreOrigLayout: mocks.restoreOrigLayout,
      setDiffOpen: mocks.setDiffOpen,
      triggerSimulation: mocks.triggerSimulation,
      clearActivityTypeFilter: mocks.clearActivityTypeFilter,
      toggleEdgesAnimated: mocks.toggleEdgesAnimated,
      cycleEdgeRouting: mocks.cycleEdgeRouting,
      edgeWidthInc: mocks.edgeWidthInc,
      edgeWidthDec: mocks.edgeWidthDec,
      toggleNodeStyle: mocks.toggleNodeStyle,
      nodeSizeInc: mocks.nodeSizeInc,
      nodeSizeDec: mocks.nodeSizeDec,
      labelFontInc: mocks.labelFontInc,
      labelFontDec: mocks.labelFontDec,
      toggleMachineColoring: mocks.toggleMachineColoring,
      toggleFailureHeatmap: mocks.toggleFailureHeatmap,
      toggleCriticalPath: mocks.toggleCriticalPath,
      toggleSnapToGrid: mocks.toggleSnapToGrid,
      toggleSelectedDisabled: mocks.toggleSelectedDisabled,
      toggleSelectedBreakpoint: mocks.toggleSelectedBreakpoint,
      nudgeSelectedNodes: mocks.nudgeSelectedNodes,
      fitViewAll: mocks.fitViewAll,
      exportJson: mocks.exportJson,
      exportPng: mocks.exportPng,
      navigate: mocks.navigate,
    }),
  );
}

function dispatchKey(opts: KeyboardEventInit & { target?: HTMLElement }) {
  const ev = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...opts });
  if (opts.target) {
    Object.defineProperty(ev, 'target', { value: opts.target });
  }
  window.dispatchEvent(ev);
  return ev;
}

describe('useEditorKeyboardShortcuts', () => {
  let mocks: ShortcutMocks;
  beforeEach(() => { mocks = newMocks(); });

  it('ignores designer shortcuts while the runScript editor dialog is open', () => {
    const dialog = document.createElement('div');
    dialog.setAttribute('data-nodepilot-script-editor-dialog', 'true');
    document.body.appendChild(dialog);
    try {
      mount(mocks);

      dispatchKey({ key: 's', ctrlKey: true });
      dispatchKey({ key: 'p', ctrlKey: true });
      dispatchKey({ key: 'P', ctrlKey: true, shiftKey: true });
      dispatchKey({ key: 'a' });
      dispatchKey({ key: 'F11' });
      dispatchKey({ key: 'Enter', ctrlKey: true });
      dispatchKey({ key: 'Escape' });

      expect(mocks.triggerSave).not.toHaveBeenCalled();
      expect(mocks.toggleQuickSwitcher).not.toHaveBeenCalled();
      expect(mocks.toggleCommandPalette).not.toHaveBeenCalled();
      expect(mocks.toggleEdgesAnimated).not.toHaveBeenCalled();
      expect(mocks.toggleFullscreen).not.toHaveBeenCalled();
      expect(mocks.triggerTest).not.toHaveBeenCalled();
      expect(mocks.setFindReplaceOpen).not.toHaveBeenCalled();
      expect(mocks.setSearchOpen).not.toHaveBeenCalled();
      expect(mocks.setHelpOpen).not.toHaveBeenCalled();
    } finally {
      dialog.remove();
    }
  });

  it('Ctrl+Z_callsUndo', () => {
    mount(mocks);
    dispatchKey({ key: 'z', ctrlKey: true });
    expect(mocks.undo).toHaveBeenCalledOnce();
    expect(mocks.redo).not.toHaveBeenCalled();
  });

  it('Ctrl+Shift+Z_callsRedo', () => {
    mount(mocks);
    dispatchKey({ key: 'Z', ctrlKey: true, shiftKey: true });
    expect(mocks.redo).toHaveBeenCalledOnce();
    expect(mocks.undo).not.toHaveBeenCalled();
  });

  it('Ctrl+Y_callsRedo', () => {
    mount(mocks);
    dispatchKey({ key: 'y', ctrlKey: true });
    expect(mocks.redo).toHaveBeenCalledOnce();
  });

  it('Ctrl+C_callsCopy', () => {
    mount(mocks);
    dispatchKey({ key: 'c', ctrlKey: true });
    expect(mocks.copySelection).toHaveBeenCalledOnce();
  });

  it('Ctrl+V_callsPaste', () => {
    mount(mocks);
    dispatchKey({ key: 'v', ctrlKey: true });
    expect(mocks.pasteBuffer).toHaveBeenCalledOnce();
  });

  it('Ctrl+D_duplicates_byCopyThenPaste', () => {
    mount(mocks);
    dispatchKey({ key: 'd', ctrlKey: true });
    expect(mocks.copySelection).toHaveBeenCalledOnce();
    expect(mocks.pasteBuffer).toHaveBeenCalledOnce();
  });

  it('Ctrl+Shift+D_callsSetDiffOpen_notDuplicate', () => {
    // Ctrl+Shift+D now opens Diff overlay; Ctrl+D (no shift) stays duplicate.
    mount(mocks);
    dispatchKey({ key: 'D', ctrlKey: true, shiftKey: true });
    expect(mocks.setDiffOpen).toHaveBeenCalledWith(true);
    expect(mocks.copySelection).not.toHaveBeenCalled();
    expect(mocks.pasteBuffer).not.toHaveBeenCalled();
  });

  it('Ctrl+A_callsSelectAll', () => {
    mount(mocks);
    dispatchKey({ key: 'a', ctrlKey: true });
    expect(mocks.selectAll).toHaveBeenCalledOnce();
  });

  it('Ctrl+G_callsGroupSelection', () => {
    mount(mocks);
    dispatchKey({ key: 'g', ctrlKey: true });
    expect(mocks.groupSelection).toHaveBeenCalledOnce();
  });

  it('Ctrl+Shift+E_callsZoomToSelection', () => {
    mount(mocks);
    dispatchKey({ key: 'e', ctrlKey: true, shiftKey: true });
    expect(mocks.zoomToSelection).toHaveBeenCalledOnce();
  });

  it('Ctrl+F_opensSearch_evenFromInputField', () => {
    mount(mocks);
    const input = document.createElement('input');
    document.body.appendChild(input);
    dispatchKey({ key: 'f', ctrlKey: true, target: input });
    expect(mocks.setSearchOpen).toHaveBeenCalledWith(true);
  });

  it('Ctrl+H_opensFindReplace', () => {
    mount(mocks);
    dispatchKey({ key: 'h', ctrlKey: true });
    expect(mocks.setFindReplaceOpen).toHaveBeenCalledWith(true);
  });

  it('Ctrl+P_callsQuickSwitcher_NotCommandPalette', () => {
    mount(mocks);
    dispatchKey({ key: 'p', ctrlKey: true });
    expect(mocks.toggleQuickSwitcher).toHaveBeenCalledOnce();
    expect(mocks.toggleCommandPalette).not.toHaveBeenCalled();
  });

  it('Ctrl+Shift+P_callsCommandPalette', () => {
    mount(mocks);
    dispatchKey({ key: 'P', ctrlKey: true, shiftKey: true });
    expect(mocks.toggleCommandPalette).toHaveBeenCalledOnce();
    expect(mocks.toggleQuickSwitcher).not.toHaveBeenCalled();
  });

  it('Tab_navigatesToNextNode', () => {
    mount(mocks);
    dispatchKey({ key: 'Tab' });
    expect(mocks.navigateNode).toHaveBeenCalledWith('next');
  });

  it('ShiftTab_navigatesToPrevNode', () => {
    mount(mocks);
    dispatchKey({ key: 'Tab', shiftKey: true });
    expect(mocks.navigateNode).toHaveBeenCalledWith('prev');
  });

  it('F11_togglesFullscreen', () => {
    mount(mocks);
    dispatchKey({ key: 'F11' });
    expect(mocks.toggleFullscreen).toHaveBeenCalledOnce();
  });

  it('QuestionMark_togglesHelp', () => {
    mount(mocks);
    dispatchKey({ key: '?' });
    expect(mocks.setHelpOpen).toHaveBeenCalledOnce();
  });

  it('Escape_closesFindReplace_first', () => {
    mount(mocks, { findReplaceOpen: true, searchOpen: true, helpOpen: true });
    dispatchKey({ key: 'Escape' });
    expect(mocks.setFindReplaceOpen).toHaveBeenCalledWith(false);
    expect(mocks.setSearchOpen).not.toHaveBeenCalled();
    expect(mocks.setHelpOpen).not.toHaveBeenCalled();
  });

  it('Escape_closesSearch_secondPriority', () => {
    mount(mocks, { findReplaceOpen: false, searchOpen: true, helpOpen: true });
    dispatchKey({ key: 'Escape' });
    expect(mocks.setSearchOpen).toHaveBeenCalledWith(false);
    expect(mocks.setSearchInput).toHaveBeenCalledWith('');
  });

  it('inputField_swallowsCtrlZ', () => {
    mount(mocks);
    const textarea = document.createElement('textarea');
    document.body.appendChild(textarea);
    dispatchKey({ key: 'z', ctrlKey: true, target: textarea });
    expect(mocks.undo).not.toHaveBeenCalled();
  });

  it('contentEditable_swallowsCtrlA', () => {
    mount(mocks);
    const div = document.createElement('div');
    Object.defineProperty(div, 'isContentEditable', { value: true, configurable: true });
    document.body.appendChild(div);
    dispatchKey({ key: 'a', ctrlKey: true, target: div });
    expect(mocks.selectAll).not.toHaveBeenCalled();
  });

  // ---- Lifecycle / save / run shortcuts (added in iteration 3) ------------------------------

  it('Ctrl+S_callsTriggerSave', () => {
    mount(mocks);
    dispatchKey({ key: 's', ctrlKey: true });
    expect(mocks.triggerSave).toHaveBeenCalledOnce();
    expect(mocks.triggerPublish).not.toHaveBeenCalled();
  });

  it('Ctrl+S_firesEvenInsideInputField', () => {
    mount(mocks);
    const input = document.createElement('input');
    document.body.appendChild(input);
    dispatchKey({ key: 's', ctrlKey: true, target: input });
    expect(mocks.triggerSave).toHaveBeenCalledOnce();
  });

  it('Ctrl+Shift+S_callsTriggerPublish', () => {
    mount(mocks);
    dispatchKey({ key: 'S', ctrlKey: true, shiftKey: true });
    expect(mocks.triggerPublish).toHaveBeenCalledOnce();
    expect(mocks.triggerSave).not.toHaveBeenCalled();
  });

  it('Ctrl+E_callsTriggerLock', () => {
    mount(mocks);
    dispatchKey({ key: 'e', ctrlKey: true });
    expect(mocks.triggerLock).toHaveBeenCalledOnce();
    expect(mocks.zoomToSelection).not.toHaveBeenCalled();
  });

  it('Ctrl+Shift+E_keepsZoomToSelection_notLock', () => {
    mount(mocks);
    dispatchKey({ key: 'e', ctrlKey: true, shiftKey: true });
    expect(mocks.zoomToSelection).toHaveBeenCalledOnce();
    expect(mocks.triggerLock).not.toHaveBeenCalled();
  });

  it('Ctrl+Enter_callsTriggerTest', () => {
    mount(mocks);
    dispatchKey({ key: 'Enter', ctrlKey: true });
    expect(mocks.triggerTest).toHaveBeenCalledOnce();
    expect(mocks.triggerDebug).not.toHaveBeenCalled();
  });

  it('Ctrl+Shift+Enter_callsTriggerDebug', () => {
    mount(mocks);
    dispatchKey({ key: 'Enter', ctrlKey: true, shiftKey: true });
    expect(mocks.triggerDebug).toHaveBeenCalledOnce();
    expect(mocks.triggerTest).not.toHaveBeenCalled();
  });

  it('Ctrl+Shift+T_callsTriggerTidy', () => {
    mount(mocks);
    dispatchKey({ key: 't', ctrlKey: true, shiftKey: true });
    expect(mocks.triggerTidy).toHaveBeenCalledOnce();
  });

  it('Ctrl+Shift+L_togglesLintPanel', () => {
    mount(mocks);
    dispatchKey({ key: 'l', ctrlKey: true, shiftKey: true });
    expect(mocks.toggleLintPanel).toHaveBeenCalledOnce();
  });

  // ---- New shortcuts: Lifecycle (unlock / force-unlock) ---------------------------------------

  it('Ctrl+U_callsTriggerUnlock', () => {
    mount(mocks);
    dispatchKey({ key: 'u', ctrlKey: true });
    expect(mocks.triggerUnlock).toHaveBeenCalledOnce();
    expect(mocks.triggerForceUnlock).not.toHaveBeenCalled();
  });

  it('Ctrl+Shift+U_callsTriggerForceUnlock', () => {
    mount(mocks);
    dispatchKey({ key: 'U', ctrlKey: true, shiftKey: true });
    expect(mocks.triggerForceUnlock).toHaveBeenCalledOnce();
    expect(mocks.triggerUnlock).not.toHaveBeenCalled();
  });

  // ---- New shortcuts: Run (cancel) -----------------------------------------------------------

  it('Ctrl+Shift+X_callsTriggerCancel', () => {
    mount(mocks);
    dispatchKey({ key: 'X', ctrlKey: true, shiftKey: true });
    expect(mocks.triggerCancel).toHaveBeenCalledOnce();
  });

  // ---- New shortcuts: Layout -----------------------------------------------------------------

  it('Ctrl+Shift+O_callsRestoreOrigLayout', () => {
    mount(mocks);
    dispatchKey({ key: 'O', ctrlKey: true, shiftKey: true });
    expect(mocks.restoreOrigLayout).toHaveBeenCalledOnce();
  });

  it('Ctrl+Shift+D_callsSetDiffOpen', () => {
    mount(mocks);
    dispatchKey({ key: 'D', ctrlKey: true, shiftKey: true });
    expect(mocks.setDiffOpen).toHaveBeenCalledWith(true);
  });

  it('Ctrl+Shift+R_callsTriggerSimulation', () => {
    mount(mocks);
    dispatchKey({ key: 'R', ctrlKey: true, shiftKey: true });
    expect(mocks.triggerSimulation).toHaveBeenCalledOnce();
  });

  it('Ctrl+Alt+X_callsClearActivityTypeFilter', () => {
    mount(mocks);
    dispatchKey({ key: 'x', ctrlKey: true, altKey: true });
    expect(mocks.clearActivityTypeFilter).toHaveBeenCalledOnce();
  });

  // ---- New shortcuts: Style -------------------------------------------------------------------

  it('Ctrl+]_callsEdgeWidthInc', () => {
    mount(mocks);
    dispatchKey({ key: ']', ctrlKey: true });
    expect(mocks.edgeWidthInc).toHaveBeenCalledOnce();
  });

  it('Ctrl+[_callsEdgeWidthDec', () => {
    mount(mocks);
    dispatchKey({ key: '[', ctrlKey: true });
    expect(mocks.edgeWidthDec).toHaveBeenCalledOnce();
  });

  it('Ctrl+Shift+N_callsToggleNodeStyle', () => {
    mount(mocks);
    dispatchKey({ key: 'N', ctrlKey: true, shiftKey: true });
    expect(mocks.toggleNodeStyle).toHaveBeenCalledOnce();
  });

  it('Ctrl+Shift+>_callsNodeSizeInc', () => {
    mount(mocks);
    dispatchKey({ key: '>', ctrlKey: true, shiftKey: true });
    expect(mocks.nodeSizeInc).toHaveBeenCalledOnce();
  });

  it('Ctrl+Shift+<_callsNodeSizeDec', () => {
    mount(mocks);
    dispatchKey({ key: '<', ctrlKey: true, shiftKey: true });
    expect(mocks.nodeSizeDec).toHaveBeenCalledOnce();
  });

  it('Ctrl+Alt+._callsLabelFontInc', () => {
    mount(mocks);
    dispatchKey({ key: '.', ctrlKey: true, altKey: true });
    expect(mocks.labelFontInc).toHaveBeenCalledOnce();
  });

  it('Ctrl+Alt+,_callsLabelFontDec', () => {
    mount(mocks);
    dispatchKey({ key: ',', ctrlKey: true, altKey: true });
    expect(mocks.labelFontDec).toHaveBeenCalledOnce();
  });

  // ---- New shortcuts: Export ------------------------------------------------------------------

  it('Ctrl+Shift+J_callsExportJson', () => {
    mount(mocks);
    dispatchKey({ key: 'J', ctrlKey: true, shiftKey: true });
    expect(mocks.exportJson).toHaveBeenCalledOnce();
  });

  it('Ctrl+Alt+P_callsExportPng', () => {
    mount(mocks);
    dispatchKey({ key: 'p', ctrlKey: true, altKey: true });
    expect(mocks.exportPng).toHaveBeenCalledOnce();
  });

  // ---- New shortcuts: Navigate ---------------------------------------------------------------

  it('Ctrl+Shift+1_navigatesToWorkflows', () => {
    mount(mocks);
    dispatchKey({ key: '1', ctrlKey: true, shiftKey: true });
    expect(mocks.navigate).toHaveBeenCalledWith('/workflows');
  });

  it('Ctrl+Shift+2_navigatesToExecutions', () => {
    mount(mocks);
    dispatchKey({ key: '2', ctrlKey: true, shiftKey: true });
    expect(mocks.navigate).toHaveBeenCalledWith('/executions');
  });

  it('Ctrl+Shift+3_navigatesToMachines', () => {
    mount(mocks);
    dispatchKey({ key: '3', ctrlKey: true, shiftKey: true });
    expect(mocks.navigate).toHaveBeenCalledWith('/machines');
  });

  it('Ctrl+Shift+4_navigatesToGlobals', () => {
    mount(mocks);
    dispatchKey({ key: '4', ctrlKey: true, shiftKey: true });
    expect(mocks.navigate).toHaveBeenCalledWith('/global-variables');
  });

  it('Ctrl+Shift+5_navigatesToAudit', () => {
    mount(mocks);
    dispatchKey({ key: '5', ctrlKey: true, shiftKey: true });
    expect(mocks.navigate).toHaveBeenCalledWith('/audit');
  });

  // ---- Single-letter style toggles (Figma-style, no modifier) -------------------------------

  it('A_togglesEdgeAnimation', () => {
    mount(mocks);
    dispatchKey({ key: 'a' });
    expect(mocks.toggleEdgesAnimated).toHaveBeenCalledOnce();
  });

  it('R_cyclesEdgeRouting', () => {
    mount(mocks);
    dispatchKey({ key: 'r' });
    expect(mocks.cycleEdgeRouting).toHaveBeenCalledOnce();
  });

  it('M_togglesMachineColoring', () => {
    mount(mocks);
    dispatchKey({ key: 'm' });
    expect(mocks.toggleMachineColoring).toHaveBeenCalledOnce();
  });

  it('H_togglesFailureHeatmap', () => {
    mount(mocks);
    dispatchKey({ key: 'h' });
    expect(mocks.toggleFailureHeatmap).toHaveBeenCalledOnce();
  });

  it('G_togglesSnapToGrid', () => {
    mount(mocks);
    dispatchKey({ key: 'g' });
    expect(mocks.toggleSnapToGrid).toHaveBeenCalledOnce();
  });

  it('singleLetterToggles_swallowedInsideInputField', () => {
    mount(mocks);
    const input = document.createElement('input');
    document.body.appendChild(input);
    dispatchKey({ key: 'a', target: input });
    dispatchKey({ key: 'm', target: input });
    expect(mocks.toggleEdgesAnimated).not.toHaveBeenCalled();
    expect(mocks.toggleMachineColoring).not.toHaveBeenCalled();
  });

  it('singleLetterToggles_skippedWhenAnyModifierIsHeld', () => {
    mount(mocks);
    dispatchKey({ key: 'a', ctrlKey: true });
    expect(mocks.toggleEdgesAnimated).not.toHaveBeenCalled();
    expect(mocks.selectAll).toHaveBeenCalledOnce();
    dispatchKey({ key: 'h', ctrlKey: true });
    expect(mocks.toggleFailureHeatmap).not.toHaveBeenCalled();
    expect(mocks.setFindReplaceOpen).toHaveBeenCalledWith(true);
    dispatchKey({ key: 'g', ctrlKey: true });
    expect(mocks.toggleSnapToGrid).not.toHaveBeenCalled();
    expect(mocks.groupSelection).toHaveBeenCalledOnce();
  });

  // ---- Per-node Quick-Toggle shortcuts (D / B) ----------------------------------------------

  it('D_togglesSelectedDisabled', () => {
    mount(mocks);
    dispatchKey({ key: 'd' });
    expect(mocks.toggleSelectedDisabled).toHaveBeenCalledOnce();
  });

  it('B_togglesSelectedBreakpoint', () => {
    mount(mocks);
    dispatchKey({ key: 'b' });
    expect(mocks.toggleSelectedBreakpoint).toHaveBeenCalledOnce();
  });

  it('D_uppercase_alsoTogglesSelectedDisabled', () => {
    mount(mocks);
    dispatchKey({ key: 'D' });
    expect(mocks.toggleSelectedDisabled).toHaveBeenCalledOnce();
  });

  it('D_swallowedInsideInputField', () => {
    mount(mocks);
    const input = document.createElement('input');
    document.body.appendChild(input);
    dispatchKey({ key: 'd', target: input });
    expect(mocks.toggleSelectedDisabled).not.toHaveBeenCalled();
  });

  it('Ctrl+D_keepsDuplicate_notDisabledToggle', () => {
    mount(mocks);
    dispatchKey({ key: 'd', ctrlKey: true });
    expect(mocks.copySelection).toHaveBeenCalledOnce();
    expect(mocks.pasteBuffer).toHaveBeenCalledOnce();
    expect(mocks.toggleSelectedDisabled).not.toHaveBeenCalled();
  });

  it('standardMode_keepsCoreShortcutsButDoesNotRunDebug', () => {
    mount(mocks, { designerMode: 'standard' });
    dispatchKey({ key: 'Enter', ctrlKey: true });
    dispatchKey({ key: 'Enter', ctrlKey: true, shiftKey: true });

    expect(mocks.triggerTest).toHaveBeenCalledOnce();
    expect(mocks.triggerDebug).not.toHaveBeenCalled();
  });

  it('standardMode_doesNotClaimExpertSingleLetterOrNavigationShortcuts', () => {
    mount(mocks, { designerMode: 'standard' });
    const letter = dispatchKey({ key: 'h' });
    const tab = dispatchKey({ key: 'Tab' });

    expect(mocks.toggleFailureHeatmap).not.toHaveBeenCalled();
    expect(mocks.navigateNode).not.toHaveBeenCalled();
    expect(letter.defaultPrevented).toBe(false);
    expect(tab.defaultPrevented).toBe(false);
  });
});
