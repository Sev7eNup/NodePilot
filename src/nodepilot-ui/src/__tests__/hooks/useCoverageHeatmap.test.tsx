import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Node } from '@xyflow/react';
import { useCoverageHeatmap } from '../../hooks/useCoverageHeatmap';

/**
 * Pin:
 * - When enabled, the hook stamps __coverage onto activity nodes with the right class.
 * - Trigger nodes (type !== 'activity') do not get __coverage attached.
 * - Toggle off clears __coverage so stale tints don't bleed.
 * - "Never executed" + "rare" + "common" classification follows the 25% threshold.
 */

const fetchMock = vi.fn();

beforeEach(() => {
  fetchMock.mockReset();
  vi.spyOn(globalThis, 'fetch').mockImplementation((...args) => fetchMock(...args));
});

afterEach(() => {
  vi.restoreAllMocks();
});

function jsonResponse(body: unknown) {
  return new Response(JSON.stringify(body), {
    status: 200, headers: { 'Content-Type': 'application/json' },
  });
}

function wrap(qc: QueryClient) {
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );
}

describe('useCoverageHeatmap', () => {
  it('stamps __coverage onto activity nodes with correct classification', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({
      workflowId: 'wf1',
      windowDays: 30,
      totalExecutions: 100,
      oldestExecutionInWindow: '2026-04-08T00:00:00Z',
      nodes: [
        { stepId: 'a', executedCount: 100, failedCount: 0, skippedCount: 0,
          lastExecutedAt: '2026-05-08', lastSucceededAt: '2026-05-08', lastFailedAt: null },
        { stepId: 'b', executedCount: 5, failedCount: 1, skippedCount: 95,
          lastExecutedAt: '2026-05-01', lastSucceededAt: '2026-05-01', lastFailedAt: '2026-04-22' },
        { stepId: 'c', executedCount: 0, failedCount: 0, skippedCount: 100,
          lastExecutedAt: null, lastSucceededAt: null, lastFailedAt: null },
      ],
    }));

    let nodes: Node[] = [
      { id: 'a', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'A' } },
      { id: 'b', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'B' } },
      { id: 'c', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'C' } },
      { id: 't', type: 'trigger', position: { x: 0, y: 0 }, data: { label: 'Trigger' } },
    ];
    const setNodes: React.Dispatch<React.SetStateAction<Node[]>> = (updater) => {
      nodes = typeof updater === 'function' ? updater(nodes) : updater;
    };

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderHook(
      () => useCoverageHeatmap({
        workflowId: 'wf1',
        enabled: true,
        windowDays: 30,
        setNodes,
      }),
      { wrapper: wrap(qc) },
    );

    await waitFor(() => {
      expect(nodes.find((n) => n.id === 'a')?.data.__coverage).toBeDefined();
    });

    const a = nodes.find((n) => n.id === 'a')!;
    const b = nodes.find((n) => n.id === 'b')!;
    const c = nodes.find((n) => n.id === 'c')!;
    const t = nodes.find((n) => n.id === 't')!;

    expect((a.data.__coverage as { cls: string }).cls).toBe('common');
    expect((b.data.__coverage as { cls: string }).cls).toBe('rare');
    expect((c.data.__coverage as { cls: string }).cls).toBe('never');
    // Non-activity node (trigger) MUST NOT receive coverage data.
    expect(t.data.__coverage).toBeUndefined();
  });

  it('clears __coverage when disabled', async () => {
    let nodes: Node[] = [
      { id: 'a', type: 'activity', position: { x: 0, y: 0 },
        data: { __coverage: { cls: 'never', executedCount: 0, totalExecutions: 100 } } },
    ];
    const setNodes: React.Dispatch<React.SetStateAction<Node[]>> = (updater) => {
      nodes = typeof updater === 'function' ? updater(nodes) : updater;
    };

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderHook(
      () => useCoverageHeatmap({
        workflowId: 'wf1',
        enabled: false,
        windowDays: 30,
        setNodes,
      }),
      { wrapper: wrap(qc) },
    );

    await waitFor(() => {
      expect(nodes.find((n) => n.id === 'a')?.data.__coverage).toBeUndefined();
    });
    // Disabled hook must not fire any fetch.
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
