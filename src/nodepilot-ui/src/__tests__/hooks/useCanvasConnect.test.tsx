import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { Node, Edge, FinalConnectionState } from '@xyflow/react';
import { useCanvasConnect } from '../../hooks/useCanvasConnect';

type SetState<T> = React.Dispatch<React.SetStateAction<T>>;

function makeEdge(id: string, source: string, target: string, dataLabel = 'On Success'): Edge {
  return {
    id, source, target, type: 'labeled',
    data: { label: dataLabel, condition: 'step-a.success', disabled: false },
  };
}

function setup({ initialEdges = [] as Edge[] } = {}) {
  let currentNodes: Node[] = [];
  let currentEdges = initialEdges;

  const setNodes = vi.fn((updater: any) => {
    currentNodes = typeof updater === 'function' ? updater(currentNodes) : updater;
  }) as unknown as SetState<Node[]>;
  const setEdges = vi.fn((updater: any) => {
    currentEdges = typeof updater === 'function' ? updater(currentEdges) : updater;
  }) as unknown as SetState<Edge[]>;
  const setSelected = vi.fn();
  const commitHistory = vi.fn();
  const screenToFlowPosition = vi.fn(({ x, y }) => ({ x: x * 0.5, y: y * 0.5 }));

  const canvasEl = {
    getBoundingClientRect: () => ({
      left: 0, top: 0, right: 800, bottom: 600, width: 800, height: 600, x: 0, y: 0, toJSON: () => ({}),
    }),
  } as unknown as HTMLElement;
  const canvasRef = { current: canvasEl };

  const { result } = renderHook(() =>
    useCanvasConnect({
      edges: currentEdges,
      setNodes, setEdges, setSelected, commitHistory,
      canvasRef, screenToFlowPosition,
    })
  );

  return {
    result,
    setNodes, setEdges, setSelected, commitHistory, screenToFlowPosition,
    getCurrentNodes: () => currentNodes,
    getCurrentEdges: () => currentEdges,
  };
}

