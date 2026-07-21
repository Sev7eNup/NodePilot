import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Node, Edge } from '@xyflow/react';
import type { ManagedMachine, StepExecution } from '../../types/api';

// Mock api.get so the two useQuery calls (step-health, step-stats) resolve under our control.
const mockApiGet = vi.fn();
vi.mock('../../api/client', () => ({
  api: { get: (...args: unknown[]) => mockApiGet(...args) },
}));

import { useNodeAnnotations } from '../../hooks/useNodeAnnotations';
import { useDesignStore } from '../../stores/designStore';

type SetState<T> = React.Dispatch<React.SetStateAction<T>>;

function makeNode(id: string, overrides: Partial<Node> = {}, data: Record<string, unknown> = {}): Node {
  return {
    id, position: { x: 0, y: 0 }, type: 'activity',
    data: { activityType: 'runScript', ...data },
    ...overrides,
  };
}

function makeMachine(id: string, name: string, hostname: string): ManagedMachine {
  return { id, name, hostname } as ManagedMachine;
}

function setup({
  initialNodes = [] as Node[],
  initialEdges = [] as Edge[],
  workflowId = 'wf-1' as string | undefined,
  workflowIsEnabled = true as boolean,
  liveExecution = null as { status: string; steps: { stepId: string; status: string }[] } | null,
  replayExecutionId = null as string | null,
  replaySteps = undefined as StepExecution[] | undefined,
  scrubTimeMs = null as number | null,
  machines = [] as ManagedMachine[],
  selected = null as { type: 'node' | 'edge'; id: string } | null,
} = {}) {
  let currentNodes = initialNodes;
  let currentEdges = initialEdges;
  const setNodes = vi.fn((updater: any) => {
    currentNodes = typeof updater === 'function' ? updater(currentNodes) : updater;
  }) as unknown as SetState<Node[]>;
  const setEdges = vi.fn((updater: any) => {
    currentEdges = typeof updater === 'function' ? updater(currentEdges) : updater;
  }) as unknown as SetState<Edge[]>;

  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );

  const { result, rerender } = renderHook(
    (props: { workflowIsEnabled: boolean; liveExecution?: typeof liveExecution }) =>
      useNodeAnnotations({
        workflowId, workflowIsEnabled: props.workflowIsEnabled,
        nodes: currentNodes, setNodes,
        edges: currentEdges, setEdges, selected,
        liveExecution: props.liveExecution ?? liveExecution, replayExecutionId, replaySteps,
        scrubTimeMs, machines,
      }),
    { wrapper, initialProps: { workflowIsEnabled, liveExecution } }
  );

  return {
    result, rerender, qc,
    setNodes, setEdges,
    getCurrentNodes: () => currentNodes,
    getCurrentEdges: () => currentEdges,
    // Simulates an external node rebuild (e.g. a workflow refetch after lock/unlock):
    // replaces currentNodes with fresh references that don't carry our annotations.
    replaceNodes: (next: Node[]) => { currentNodes = next; },
  };
}

