import { useState } from 'react';
import type { Node } from '@xyflow/react';
import type { LintResult } from '../lib/workflowLint';

/** Node and screen position of the double-click quick-edit popup. */
export interface QuickEditTarget {
  node: Node;
  x: number;
  y: number;
}

/**
 * Open state of the editor's floating overlays: search, find & replace, help, diff, lint panel,
 * quick switcher, command palette, quick edit, the script editor, the pre-publish checklist and
 * the sub-workflow preview. The header, the keyboard shortcuts and the command palette all open
 * them, and EditorOverlays renders them, so the page holds the state once and hands this object
 * on instead of one prop per flag and setter.
 */
export function useEditorOverlays() {
  const [searchOpen, setSearchOpen] = useState(false);
  const [searchInput, setSearchInput] = useState('');
  const [findReplaceOpen, setFindReplaceOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [diffOpen, setDiffOpen] = useState(false);
  const [lintPanelOpen, setLintPanelOpen] = useState(false);
  const [quickSwitcherOpen, setQuickSwitcherOpen] = useState(false);
  const [commandPaletteOpen, setCommandPaletteOpen] = useState(false);
  const [quickEdit, setQuickEdit] = useState<QuickEditTarget | null>(null);
  const [scriptEditNodeId, setScriptEditNodeId] = useState<string | null>(null);
  // Lint shown by the pre-publish checklist; the modal is open while this is set.
  const [prePublishLint, setPrePublishLint] = useState<LintResult | null>(null);
  // Workflow name or id of the sub-workflow shown in the preview modal.
  const [previewSubWorkflowRef, setPreviewSubWorkflowRef] = useState<string | null>(null);

  return {
    searchOpen, setSearchOpen, searchInput, setSearchInput,
    findReplaceOpen, setFindReplaceOpen,
    helpOpen, setHelpOpen,
    diffOpen, setDiffOpen,
    lintPanelOpen, setLintPanelOpen,
    toggleLintPanel: () => setLintPanelOpen((o) => !o),
    quickSwitcherOpen, setQuickSwitcherOpen,
    toggleQuickSwitcher: () => setQuickSwitcherOpen((o) => !o),
    commandPaletteOpen, setCommandPaletteOpen,
    toggleCommandPalette: () => setCommandPaletteOpen((o) => !o),
    quickEdit, setQuickEdit,
    scriptEditNodeId, setScriptEditNodeId,
    prePublishLint, setPrePublishLint,
    previewSubWorkflowRef, setPreviewSubWorkflowRef,
  };
}

export type EditorOverlayControls = ReturnType<typeof useEditorOverlays>;
