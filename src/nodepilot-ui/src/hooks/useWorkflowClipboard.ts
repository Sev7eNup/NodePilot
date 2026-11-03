import { useRef, useCallback, useEffect } from 'react';
import type { XYPosition } from '@xyflow/react';
import {
  parseFragment,
  remapFragment,
  selectFragment,
  serializeFragment,
  type FragmentParseResult,
  type WorkflowFragment,
} from '../lib/workflowFragment';
import type { WorkflowGraphSource } from './useWorkflowHistory';

/** Shift per repeated paste of the same copy, so the copies do not stack on each other. */
const PASTE_OFFSET = 40;

/**
 * Copy and paste of canvas nodes as workflow JSON text. The editor puts the text on the system
 * clipboard, so a selection can go to a text editor and back, or into another workflow.
 */
export function useWorkflowClipboard(
  commitHistory: (label?: string) => void,
  markDirty: () => void,
  source: WorkflowGraphSource,
) {
  // Copy reads the editor's graph state, not React Flow's store: a collapsed group is
  // projected without its children, so copying it from the store would lose them.
  const sourceRef = useRef(source);
  useEffect(() => { sourceRef.current = source; });
  const selectionRef = useRef<{ nodeIds: string[]; edgeIds: string[] }>({ nodeIds: [], edgeIds: [] });
  // Text this editor copied last and how often it has been pasted since. The clipboard may
  // hand it back with Windows line endings, so it is kept and compared normalised.
  const lastCopyRef = useRef<{ text: string; pastes: number } | null>(null);

  const resetPasteCount = useCallback(() => {
    if (lastCopyRef.current) lastCopyRef.current.pastes = 0;
  }, []);

  const updateSelection = useCallback((sel: { nodeIds: string[]; edgeIds: string[] }) => {
    selectionRef.current = sel;
  }, []);

  const selectedFragment = useCallback((): WorkflowFragment => {
    const { nodes, edges } = sourceRef.current;
    // An edge is copied only when both of its endpoints are in the selection.
    return selectFragment(nodes, edges, selectionRef.current.nodeIds);
  }, []);

  /** The selection as JSON text for the clipboard, or null when nothing is selected. */
  const copySelection = useCallback((): string | null => {
    const fragment = selectedFragment();
    if (fragment.nodes.length === 0) return null;
    const text = serializeFragment(fragment);
    lastCopyRef.current = { text: normalizeLineEndings(text), pastes: 0 };
    return text;
  }, [selectedFragment]);

  const insertFragment = useCallback((fragment: WorkflowFragment, dx: number, dy: number) => {
    const { nodes, setNodes, setEdges } = sourceRef.current;
    const existingGroupIds = new Set(nodes.filter((n) => n.type === 'group').map((n) => n.id));
    const existingAliases = new Set(nodes.flatMap((n) => typeof n.data.outputVariable === 'string' ? [n.data.outputVariable] : []));
    const pasted = remapFragment(fragment, { existingGroupIds, existingAliases, dx, dy });
    // Pasting writes through the setters directly, so onNodesChange never fires and the editor's
    // automatic dirty detection would miss the new nodes.
    commitHistory('Paste');
    markDirty();
    setNodes((nds) => [...nds.map((n) => ({ ...n, selected: false })), ...pasted.nodes]);
    setEdges((eds) => [...eds.map((e) => ({ ...e, selected: false })), ...pasted.edges]);
  }, [commitHistory, markDirty]);

  /**
   * Inserts pasted JSON text. A copy made in this editor lands next to its original, each
   * further paste a little further away; other text lands with its top-left node at `origin`.
   * Returns the parse result; nothing changes when it is not ok.
   */
  const pasteText = useCallback((text: string, origin: XYPosition): FragmentParseResult => {
    const result = parseFragment(text);
    if (!result.ok) return result;
    const last = lastCopyRef.current;
    if (last && last.text === normalizeLineEndings(text)) {
      last.pastes += 1;
      insertFragment(result.fragment, PASTE_OFFSET * last.pastes, PASTE_OFFSET * last.pastes);
    } else {
      const { dx, dy } = offsetToOrigin(result.fragment, origin);
      insertFragment(result.fragment, dx, dy);
    }
    return result;
  }, [insertFragment]);

  /** Copies the selection next to itself, without touching the system clipboard. */
  const duplicateSelection = useCallback(() => {
    const fragment = selectedFragment();
    if (fragment.nodes.length > 0) insertFragment(fragment, PASTE_OFFSET, PASTE_OFFSET);
  }, [selectedFragment, insertFragment]);

  return { copySelection, pasteText, duplicateSelection, resetPasteCount, updateSelection };
}

function normalizeLineEndings(text: string): string {
  return text.replaceAll('\r\n', '\n');
}

/** Shift that moves the fragment's top-left node to `origin`. Group children move with their group. */
function offsetToOrigin(fragment: WorkflowFragment, origin: XYPosition): { dx: number; dy: number } {
  const groupIds = new Set(fragment.nodes.filter((n) => n.type === 'group').map((n) => n.id));
  const outer = fragment.nodes.filter((n) => !(n.parentId && groupIds.has(n.parentId)));
  if (outer.length === 0) return { dx: 0, dy: 0 };
  return {
    dx: origin.x - Math.min(...outer.map((n) => n.position.x)),
    dy: origin.y - Math.min(...outer.map((n) => n.position.y)),
  };
}