describe('useNodeAnnotations', () => {
  beforeEach(() => {
    mockApiGet.mockReset();
    // Reset persisted Zustand store between tests so toggles don't leak.
    useDesignStore.setState({ machineColoringEnabled: false });
  });

  describe('liveStatus annotation', () => {
    it('writes __liveStatus on nodes for each running step from liveExecution', () => {
      const a = makeNode('step-a');
      const b = makeNode('step-b');
      mockApiGet.mockResolvedValue({});
      const harness = setup({
        initialNodes: [a, b],
        liveExecution: { status: 'Running', steps: [{ stepId: 'step-a', status: 'Running' }] },
      });

      // Find the setNodes call that sets the live-status annotation.
      const updaterCalls = (harness.setNodes as any).mock.calls.map((c: any[]) => c[0]);
      const annotated: Node[] = updaterCalls.reduce(
        (nds: Node[], up: (n: Node[]) => Node[]) => up(nds),
        [a, b],
      );

      const ann = annotated.find(n => n.id === 'step-a')!;
      expect((ann.data as Record<string, unknown>).__liveStatus).toBe('Running');
    });

    it('paints liveExecution changes immediately for designer-started canvas runs', () => {
      const a = makeNode('step-a');
      mockApiGet.mockResolvedValue({});
      const harness = setup({
        initialNodes: [a],
        liveExecution: null,
      });

      (harness.setNodes as any).mockClear();
      harness.rerender({
        workflowIsEnabled: true,
        liveExecution: { status: 'Running', steps: [{ stepId: 'step-a', status: 'Running' }] },
      });

      const hasRunningAnnotation = () => (harness.setNodes as any).mock.calls.some((c: any[]) => {
        const annotated = c[0]([a]);
        return (annotated.find((n: Node) => n.id === 'step-a')!.data as Record<string, unknown>).__liveStatus === 'Running';
      });

      expect(hasRunningAnnotation()).toBe(true);
    });

    it('keeps terminal liveExecution status so the designer test path remains visible', () => {
      const a = makeNode('step-a', {}, { __liveStatus: 'Running' });
      mockApiGet.mockResolvedValue({});
      const harness = setup({
        initialNodes: [a],
        liveExecution: { status: 'Succeeded', steps: [{ stepId: 'step-a', status: 'Succeeded' }] },
      });

      const updater = (harness.setNodes as any).mock.calls[0][0];
      const annotated: Node[] = updater([a]);
      const ann = annotated.find(n => n.id === 'step-a')!;
      expect((ann.data as Record<string, unknown>).__liveStatus).toBe('Succeeded');
    });

    it('clears __liveStatus when no live execution and no replay is active', () => {
      const a = makeNode('step-a', {}, { __liveStatus: 'Running' });
      mockApiGet.mockResolvedValue({});
      const harness = setup({
        initialNodes: [a],
        liveExecution: null,
      });

      const updater = (harness.setNodes as any).mock.calls[0][0];
      const annotated: Node[] = updater([a]);
      const ann = annotated.find(n => n.id === 'step-a')!;
      expect('__liveStatus' in (ann.data as Record<string, unknown>)).toBe(false);
    });

    it('paints replay steps when no live execution is active', () => {
      const a = makeNode('step-a');
      const b = makeNode('step-b');
      const replaySteps: StepExecution[] = [
        { stepId: 'step-a', status: 'Succeeded', startedAt: '2026-04-26T12:00:00Z', completedAt: '2026-04-26T12:00:01Z' } as StepExecution,
        { stepId: 'step-b', status: 'Failed', startedAt: '2026-04-26T12:00:02Z', completedAt: '2026-04-26T12:00:03Z' } as StepExecution,
      ];
      mockApiGet.mockResolvedValue({});

      const harness = setup({
        initialNodes: [a, b],
        liveExecution: null,
        replayExecutionId: 'exec-1',
        replaySteps,
      });

      const updater = (harness.setNodes as any).mock.calls[0][0];
      const annotated: Node[] = updater([a, b]);
      expect((annotated.find(n => n.id === 'step-a')!.data as Record<string, unknown>).__liveStatus).toBe('Succeeded');
      expect((annotated.find(n => n.id === 'step-b')!.data as Record<string, unknown>).__liveStatus).toBe('Failed');
    });

    it('honors scrubTimeMs by showing Running for steps that started but not finished by the cursor', () => {
      const a = makeNode('step-a');
      const startedAt = new Date('2026-04-26T12:00:00Z').getTime();
      const completedAt = new Date('2026-04-26T12:00:10Z').getTime();
      const replaySteps: StepExecution[] = [
        {
          stepId: 'step-a', status: 'Succeeded',
          startedAt: new Date(startedAt).toISOString(),
          completedAt: new Date(completedAt).toISOString(),
        } as StepExecution,
      ];
      mockApiGet.mockResolvedValue({});

      const harness = setup({
        initialNodes: [a],
        liveExecution: null,
        replayExecutionId: 'exec-1',
        replaySteps,
        scrubTimeMs: startedAt + 5_000, // 5s into a 10s step
      });

      const updater = (harness.setNodes as any).mock.calls[0][0];
      const annotated: Node[] = updater([a]);
      expect((annotated.find(n => n.id === 'step-a')!.data as Record<string, unknown>).__liveStatus).toBe('Running');
    });
  });

  describe('machine coloring', () => {
    it('legendMachines is empty when no remote-activity nodes have a fixed targetMachineId', () => {
      const harness = setup({
        initialNodes: [makeNode('step-a', {}, { activityType: 'delay' })],
      });

      expect(harness.result.current.legendMachines).toEqual([]);
    });

    it('legendMachines lists each unique target machine in stable color-assignment order', () => {
      const machines = [
        makeMachine('m-1', 'Web01', 'web01.local'),
        makeMachine('m-2', 'Db01', 'db01.local'),
      ];
      const harness = setup({
        initialNodes: [
          makeNode('step-a', {}, { activityType: 'runScript', targetMachineId: 'm-2' }),
          makeNode('step-b', {}, { activityType: 'folderOperation', targetMachineId: 'm-1' }),
          makeNode('step-c', {}, { activityType: 'runScript', targetMachineId: 'm-1' }), // dup
        ],
        machines,
      });

      // Sort is alphabetical on machine id; m-1 then m-2.
      expect(harness.result.current.legendMachines).toEqual([
        { id: 'm-1', name: 'Web01 (web01.local)', colorIdx: 0 },
        { id: 'm-2', name: 'Db01 (db01.local)', colorIdx: 1 },
      ]);
    });

    it('legendMachines ignores templated targetMachineId values like {{x.y}}', () => {
      const harness = setup({
        initialNodes: [
          makeNode('step-a', {}, { activityType: 'runScript', targetMachineId: '{{step-1.param.host}}' }),
        ],
        machines: [],
      });

      expect(harness.result.current.legendMachines).toEqual([]);
    });
  });

  describe('variable-flow hover annotation', () => {
    it('handleVarHover applies __varFlowRole=producer to the producer node and consumer to the selected node', () => {
      const producer = makeNode('step-prod');
      const consumer = makeNode('step-cons');
      const harness = setup({
        initialNodes: [producer, consumer],
        selected: { type: 'node', id: 'step-cons' },
      });

      // Initial setNodes call is the role-clear pass; trigger hover and capture the next call.
      (harness.setNodes as any).mockClear();
      act(() => { harness.result.current.handleVarHover('step-prod'); });

      const updater = (harness.setNodes as any).mock.calls.at(-1)[0];
      const annotated: Node[] = updater([producer, consumer]);
      expect((annotated.find(n => n.id === 'step-prod')!.data as Record<string, unknown>).__varFlowRole).toBe('producer');
      expect((annotated.find(n => n.id === 'step-cons')!.data as Record<string, unknown>).__varFlowRole).toBe('consumer');
    });

    it('handleVarHover(null) strips __varFlowRole that a prior hover set', () => {
      const producer = makeNode('step-prod');
      const consumer = makeNode('step-cons');
      const harness = setup({
        initialNodes: [producer, consumer],
        selected: { type: 'node', id: 'step-cons' },
      });

      // Set the role first.
      (harness.setNodes as any).mockClear();
      act(() => { harness.result.current.handleVarHover('step-prod'); });
      const setUpdater = (harness.setNodes as any).mock.calls.at(-1)[0];
      const annotatedAfterSet: Node[] = setUpdater([producer, consumer]);
      expect((annotatedAfterSet.find(n => n.id === 'step-prod')!.data as Record<string, unknown>).__varFlowRole).toBe('producer');

      // Now clear it.
      (harness.setNodes as any).mockClear();
      act(() => { harness.result.current.handleVarHover(null); });
      const clearUpdater = (harness.setNodes as any).mock.calls.at(-1)[0];
      const annotatedAfterClear: Node[] = clearUpdater(annotatedAfterSet);
      expect('__varFlowRole' in (annotatedAfterClear.find(n => n.id === 'step-prod')!.data as Record<string, unknown>)).toBe(false);
    });
  });

  describe('step-health/step-stats integration', () => {
    it('writes __health onto nodes when /step-health resolves with data', async () => {
      const a = makeNode('step-a');
      mockApiGet.mockImplementation((url: string) => {
        if (url.includes('/step-health')) return Promise.resolve({ 'step-a': [{ status: 'Succeeded', startedAt: '2026-04-26T12:00:00Z' }] });
        return Promise.resolve({});
      });

      const harness = setup({ initialNodes: [a] });

      // Wait for the useQuery to settle and useEffect to fire setNodes with the health annotation.
      await waitFor(() => {
        const calls = (harness.setNodes as any).mock.calls;
        const hadHealth = calls.some((c: any[]) => {
          const annotated = c[0]([a]);
          return Array.isArray(annotated) && (annotated[0]?.data as Record<string, unknown>)?.__health !== undefined;
        });
        expect(hadHealth).toBe(true);
      });
    });
  });

  describe('__workflowEnabled annotation', () => {
    it('marks every node with __workflowEnabled=false when the workflow is disabled', () => {
      const a = makeNode('step-a');
      const b = makeNode('step-b');
      const harness = setup({ initialNodes: [a, b], workflowIsEnabled: false });

      const updaters = (harness.setNodes as any).mock.calls.map((c: any[]) => c[0]);
      const annotated = updaters.reduce((nds: Node[], u: any) => u(nds), [a, b]);
      expect((annotated[0].data as Record<string, unknown>).__workflowEnabled).toBe(false);
      expect((annotated[1].data as Record<string, unknown>).__workflowEnabled).toBe(false);
    });

    it('does not annotate nodes when the workflow is enabled (enabled is the default)', () => {
      const a = makeNode('step-a', {}, { __workflowEnabled: true });
      const harness = setup({ initialNodes: [a], workflowIsEnabled: true });

      const updaters = (harness.setNodes as any).mock.calls.map((c: any[]) => c[0]);
      const annotated = updaters.reduce((nds: Node[], u: any) => u(nds), [a]);
      expect('__workflowEnabled' in (annotated[0].data as Record<string, unknown>)).toBe(false);
    });

    it('re-applies __workflowEnabled=false when nodes are externally rebuilt (lock-refetch flow)', () => {
      // Initial render: workflow disabled → annotation gets set.
      const a = makeNode('step-a');
      const harness = setup({ initialNodes: [a], workflowIsEnabled: false });

      // Simulate a workflow refetch: the editor rebuilds `nodes` from definitionJson,
      // producing fresh node references without our __workflowEnabled annotation.
      const aFresh = makeNode('step-a');
      harness.replaceNodes([aFresh]);
      // Re-render with the same workflowIsEnabled=false (locking doesn't flip enabled to true).
      harness.rerender({ workflowIsEnabled: false, liveExecution: null });

      const updaters = (harness.setNodes as any).mock.calls.map((c: any[]) => c[0]);
      const annotated = updaters.reduce((nds: Node[], u: any) => u(nds), [aFresh]);
      expect((annotated[0].data as Record<string, unknown>).__workflowEnabled).toBe(false);
    });
  });
});
