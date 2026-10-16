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

import { useNodeAnnotations, type NodeAnnotation } from '../../hooks/useNodeAnnotations';
import { useDesignStore } from '../../stores/designStore';

type LiveExecutionArg = { status: string; steps: { stepId: string; status: string }[] } | null;

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

interface HookProps {
  nodes: Node[];
  edges: Edge[];
  workflowIsEnabled: boolean;
  liveExecution: LiveExecutionArg;
}

function setup({
  initialNodes = [] as Node[],
  initialEdges = [] as Edge[],
  workflowId = 'wf-1' as string | undefined,
  workflowIsEnabled = true as boolean,
  liveExecution = null as LiveExecutionArg,
  replayExecutionId = null as string | null,
  replaySteps = undefined as StepExecution[] | undefined,
  scrubTimeMs = null as number | null,
  machines = [] as ManagedMachine[],
  selected = null as { type: 'node' | 'edge'; id: string } | null,
} = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );

  const { result, rerender } = renderHook(
    (props: HookProps) =>
      useNodeAnnotations({
        workflowId, workflowIsEnabled: props.workflowIsEnabled,
        nodes: props.nodes, edges: props.edges, selected,
        liveExecution: props.liveExecution, replayExecutionId, replaySteps,
        scrubTimeMs, machines,
      }),
    { wrapper, initialProps: { nodes: initialNodes, edges: initialEdges, workflowIsEnabled, liveExecution } },
  );

  return {
    result, rerender,
    annotationOf: (id: string): NodeAnnotation | undefined => result.current.nodeAnnotations.get(id),
  };
}

