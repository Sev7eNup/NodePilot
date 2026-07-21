import type { Node } from '@xyflow/react';
import type { Workflow } from '../../../types/api';

export type LiveExecution = {
  status: string;
  executionId: string;
} | null | undefined;

export type LintResult = { errors: { nodeId?: string }[]; warnings: { nodeId?: string }[] };

/**
 * Full prop contract for the editor header. Shared by the dispatcher ({@link EditorHeader})
 * and both concrete layouts ({@link CompactEditorHeader} / {@link ClassicEditorHeader}); the
 * dispatcher forwards `{...props}` verbatim so the two layouts stay behaviourally identical.
 */
export interface EditorHeaderProps {
  workflowId: string | undefined;
  workflow: Workflow | undefined;
  name: string;
  onRename: (value: string) => void;
  isDirty: boolean;
  canWrite: boolean;
  nodes: Node[];

  // AI workflow assistant (docked chat panel toggle)
  aiChatOpen: boolean;
  onToggleAiChat: () => void;

  // History
  undo: () => void;
  redo: () => void;
  historyPast: unknown[];
  historyFuture: unknown[];

  // Layout
  tidyLayout: () => void;
  isTidying: boolean;
  layoutMode: import('../../../stores/designStore').LayoutMode;
  restoreOrigLayout: () => void;
  hasOrigLayout: boolean;

  // Inspect cluster
  setSearchOpen: (o: boolean) => void;
  setFindReplaceOpen: (o: boolean) => void;
  zoomToSelection: () => void;
  setDiffOpen: (o: boolean) => void;
  simulation: unknown;
  runSimulation: () => void;
  clearSimulation: () => void;
  lintResult: LintResult;
  setLintPanelOpen: (fn: (o: boolean) => boolean) => void;
  setHelpOpen: (o: boolean) => void;

  // View cluster
  hiddenActivityTypes: Set<string>;
  setHiddenActivityTypes: (s: Set<string>) => void;

  // Run cluster
  liveExecution: LiveExecution;
  handleRunClick: (debug?: boolean) => void;

  // Persist cluster
  exportPng: () => void;
  onSave: () => void;
  isPublishing: boolean;
  /**
   * Single entry point for both publish (lock-by-me) and enable (no lock) paths. The parent
   * runs the pre-publish checklist before triggering the actual mutation, so the toolbar must
   * NOT call the mutations directly.
   */
  onRequestPublish: () => void;

  // Edit-lock lifecycle. `canWrite` already encodes "lock-by-me"; these drive the lifecycle
  // affordances. The Publish/Disable toggle routes to disable/publish/enable based on state.
  roleCanWrite: boolean;
  isLockedByMe: boolean;
  isLockedByOther: boolean;
  onLock: () => void;
  isLocking: boolean;
  onUnlock: () => void;
  isUnlocking: boolean;
  onDisable: () => void;
  isDisabling: boolean;
  isEnabling: boolean;
}
