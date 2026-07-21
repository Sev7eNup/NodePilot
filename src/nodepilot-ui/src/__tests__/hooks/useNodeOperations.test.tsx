import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';
import { useNodeOperations } from '../../hooks/useNodeOperations';

type SetState<T> = React.Dispatch<React.SetStateAction<T>>;

function makeNode(id: string, overrides: Partial<Node> = {}): Node {
  return {
    id,
    position: { x: 100, y: 100 },
    data: { label: id, activityType: 'runScript', config: {} },
    type: 'activity',
    ...overrides,
  };
}

function makeEdge(id: string, source: string, target: string): Edge {
  return { id, source, target };
}

function setup({
  initialNodes = [],
  initialEdges = [],
  initialSelected = null,
}: {
  initialNodes?: Node[];
  initialEdges?: Edge[];
  initialSelected?: { type: 'node' | 'edge'; id: string } | null;
} = {}) {
  let currentNodes = initialNodes;
  let currentEdges = initialEdges;
  let currentSelected = initialSelected;

  const setNodes = vi.fn((updater: any) => {
    currentNodes = typeof updater === 'function' ? updater(currentNodes) : updater;
  }) as unknown as SetState<Node[]>;

  const setEdges = vi.fn((updater: any) => {
    currentEdges = typeof updater === 'function' ? updater(currentEdges) : updater;
  }) as unknown as SetState<Edge[]>;

  const setSelected = vi.fn((s: typeof initialSelected) => { currentSelected = s; });
  const commitHistory = vi.fn();
  const screenToFlowPosition = vi.fn(({ x, y }) => ({ x, y }));

  // canvasRef.current.getBoundingClientRect() must return something - the hook reads it for
  // viewport-based placement. Stub a 800x600 canvas at origin.
  const canvasEl = {
    getBoundingClientRect: () => ({
      left: 0, top: 0, right: 800, bottom: 600, width: 800, height: 600, x: 0, y: 0, toJSON: () => ({}),
    }),
  } as unknown as HTMLElement;
  const canvasRef = { current: canvasEl };

  const { result, rerender } = renderHook((props) =>
    useNodeOperations({
      nodes: props.nodes,
      setNodes,
      edges: props.edges,
      setEdges,
      selected: props.selected,
      setSelected,
      commitHistory,
      canvasRef,
      screenToFlowPosition,
    }),
    { initialProps: { nodes: initialNodes, edges: initialEdges, selected: initialSelected } as any }
  );

  return {
    result, rerender,
    setNodes, setEdges, setSelected, commitHistory,
    getCurrentNodes: () => currentNodes,
    getCurrentEdges: () => currentEdges,
    getCurrentSelected: () => currentSelected,
  };
}

