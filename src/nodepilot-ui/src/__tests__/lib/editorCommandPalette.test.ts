import { describe, it, expect } from 'vitest';
import type { PaletteCommand } from '../../components/designer/overlays/CommandPalette';
import {
  filterCommandsForDesignerMode,
  buildCommandPaletteCommands,
  type CommandPaletteContext,
} from '../../lib/editorCommandPalette';

const noop = () => {};

/** A fully-populated context with everything "off" — tests flip only the fields they exercise. */
function makeCtx(overrides: Partial<CommandPaletteContext> = {}): CommandPaletteContext {
  return {
    roleCanWrite: false,
    canWrite: false,
    isAdmin: false,
    isLockedByMe: false,
    isLockedByOther: false,
    workflowIsEnabled: false,
    hasWorkflow: false,
    isDirty: false,
    save: noop,
    isSaving: false,
    handleRunClick: noop,
    liveExecutionRunning: false,
    cancelRunningExecution: noop,
    lock: noop,
    isLocking: false,
    unlock: noop,
    isUnlocking: false,
    isPublishing: false,
    isEnabling: false,
    isDisabling: false,
    forceUnlock: noop,
    isForceUnlocking: false,
    requestPublish: noop,
    undo: noop,
    redo: noop,
    copySelection: noop,
    pasteBuffer: noop,
    groupSelection: noop,
    deleteSelected: noop,
    selectAll: noop,
    navigateNode: noop,
    setSearchOpen: noop,
    setFindReplaceOpen: noop,
    setHelpOpen: noop,
    setDiffOpen: noop,
    setLintPanelOpen: noop,
    tidyLayout: noop,
    isTidying: false,
    layoutMode: 'dagre',
    restoreOrigLayout: noop,
    hasOrigLayout: false,
    simulationActive: false,
    runSimulation: noop,
    clearSimulation: noop,
    hiddenActivityTypesCount: 0,
    clearActivityTypeFilter: noop,
    toggleFullscreen: noop,
    toggleQuickSwitcher: noop,
    zoomToSelection: noop,
    machineColoringEnabled: false,
    failureHeatmapEnabled: false,
    toggleMachineColoring: noop,
    toggleFailureHeatmap: noop,
    criticalPathEnabled: false,
    toggleCriticalPath: noop,
    fitViewAll: noop,
    edgesAnimated: false,
    toggleEdgesAnimated: noop,
    edgeRouting: 'smooth',
    cycleEdgeRouting: noop,
    edgeWidthInc: noop,
    edgeWidthDec: noop,
    nodeStyle: 'classic',
    toggleNodeStyle: noop,
    zoomIn: noop,
    zoomOut: noop,
    labelFontInc: noop,
    labelFontDec: noop,
    snapToGrid: false,
    setSnapToGrid: noop,
    exportPng: noop,
    exportJson: noop,
    navigate: noop,
    ...overrides,
  };
}

const byId = (cmds: PaletteCommand[], id: string): PaletteCommand => {
  const found = cmds.find((c) => c.id === id);
  if (!found) throw new Error(`command "${id}" not found`);
  return found;
};

describe('filterCommandsForDesignerMode', () => {
  const cmds: PaletteCommand[] = [
    { id: 'plain', title: 'Plain', group: 'Edit', run: noop },
    { id: 'expert', title: 'Expert', group: 'Edit', expertOnly: true, run: noop },
    { id: 'style', title: 'Style toggle', group: 'Style', run: noop },
    { id: 'style-expert', title: 'Style expert', group: 'Style', expertOnly: true, run: noop },
  ];

  it('returns the list unchanged in expert mode', () => {
    const result = filterCommandsForDesignerMode(cmds, 'expert');
    expect(result).toBe(cmds); // same reference — no filtering applied
    expect(result).toHaveLength(4);
  });

  it('drops expertOnly and Style-group commands in standard mode', () => {
    const result = filterCommandsForDesignerMode(cmds, 'standard');
    expect(result.map((c) => c.id)).toEqual(['plain']);
  });

  it('keeps a non-expert non-Style command in standard mode', () => {
    const result = filterCommandsForDesignerMode(
      [{ id: 'view', title: 'View', group: 'View', run: noop }],
      'standard',
    );
    expect(result.map((c) => c.id)).toEqual(['view']);
  });
});