describe('useNodeAnnotations', () => {
  beforeEach(() => {
    mockApiGet.mockReset();
    mockApiGet.mockResolvedValue({});
    // Reset persisted Zustand store between tests so toggles don't leak.
    useDesignStore.setState({ machineColoringEnabled: false });
  });

  it('never touches the node data it is given', async () => {
    mockApiGet.mockImplementation((url: string) => {
      if (url.includes('/step-health')) return Promise.resolve({ 'step-a': [{ status: 'Succeeded', startedAt: '2026-04-26T12:00:00Z' }] });
      if (url.includes('/step-stats')) return Promise.resolve({ 'step-a': { totalRuns: 1, failedRuns: 0, failureRate: 0, avgDurationMs: 5, p95DurationMs: 5, lastDurationMs: 5 } });
      return Promise.resolve({});
    });
    const a = makeNode('step-a');
    const harness = setup({
      initialNodes: [a],
      workflowIsEnabled: false,
      liveExecution: { status: 'Running', steps: [{ stepId: 'step-a', status: 'Running' }] },
    });

    await waitFor(() => expect(harness.annotationOf('step-a')?.__stats).toBeDefined());
    expect(harness.annotationOf('step-a')?.__liveStatus).toBe('Running');
    expect(a.data).toEqual({ activityType: 'runScript' });
  });

  describe('liveStatus annotation', () => {
    it('annotates each step of the live execution', () => {
      const harness = setup({
        initialNodes: [makeNode('step-a'), makeNode('step-b')],
        liveExecution: { status: 'Running', steps: [{ stepId: 'step-a', status: 'Running' }] },
      });

      expect(harness.annotationOf('step-a')?.__liveStatus).toBe('Running');
      expect(harness.annotationOf('step-b')?.__liveStatus).toBeUndefined();
    });

    it('paints liveExecution changes immediately for designer-started canvas runs', () => {
      const nodes = [makeNode('step-a')];
      const harness = setup({ initialNodes: nodes, liveExecution: null });
      expect(harness.annotationOf('step-a')?.__liveStatus).toBeUndefined();

      harness.rerender({
        nodes, edges: [], workflowIsEnabled: true,
        liveExecution: { status: 'Running', steps: [{ stepId: 'step-a', status: 'Running' }] },
      });

      expect(harness.annotationOf('step-a')?.__liveStatus).toBe('Running');
    });

    it('keeps terminal liveExecution status so the designer test path remains visible', () => {
      const harness = setup({
        initialNodes: [makeNode('step-a')],
        liveExecution: { status: 'Succeeded', steps: [{ stepId: 'step-a', status: 'Succeeded' }] },
      });

      expect(harness.annotationOf('step-a')?.__liveStatus).toBe('Succeeded');
    });

    it('has no live status when no live execution and no replay is active', () => {
      const harness = setup({ initialNodes: [makeNode('step-a')], liveExecution: null });

      expect(harness.annotationOf('step-a')?.__liveStatus).toBeUndefined();
    });

    it('drops the live status once the execution is gone', () => {
      const nodes = [makeNode('step-a')];
      const harness = setup({
        initialNodes: nodes,
        liveExecution: { status: 'Running', steps: [{ stepId: 'step-a', status: 'Running' }] },
      });

      harness.rerender({ nodes, edges: [], workflowIsEnabled: true, liveExecution: null });

      expect(harness.annotationOf('step-a')?.__liveStatus).toBeUndefined();
    });

    it('paints replay steps when no live execution is active', () => {
      const replaySteps: StepExecution[] = [
        { stepId: 'step-a', status: 'Succeeded', startedAt: '2026-04-26T12:00:00Z', completedAt: '2026-04-26T12:00:01Z' } as StepExecution,
        { stepId: 'step-b', status: 'Failed', startedAt: '2026-04-26T12:00:02Z', completedAt: '2026-04-26T12:00:03Z' } as StepExecution,
      ];

      const harness = setup({
        initialNodes: [makeNode('step-a'), makeNode('step-b')],
        liveExecution: null,
        replayExecutionId: 'exec-1',
        replaySteps,
      });

      expect(harness.annotationOf('step-a')?.__liveStatus).toBe('Succeeded');
      expect(harness.annotationOf('step-b')?.__liveStatus).toBe('Failed');
    });

    it('honors scrubTimeMs by showing Running for steps that started but not finished by the cursor', () => {
      const startedAt = new Date('2026-04-26T12:00:00Z').getTime();
      const completedAt = new Date('2026-04-26T12:00:10Z').getTime();
      const replaySteps: StepExecution[] = [
        {
          stepId: 'step-a', status: 'Succeeded',
          startedAt: new Date(startedAt).toISOString(),
          completedAt: new Date(completedAt).toISOString(),
        } as StepExecution,
      ];

      const harness = setup({
        initialNodes: [makeNode('step-a')],
        liveExecution: null,
        replayExecutionId: 'exec-1',
        replaySteps,
        scrubTimeMs: startedAt + 5_000, // 5s into a 10s step
      });

      expect(harness.annotationOf('step-a')?.__liveStatus).toBe('Running');
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

    it('annotates the color index only while machine coloring is on', () => {
      const nodes = [
        makeNode('step-a', {}, { activityType: 'runScript', targetMachineId: 'm-2' }),
        makeNode('step-b', {}, { activityType: 'runScript', targetMachineId: 'm-1' }),
        makeNode('step-c', {}, { activityType: 'delay', targetMachineId: 'm-1' }),
      ];
      const harness = setup({ initialNodes: nodes });
      expect(harness.annotationOf('step-a')?.__machineColorIdx).toBeUndefined();

      act(() => { useDesignStore.setState({ machineColoringEnabled: true }); });

      expect(harness.annotationOf('step-a')?.__machineColorIdx).toBe(1);
      expect(harness.annotationOf('step-b')?.__machineColorIdx).toBe(0);
      // Engine-local activities run on the server, so they get no machine colour.
      expect(harness.annotationOf('step-c')?.__machineColorIdx).toBeUndefined();
    });
  });

  describe('variable-flow hover annotation', () => {
    it('handleVarHover marks the producer and the selected consumer, plus the path between them', () => {
      const edges: Edge[] = [{ id: 'e1', source: 'step-prod', target: 'step-cons' }];
      const harness = setup({
        initialNodes: [makeNode('step-prod'), makeNode('step-cons')],
        initialEdges: edges,
        selected: { type: 'node', id: 'step-cons' },
      });

      act(() => { harness.result.current.handleVarHover('step-prod'); });

      expect(harness.annotationOf('step-prod')?.__varFlowRole).toBe('producer');
      expect(harness.annotationOf('step-cons')?.__varFlowRole).toBe('consumer');
      expect([...harness.result.current.varFlowEdgeIds]).toEqual(['e1']);
    });

    it('handleVarHover(null) clears the roles and the highlighted path', () => {
      const edges: Edge[] = [{ id: 'e1', source: 'step-prod', target: 'step-cons' }];
      const harness = setup({
        initialNodes: [makeNode('step-prod'), makeNode('step-cons')],
        initialEdges: edges,
        selected: { type: 'node', id: 'step-cons' },
      });

      act(() => { harness.result.current.handleVarHover('step-prod'); });
      act(() => { harness.result.current.handleVarHover(null); });

      expect(harness.annotationOf('step-prod')?.__varFlowRole).toBeUndefined();
      expect(harness.annotationOf('step-cons')?.__varFlowRole).toBeUndefined();
      expect(harness.result.current.varFlowEdgeIds.size).toBe(0);
    });
  });

  describe('step-health/step-stats integration', () => {
    it('annotates __health when /step-health resolves with data', async () => {
      mockApiGet.mockImplementation((url: string) => {
        if (url.includes('/step-health')) return Promise.resolve({ 'step-a': [{ status: 'Succeeded', startedAt: '2026-04-26T12:00:00Z' }] });
        return Promise.resolve({});
      });

      const harness = setup({ initialNodes: [makeNode('step-a')] });

      await waitFor(() => expect(harness.annotationOf('step-a')?.__health).toEqual(
        [{ status: 'Succeeded', startedAt: '2026-04-26T12:00:00Z' }],
      ));
    });

    it('annotates a node rebuilt from the server at once, without refetching', async () => {
      // After save/publish/lock the editor rebuilds `nodes` from definitionJson. The annotations
      // follow the node ids, so the fresh nodes carry them immediately.
      mockApiGet.mockImplementation((url: string) => {
        if (url.includes('/step-health')) return Promise.resolve({ 'step-a': [{ status: 'Failed', startedAt: '2026-04-26T12:00:00Z' }] });
        if (url.includes('/step-stats')) return Promise.resolve({ 'step-a': { totalRuns: 3, failedRuns: 1, failureRate: 0.33, avgDurationMs: 10, p95DurationMs: 20, lastDurationMs: 12 } });
        return Promise.resolve({});
      });

      const harness = setup({ initialNodes: [makeNode('step-a')] });
      await waitFor(() => {
        expect(harness.annotationOf('step-a')?.__health).toBeDefined();
        expect(harness.annotationOf('step-a')?.__stats).toBeDefined();
      });
      const requestsBefore = mockApiGet.mock.calls.length;

      harness.rerender({ nodes: [makeNode('step-a')], edges: [], workflowIsEnabled: true, liveExecution: null });

      expect(harness.annotationOf('step-a')?.__health).toBeDefined();
      expect(harness.annotationOf('step-a')?.__stats).toBeDefined();
      expect(harness.result.current.stepStats?.['step-a'].p95DurationMs).toBe(20);
      // Same node ids give the same query key, so the data is served from cache.
      expect(mockApiGet.mock.calls.length).toBe(requestsBefore);
    });
  });

  describe('__workflowEnabled annotation', () => {
    it('marks every node with __workflowEnabled=false when the workflow is disabled', () => {
      const harness = setup({ initialNodes: [makeNode('step-a'), makeNode('step-b')], workflowIsEnabled: false });

      expect(harness.annotationOf('step-a')?.__workflowEnabled).toBe(false);
      expect(harness.annotationOf('step-b')?.__workflowEnabled).toBe(false);
    });

    it('does not annotate nodes when the workflow is enabled (enabled is the default)', () => {
      const harness = setup({ initialNodes: [makeNode('step-a')], workflowIsEnabled: true });

      expect(harness.annotationOf('step-a')).toBeUndefined();
    });

    it('marks nodes rebuilt from the server (lock-refetch flow)', () => {
      const harness = setup({ initialNodes: [makeNode('step-a')], workflowIsEnabled: false });

      harness.rerender({ nodes: [makeNode('step-a')], edges: [], workflowIsEnabled: false, liveExecution: null });

      expect(harness.annotationOf('step-a')?.__workflowEnabled).toBe(false);
    });
  });
});