describe('useNodeOperations', () => {
  beforeEach(() => { vi.useFakeTimers().setSystemTime(new Date('2026-04-26T12:00:00Z')); });

  it('addNode commits history, appends a new activity node, and selects it', () => {
    const harness = setup();

    act(() => { harness.result.current.addNode('runScript', 'My Step'); });

    expect(harness.commitHistory).toHaveBeenCalledOnce();
    expect(harness.setNodes).toHaveBeenCalledOnce();
    const updater = (harness.setNodes as any).mock.calls[0][0];
    const next: Node[] = updater([]);
    expect(next).toHaveLength(1);
    expect(next[0].type).toBe('activity');
    expect(next[0].data).toMatchObject({ label: 'My Step', activityType: 'runScript' });
    expect(harness.setSelected).toHaveBeenCalledWith({ type: 'node', id: next[0].id });
  });

  it('addNode with type "note" creates a stickyNote with disabled=true', () => {
    const harness = setup();

    act(() => { harness.result.current.addNode('note', 'unused'); });

    const updater = (harness.setNodes as any).mock.calls[0][0];
    const next: Node[] = updater([]);
    expect(next[0].type).toBe('stickyNote');
    expect(next[0].id).toMatch(/^note-/);
    expect(next[0].data).toMatchObject({ activityType: 'note', disabled: true });
  });

  it('duplicateNode clones an existing node with a new id and offset position', () => {
    const source = makeNode('step-1', { position: { x: 200, y: 150 } });
    const harness = setup({ initialNodes: [source] });

    act(() => { harness.result.current.duplicateNode('step-1'); });

    expect(harness.commitHistory).toHaveBeenCalledOnce();
    const updater = (harness.setNodes as any).mock.calls[0][0];
    const next: Node[] = updater([source]);
    expect(next).toHaveLength(2);
    const clone = next[1];
    expect(clone.id).not.toBe(source.id);
    expect(clone.position).toEqual({ x: 240, y: 190 });
    expect(clone.data).toEqual(source.data);
    expect(harness.setSelected).toHaveBeenCalledWith({ type: 'node', id: clone.id });
  });

  it('duplicateNode is a no-op when the source id does not exist', () => {
    const harness = setup({ initialNodes: [makeNode('step-1')] });

    act(() => { harness.result.current.duplicateNode('does-not-exist'); });

    expect(harness.commitHistory).not.toHaveBeenCalled();
    expect(harness.setNodes).not.toHaveBeenCalled();
  });

  it('deleteNodeById removes the node and any incident edges, and clears selection if it was selected', () => {
    const a = makeNode('step-a');
    const b = makeNode('step-b');
    const c = makeNode('step-c');
    const e1 = makeEdge('e1', 'step-a', 'step-b');
    const e2 = makeEdge('e2', 'step-b', 'step-c');
    const e3 = makeEdge('e3', 'step-a', 'step-c');
    const harness = setup({
      initialNodes: [a, b, c],
      initialEdges: [e1, e2, e3],
      initialSelected: { type: 'node', id: 'step-b' },
    });

    act(() => { harness.result.current.deleteNodeById('step-b'); });

    expect(harness.commitHistory).toHaveBeenCalledOnce();
    const nodeUpdater = (harness.setNodes as any).mock.calls[0][0];
    const remainingNodes: Node[] = nodeUpdater([a, b, c]);
    expect(remainingNodes.map(n => n.id)).toEqual(['step-a', 'step-c']);

    const edgeUpdater = (harness.setEdges as any).mock.calls[0][0];
    const remainingEdges: Edge[] = edgeUpdater([e1, e2, e3]);
    expect(remainingEdges.map(e => e.id)).toEqual(['e3']); // only the a→c edge survives

    expect(harness.setSelected).toHaveBeenCalledWith(null);
  });

  it('deleteNodeById preserves selection when a different node is selected', () => {
    const a = makeNode('step-a');
    const b = makeNode('step-b');
    const harness = setup({
      initialNodes: [a, b],
      initialSelected: { type: 'node', id: 'step-a' },
    });

    act(() => { harness.result.current.deleteNodeById('step-b'); });

    // setSelected(null) only fires when the deleted node was selected.
    expect(harness.setSelected).not.toHaveBeenCalled();
  });

  it('groupSelection wraps selected non-group nodes inside a new group node placed first in the array', () => {
    const a = makeNode('step-a', { position: { x: 100, y: 100 }, selected: true });
    const b = makeNode('step-b', { position: { x: 300, y: 200 }, selected: true });
    const c = makeNode('step-c', { position: { x: 500, y: 300 }, selected: false });
    const harness = setup({ initialNodes: [a, b, c] });

    act(() => { harness.result.current.groupSelection(); });

    expect(harness.commitHistory).toHaveBeenCalledOnce();
    const updater = (harness.setNodes as any).mock.calls[0][0];
    const next: Node[] = updater([a, b, c]);
    expect(next[0].type).toBe('group');
    expect(next[0].id).toMatch(/^group-/);
    // Selected nodes get re-parented under the group.
    const reA = next.find(n => n.id === 'step-a')!;
    const reB = next.find(n => n.id === 'step-b')!;
    const reC = next.find(n => n.id === 'step-c')!;
    expect(reA.parentId).toBe(next[0].id);
    expect(reB.parentId).toBe(next[0].id);
    expect(reC.parentId).toBeUndefined();
  });

  it('groupSelection is a no-op when no nodes are selected', () => {
    const harness = setup({ initialNodes: [makeNode('step-a'), makeNode('step-b')] });

    act(() => { harness.result.current.groupSelection(); });

    expect(harness.commitHistory).not.toHaveBeenCalled();
    expect(harness.setNodes).not.toHaveBeenCalled();
  });

  describe('smart defaults on addNode', () => {
    it('inherits targetMachineId + credentialId from the last runScript', () => {
      const existing = makeNode('rs-1', {
        position: { x: 0, y: 0 },
        data: {
          label: 'Existing', activityType: 'runScript',
          targetMachineId: 'machine-A', credentialId: 'cred-1',
          config: { script: 'Get-Date' },
        },
      });
      const harness = setup({ initialNodes: [existing] });

      act(() => { harness.result.current.addNode('runScript', 'Next Step'); });

      const updater = (harness.setNodes as any).mock.calls[0][0];
      const next: Node[] = updater([existing]);
      const newNode = next[next.length - 1];
      expect(newNode.data).toMatchObject({
        targetMachineId: 'machine-A',
        credentialId: 'cred-1',
      });
    });

    it('first node of a type still falls back to null defaults', () => {
      const harness = setup();

      act(() => { harness.result.current.addNode('runScript', 'First Ever'); });

      const updater = (harness.setNodes as any).mock.calls[0][0];
      const next: Node[] = updater([]);
      expect(next[0].data).toMatchObject({
        targetMachineId: null,
        credentialId: null,
        config: {},
      });
    });

    it('inherits base URL from the last restApi node', () => {
      const existing = makeNode('api-1', {
        position: { x: 0, y: 0 },
        data: {
          label: 'Existing', activityType: 'restApi',
          targetMachineId: null, credentialId: null,
          config: { url: 'https://api.example.com/v1/orders/42', method: 'GET' },
        },
      });
      const harness = setup({ initialNodes: [existing] });

      act(() => { harness.result.current.addNode('restApi', 'Next API'); });

      const updater = (harness.setNodes as any).mock.calls[0][0];
      const next: Node[] = updater([existing]);
      const newNode = next[next.length - 1];
      const cfg = newNode.data!.config as Record<string, unknown>;
      expect(cfg.url).toBe('https://api.example.com/v1');
    });

    it('only inherits from same-type siblings — runScript does not pull from restApi', () => {
      const restApi = makeNode('api-1', {
        position: { x: 0, y: 0 },
        data: {
          label: 'API', activityType: 'restApi',
          targetMachineId: null, credentialId: null,
          config: { url: 'https://api.example.com', method: 'GET' },
        },
      });
      const harness = setup({ initialNodes: [restApi] });

      act(() => { harness.result.current.addNode('runScript', 'New runScript'); });

      const updater = (harness.setNodes as any).mock.calls[0][0];
      const next: Node[] = updater([restApi]);
      const newNode = next[next.length - 1];
      // No similar runScript existed, so defaults stay null + empty config (no URL leakage).
      expect(newNode.data).toMatchObject({ targetMachineId: null, credentialId: null });
      expect(newNode.data!.config).toEqual({});
    });

    it('does not apply smart defaults to sticky-notes', () => {
      const harness = setup({ initialNodes: [makeNode('rs-1', {
        data: { label: 'rs-1', activityType: 'runScript', targetMachineId: 'machine-A', credentialId: 'cred-1', config: {} },
      })] });

      act(() => { harness.result.current.addNode('note', 'unused'); });

      const updater = (harness.setNodes as any).mock.calls[0][0];
      const next: Node[] = updater([]);
      expect(next[0].type).toBe('stickyNote');
      expect((next[0].data as Record<string, unknown>).targetMachineId).toBeUndefined();
    });
  });
});