describe('buildCommandPaletteCommands', () => {
  it('produces stable ids for the core commands', () => {
    const cmds = buildCommandPaletteCommands(makeCtx());
    const ids = cmds.map((c) => c.id);
    for (const id of ['lock-edit', 'lock-end', 'publish', 'force-unlock', 'run-test', 'save', 'undo', 'help']) {
      expect(ids).toContain(id);
    }
    // Ids are unique.
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('marks force-unlock as expertOnly', () => {
    const cmds = buildCommandPaletteCommands(makeCtx());
    expect(byId(cmds, 'force-unlock').expertOnly).toBe(true);
  });

  describe('lock-edit disabled rules', () => {
    it('enabled when role can write and no lock is held', () => {
      const cmds = buildCommandPaletteCommands(makeCtx({ roleCanWrite: true }));
      expect(byId(cmds, 'lock-edit').disabled).toBe(false);
    });
    it('disabled without write role', () => {
      const cmds = buildCommandPaletteCommands(makeCtx({ roleCanWrite: false }));
      expect(byId(cmds, 'lock-edit').disabled).toBe(true);
    });
    it('disabled while already locked by me', () => {
      const cmds = buildCommandPaletteCommands(makeCtx({ roleCanWrite: true, isLockedByMe: true }));
      expect(byId(cmds, 'lock-edit').disabled).toBe(true);
    });
    it('disabled while locked by another user', () => {
      const cmds = buildCommandPaletteCommands(makeCtx({ roleCanWrite: true, isLockedByOther: true }));
      expect(byId(cmds, 'lock-edit').disabled).toBe(true);
    });
    it('disabled while a lock request is in flight', () => {
      const cmds = buildCommandPaletteCommands(makeCtx({ roleCanWrite: true, isLocking: true }));
      expect(byId(cmds, 'lock-edit').disabled).toBe(true);
    });
  });

  describe('lock-end disabled rules', () => {
    it('enabled only when locked by me', () => {
      expect(byId(buildCommandPaletteCommands(makeCtx({ isLockedByMe: true })), 'lock-end').disabled).toBe(false);
      expect(byId(buildCommandPaletteCommands(makeCtx({ isLockedByMe: false })), 'lock-end').disabled).toBe(true);
    });
    it('disabled while unlocking', () => {
      const cmds = buildCommandPaletteCommands(makeCtx({ isLockedByMe: true, isUnlocking: true }));
      expect(byId(cmds, 'lock-end').disabled).toBe(true);
    });
  });

  describe('publish disabled rules', () => {
    it('enabled with write role and no busy/lock conflict', () => {
      const cmds = buildCommandPaletteCommands(makeCtx({ roleCanWrite: true }));
      expect(byId(cmds, 'publish').disabled).toBe(false);
    });
    it('disabled without write role', () => {
      expect(byId(buildCommandPaletteCommands(makeCtx({ roleCanWrite: false })), 'publish').disabled).toBe(true);
    });
    it('disabled when locked by another user', () => {
      const cmds = buildCommandPaletteCommands(makeCtx({ roleCanWrite: true, isLockedByOther: true }));
      expect(byId(cmds, 'publish').disabled).toBe(true);
    });
    it('disabled while publishing or enabling (publishBusy)', () => {
      expect(byId(buildCommandPaletteCommands(makeCtx({ roleCanWrite: true, isPublishing: true })), 'publish').disabled).toBe(true);
      expect(byId(buildCommandPaletteCommands(makeCtx({ roleCanWrite: true, isEnabling: true })), 'publish').disabled).toBe(true);
    });
    it('disabled while disabling', () => {
      const cmds = buildCommandPaletteCommands(makeCtx({ roleCanWrite: true, isDisabling: true }));
      expect(byId(cmds, 'publish').disabled).toBe(true);
    });
  });

  describe('force-unlock disabled rules', () => {
    it('enabled for an admin when another user holds the lock', () => {
      const cmds = buildCommandPaletteCommands(makeCtx({ isAdmin: true, isLockedByOther: true }));
      expect(byId(cmds, 'force-unlock').disabled).toBe(false);
    });
    it('disabled for a non-admin', () => {
      const cmds = buildCommandPaletteCommands(makeCtx({ isAdmin: false, isLockedByOther: true }));
      expect(byId(cmds, 'force-unlock').disabled).toBe(true);
    });
    it('disabled when nobody else holds the lock', () => {
      const cmds = buildCommandPaletteCommands(makeCtx({ isAdmin: true, isLockedByOther: false }));
      expect(byId(cmds, 'force-unlock').disabled).toBe(true);
    });
  });

  describe('run + save disabled rules', () => {
    it('run-test needs write role and no live execution', () => {
      expect(byId(buildCommandPaletteCommands(makeCtx({ roleCanWrite: true })), 'run-test').disabled).toBe(false);
      expect(byId(buildCommandPaletteCommands(makeCtx({ roleCanWrite: false })), 'run-test').disabled).toBe(true);
      expect(byId(buildCommandPaletteCommands(makeCtx({ roleCanWrite: true, liveExecutionRunning: true })), 'run-test').disabled).toBe(true);
    });
    it('run-cancel is enabled only while an execution is live', () => {
      expect(byId(buildCommandPaletteCommands(makeCtx({ liveExecutionRunning: true })), 'run-cancel').disabled).toBe(false);
      expect(byId(buildCommandPaletteCommands(makeCtx({ liveExecutionRunning: false })), 'run-cancel').disabled).toBe(true);
    });
    it('save needs canWrite AND a dirty buffer AND not-saving', () => {
      expect(byId(buildCommandPaletteCommands(makeCtx({ canWrite: true, isDirty: true })), 'save').disabled).toBe(false);
      expect(byId(buildCommandPaletteCommands(makeCtx({ canWrite: false, isDirty: true })), 'save').disabled).toBe(true);
      expect(byId(buildCommandPaletteCommands(makeCtx({ canWrite: true, isDirty: false })), 'save').disabled).toBe(true);
      expect(byId(buildCommandPaletteCommands(makeCtx({ canWrite: true, isDirty: true, isSaving: true })), 'save').disabled).toBe(true);
    });
  });

  describe('permission-gated edit commands', () => {
    it('paste / delete / duplicate require canWrite', () => {
      const off = buildCommandPaletteCommands(makeCtx({ canWrite: false }));
      const on = buildCommandPaletteCommands(makeCtx({ canWrite: true }));
      for (const id of ['paste', 'delete-sel', 'duplicate']) {
        expect(byId(off, id).disabled).toBe(true);
        expect(byId(on, id).disabled).toBe(false);
      }
    });
    it('undo / redo / select-all carry no disabled gate', () => {
      const cmds = buildCommandPaletteCommands(makeCtx());
      for (const id of ['undo', 'redo', 'select-all']) {
        expect(byId(cmds, id).disabled).toBeUndefined();
      }
    });
  });

  describe('layout / export / style disabled rules', () => {
    it('filter-clear is disabled when nothing is hidden', () => {
      expect(byId(buildCommandPaletteCommands(makeCtx({ hiddenActivityTypesCount: 0 })), 'filter-clear').disabled).toBe(true);
      expect(byId(buildCommandPaletteCommands(makeCtx({ hiddenActivityTypesCount: 3 })), 'filter-clear').disabled).toBe(false);
    });
    it('restore-original-layout depends on hasOrigLayout', () => {
      expect(byId(buildCommandPaletteCommands(makeCtx({ hasOrigLayout: false })), 'orig').disabled).toBe(true);
      expect(byId(buildCommandPaletteCommands(makeCtx({ hasOrigLayout: true })), 'orig').disabled).toBe(false);
    });
    it('diff + export-json depend on hasWorkflow', () => {
      const without = buildCommandPaletteCommands(makeCtx({ hasWorkflow: false }));
      const withWf = buildCommandPaletteCommands(makeCtx({ hasWorkflow: true }));
      expect(byId(without, 'diff').disabled).toBe(true);
      expect(byId(without, 'export-json').disabled).toBe(true);
      expect(byId(withWf, 'diff').disabled).toBe(false);
      expect(byId(withWf, 'export-json').disabled).toBe(false);
    });
    it('node-size style commands are only enabled in classic node style', () => {
      const classic = buildCommandPaletteCommands(makeCtx({ nodeStyle: 'classic' }));
      const card = buildCommandPaletteCommands(makeCtx({ nodeStyle: 'card' }));
      for (const id of ['style-node-larger', 'style-node-smaller', 'style-label-larger', 'style-label-smaller']) {
        expect(byId(classic, id).disabled).toBe(false);
        expect(byId(card, id).disabled).toBe(true);
      }
    });
  });
});