describe('useCanvasConnect', () => {
  beforeEach(() => { vi.useFakeTimers().setSystemTime(new Date('2026-04-26T12:00:00Z')); });

  describe('quick-connect (drag end on empty canvas)', () => {
    it('handleConnectEnd opens the picker when a connection drag ends with no target', () => {
      const harness = setup();
      const event = { clientX: 200, clientY: 150 } as MouseEvent;
      const state: FinalConnectionState = {
        toHandle: null,
        toNode: null,
        fromNode: { id: 'step-source' } as Node,
        fromHandle: { id: 'out' } as any,
        isValid: null,
        toPosition: null,
        connection: null as any,
      } as any;

      act(() => { harness.result.current.handleConnectEnd(event, state); });

      expect(harness.result.current.quickConnect).not.toBeNull();
      expect(harness.result.current.quickConnect!.fromNodeId).toBe('step-source');
      expect(harness.result.current.quickConnect!.fromHandleId).toBe('out');
      // screenToFlowPosition is called with raw client coords; our stub halves them.
      expect(harness.result.current.quickConnect!.flowPosition).toEqual({ x: 100, y: 75 });
    });

    it('handleConnectEnd is a no-op when the drag landed on an existing handle', () => {
      const harness = setup();
      const state = {
        toHandle: { id: 'in' },
        toNode: { id: 'step-other' },
        fromNode: { id: 'step-source' },
      } as any;

      act(() => { harness.result.current.handleConnectEnd({ clientX: 0, clientY: 0 } as MouseEvent, state); });

      expect(harness.result.current.quickConnect).toBeNull();
    });

    it('handleQuickConnectPick creates a new activity node and a labeled edge from the source', () => {
      const harness = setup();
      // Open the picker first.
      act(() => {
        harness.result.current.handleConnectEnd(
          { clientX: 200, clientY: 150 } as MouseEvent,
          { toHandle: null, toNode: null, fromNode: { id: 'step-source' } as Node, fromHandle: null } as any
        );
      });

      act(() => { harness.result.current.handleQuickConnectPick('runScript', 'New Step'); });

      expect(harness.commitHistory).toHaveBeenCalledOnce();

      const nodeUpdater = (harness.setNodes as any).mock.calls[0][0];
      const newNodes: Node[] = nodeUpdater([]);
      expect(newNodes).toHaveLength(1);
      expect(newNodes[0].type).toBe('activity');
      expect(newNodes[0].data).toMatchObject({ label: 'New Step', activityType: 'runScript' });

      const edgeUpdater = (harness.setEdges as any).mock.calls[0][0];
      const newEdges: Edge[] = edgeUpdater([]);
      expect(newEdges).toHaveLength(1);
      expect(newEdges[0].source).toBe('step-source');
      expect(newEdges[0].target).toBe(newNodes[0].id);
      expect(newEdges[0].sourceHandle).toBe('right');
      expect(newEdges[0].targetHandle).toBe('left');
      expect(newEdges[0].type).toBe('labeled');
      expect(newEdges[0].data).toMatchObject({ label: '', condition: '', disabled: false });

      // Picker closes after the pick.
      expect(harness.result.current.quickConnect).toBeNull();
      expect(harness.setSelected).toHaveBeenCalledWith({ type: 'node', id: newNodes[0].id });
    });

    it('handleQuickConnectPick mirrors the target port from the dragged source port', () => {
      const harness = setup();
      act(() => {
        harness.result.current.handleConnectEnd(
          { clientX: 200, clientY: 150 } as MouseEvent,
          { toHandle: null, toNode: null, fromNode: { id: 'step-source' } as Node, fromHandle: { id: 'bottom' } as any } as any
        );
      });

      act(() => { harness.result.current.handleQuickConnectPick('runScript', 'New Step'); });

      const edgeUpdater = (harness.setEdges as any).mock.calls[0][0];
      const newEdges: Edge[] = edgeUpdater([]);
      expect(newEdges[0].sourceHandle).toBe('bottom');
      expect(newEdges[0].targetHandle).toBe('top');
    });

    it('handleQuickConnectPick is a no-op when the picker is closed', () => {
      const harness = setup();

      act(() => { harness.result.current.handleQuickConnectPick('runScript', 'X'); });

      expect(harness.commitHistory).not.toHaveBeenCalled();
      expect(harness.setNodes).not.toHaveBeenCalled();
      expect(harness.setEdges).not.toHaveBeenCalled();
    });
  });

  describe('inline-insert on edge', () => {
    it('requestInsert opens the inline picker at the requested coordinates', () => {
      const harness = setup({ initialEdges: [makeEdge('e1', 'a', 'b')] });

      act(() => { harness.result.current.requestInsert('e1', 250, 175); });

      expect(harness.result.current.insertAt).toEqual({ edgeId: 'e1', x: 250, y: 175 });
    });

    it('insertOnEdge splits A->B into A->NEW->B; first hop is unconditional, second hop inherits the original label/condition', () => {
      const original = makeEdge('e-orig', 'step-a', 'step-b', 'On Success');
      const harness = setup({ initialEdges: [original] });

      act(() => { harness.result.current.requestInsert('e-orig', 400, 300); });
      act(() => { harness.result.current.insertOnEdge('runScript', 'Mid Step'); });

      expect(harness.commitHistory).toHaveBeenCalledOnce();

      const nodeUpdater = (harness.setNodes as any).mock.calls[0][0];
      const nodes: Node[] = nodeUpdater([]);
      expect(nodes).toHaveLength(1);
      const inserted = nodes[0];
      expect(inserted.type).toBe('activity');
      expect(inserted.data).toMatchObject({ label: 'Mid Step', activityType: 'runScript' });

      const edgeUpdater = (harness.setEdges as any).mock.calls[0][0];
      const edges: Edge[] = edgeUpdater([original]);
      expect(edges).toHaveLength(2);
      expect(edges.find(e => e.id === 'e-orig')).toBeUndefined(); // original removed

      const first = edges.find(e => e.source === 'step-a' && e.target === inserted.id)!;
      const second = edges.find(e => e.source === inserted.id && e.target === 'step-b')!;
      expect(first).toBeDefined();
      expect(second).toBeDefined();
      expect(first.sourceHandle).toBe('right');
      expect(first.targetHandle).toBe('left');
      expect(second.sourceHandle).toBe('right');
      expect(second.targetHandle).toBe('left');
      expect(first.type).toBe('labeled');
      expect(first.data).toMatchObject({ label: '', condition: '', disabled: false });
      // Second hop inherits the original label + condition.
      expect(second.data).toMatchObject({ label: 'On Success', condition: 'step-a.success' });

      // Picker closes after the insert.
      expect(harness.result.current.insertAt).toBeNull();
      expect(harness.setSelected).toHaveBeenCalledWith({ type: 'node', id: inserted.id });
    });

    it('insertOnEdge is a no-op when no insert is pending', () => {
      const harness = setup({ initialEdges: [makeEdge('e1', 'a', 'b')] });

      act(() => { harness.result.current.insertOnEdge('runScript', 'X'); });

      expect(harness.commitHistory).not.toHaveBeenCalled();
      expect(harness.setNodes).not.toHaveBeenCalled();
    });

    it('insertOnEdge clears the picker when the target edge is gone (e.g. concurrent delete)', () => {
      const harness = setup({ initialEdges: [makeEdge('e1', 'a', 'b')] });

      act(() => { harness.result.current.requestInsert('phantom-edge', 100, 100); });
      act(() => { harness.result.current.insertOnEdge('runScript', 'X'); });

      expect(harness.commitHistory).not.toHaveBeenCalled();
      expect(harness.result.current.insertAt).toBeNull();
    });
  });
});
