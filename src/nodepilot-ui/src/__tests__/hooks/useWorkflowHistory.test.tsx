import { describe, it, expect } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useState } from 'react';
import type { Node, Edge } from '@xyflow/react';

import { useWorkflowHistory } from '../../hooks/useWorkflowHistory';

function makeNode(id: string, parentId?: string): Node {
  return { id, position: { x: 0, y: 0 }, data: { label: id }, ...(parentId ? { parentId } : {}) };
}

/**
 * Drives the hook from real React state, the way the editor does. The hook must never read
 * the graph from React Flow's store, so no store is provided here.
 */
function setup(initialNodes: Node[] = [], initialEdges: Edge[] = []) {
  return renderHook(() => {
    const [nodes, setNodes] = useState<Node[]>(initialNodes);
    const [edges, setEdges] = useState<Edge[]>(initialEdges);
    const history = useWorkflowHistory('wf-1', { nodes, edges, setNodes, setEdges });
    return { nodes, edges, setNodes, setEdges, history };
  });
}

describe('useWorkflowHistory', () => {
  it('starts with empty past and future stacks', () => {
    const { result } = setup();
    expect(result.current.history.historyPast).toEqual([]);
    expect(result.current.history.historyFuture).toEqual([]);
  });

  it('commitHistory snapshots the current state onto the past stack', () => {
    const { result } = setup();

    act(() => { result.current.setNodes([makeNode('a')]); });
    act(() => { result.current.history.commitHistory(); });

    expect(result.current.history.historyPast).toHaveLength(1);
    expect(result.current.history.historyPast[0].nodes.map(n => n.id)).toEqual(['a']);
    expect(result.current.history.historyFuture).toEqual([]);
  });

  it('undo restores the previous snapshot and shifts stacks', () => {
    const { result } = setup();

    act(() => { result.current.setNodes([makeNode('a')]); });
    act(() => { result.current.history.commitHistory(); });
    act(() => { result.current.setNodes([makeNode('a'), makeNode('b')]); });
    act(() => { result.current.history.undo(); });

    expect(result.current.nodes.map(n => n.id)).toEqual(['a']);
    expect(result.current.history.historyPast).toHaveLength(0);
    expect(result.current.history.historyFuture).toHaveLength(1);
    // The future entry holds the pre-undo graph, so redo restores it exactly.
    expect(result.current.history.historyFuture[0].nodes.map(n => n.id)).toEqual(['a', 'b']);
  });

  it('redo pops from the future stack and pushes onto the past stack', () => {
    const { result } = setup();

    act(() => { result.current.setNodes([makeNode('a')]); });
    act(() => { result.current.history.commitHistory(); });
    act(() => { result.current.setNodes([makeNode('a'), makeNode('b')]); });
    act(() => { result.current.history.undo(); });

    expect(result.current.history.historyFuture).toHaveLength(1);
    expect(result.current.history.historyPast).toHaveLength(0);

    act(() => { result.current.history.redo(); });

    expect(result.current.nodes.map(n => n.id)).toEqual(['a', 'b']);
    expect(result.current.history.historyFuture).toHaveLength(0);
    expect(result.current.history.historyPast).toHaveLength(1);
  });

  it('commitHistory after an undo clears the redo stack', () => {
    const { result } = setup();

    act(() => { result.current.setNodes([makeNode('a')]); });
    act(() => { result.current.history.commitHistory(); });
    act(() => { result.current.setNodes([makeNode('a'), makeNode('b')]); });
    act(() => { result.current.history.undo(); });
    act(() => { result.current.setNodes([makeNode('c')]); });
    act(() => { result.current.history.commitHistory(); });

    expect(result.current.history.historyFuture).toEqual([]);
  });

  it('caps the past stack at 50 snapshots, dropping the oldest', () => {
    const { result } = setup();

    for (let i = 0; i < 55; i++) {
      act(() => { result.current.setNodes([makeNode(`n${i}`)]); });
      act(() => { result.current.history.commitHistory(); });
    }

    expect(result.current.history.historyPast).toHaveLength(50);
    expect(result.current.history.historyPast[0].nodes[0].id).toBe('n5');
  });

  it('undo on an empty past stack is a no-op', () => {
    const { result } = setup();

    act(() => { result.current.setNodes([makeNode('a')]); });
    act(() => { result.current.history.undo(); });

    expect(result.current.nodes.map(n => n.id)).toEqual(['a']);
    expect(result.current.history.historyFuture).toEqual([]);
  });

  it('keeps the children of a collapsed group through commit and undo', () => {
    // The canvas projection drops a collapsed group's children (buildCollapsedGraphView), so a
    // snapshot taken from React Flow's store would omit them and undo would delete them for
    // real. The hook snapshots the editor's own state instead.
    const group = makeNode('group-1');
    const children = [makeNode('step-a', 'group-1'), makeNode('step-b', 'group-1')];
    const { result } = setup([group, ...children]);

    act(() => { result.current.history.commitHistory('Collapse'); });
    act(() => { result.current.setNodes([group]); });
    act(() => { result.current.history.undo(); });

    expect(result.current.nodes.map(n => n.id)).toEqual(['group-1', 'step-a', 'step-b']);
  });

  it('commitHistory keeps one identity across graph updates', () => {
    // It is a dependency of most graph callbacks; a new identity per drag frame would
    // invalidate all of them.
    const { result } = setup();
    const first = result.current.history.commitHistory;

    act(() => { result.current.setNodes([makeNode('a')]); });
    act(() => { result.current.setNodes([makeNode('a'), makeNode('b')]); });

    expect(result.current.history.commitHistory).toBe(first);
  });
});
