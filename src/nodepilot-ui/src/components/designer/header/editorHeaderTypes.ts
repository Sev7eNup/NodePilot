import type { Node } from '@xyflow/react';
import type { Workflow } from '../../../types/api';
import type { EditorLifecycle } from '../../../hooks/useEditorLifecycle';
import type { EditorOverlayControls } from '../../../hooks/useEditorOverlays';
import type { TidyLayoutApi } from '../../../hooks/useTidyLayout';

export type LiveExecution = {
  status: string;
  executionId: string;
} | null | undefined;

export type LintResult = { errors: { nodeId?: string }[]; warnings: { nodeId?: string }[] };

/** Run controls: the live execution, starting a test or debug run, cancelling it. */
export interface HeaderRunControls {
  liveExecution: LiveExecution;
  run: (debug?: boolean) => void;
  cancelRun: () => void;
}

/**
 * Full prop contract for the editor header. Shared by the dispatcher ({@link EditorHeader})
 * and both concrete layouts ({@link CompactEditorHeader} / {@link ClassicEditorHeader}); the
 * dispatcher forwards `{...props}` verbatim so the two layouts stay behaviourally identical.
 * State that belongs together travels as one object, built once by the page.
 */
export interface EditorHeaderProps {
  workflowId: string | undefined;
  workflow: Workflow | undefined;
  name: string;
  onRename: (value: string) => void;
  nodes: Node[];

  /** Edit-lock, save and publish state with its actions. The toolbar never calls the publish
   *  mutations directly: `requestPublish` runs the pre-publish checklist first. */
  lifecycle: EditorLifecycle;
  history: { undo: () => void; redo: () => void; historyPast: unknown[]; historyFuture: unknown[] };
  layout: TidyLayoutApi;
  /** Opens the search, find & replace, diff, lint and help overlays. */
  overlays: Pick<EditorOverlayControls, 'setSearchOpen' | 'setFindReplaceOpen' | 'setDiffOpen' | 'setLintPanelOpen' | 'setHelpOpen'>;
  simulation: { simulation: unknown; runSimulation: () => void; clearSimulation: () => void };
  run: HeaderRunControls;
  lintResult: LintResult;
  hiddenActivityTypes: Set<string>;
  setHiddenActivityTypes: (s: Set<string>) => void;
  zoomToSelection: () => void;
  exportPng: () => void;

  // AI workflow assistant (docked chat panel toggle)
  aiChatOpen: boolean;
  onToggleAiChat: () => void;
}
