import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Node } from '@xyflow/react';
import { useCoverageHeatmap } from '../../hooks/useCoverageHeatmap';

/**
 * Covers:
 * - When enabled, the hook derives a coverage annotation per activity node with the matching class.
 * - Trigger nodes (type !== 'activity') never receive one.
 * - Disabled, the hook returns null and fetches nothing, so no stale tint can linger.
 * - The never, rare and common classification follows the 25% threshold.
 * - The node data itself is never written.
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
  it('derives a coverage annotation per activity node with the correct classification', async () => {
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

    const nodes: Node[] = [
      { id: 'a', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'A' } },
      { id: 'b', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'B' } },
      { id: 'c', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'C' } },
      { id: 't', type: 'trigger', position: { x: 0, y: 0 }, data: { label: 'Trigger' } },
    ];

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { result } = renderHook(
      () => useCoverageHeatmap({ workflowId: 'wf1', enabled: true, windowDays: 30, nodes }),
      { wrapper: wrap(qc) },
    );

    await waitFor(() => expect(result.current).not.toBeNull());
    const coverage = result.current!;

    expect(coverage.get('a')?.cls).toBe('common');
    expect(coverage.get('b')?.cls).toBe('rare');
    expect(coverage.get('b')?.failedCount).toBe(1);
    expect(coverage.get('c')?.cls).toBe('never');
    // A trigger is not an activity node, so it receives no coverage data.
    expect(coverage.has('t')).toBe(false);
    for (const n of nodes) expect(n.data).not.toHaveProperty('__coverage');
  });

  it('keeps the same annotations when only node positions change', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({
      workflowId: 'wf1', windowDays: 30, totalExecutions: 0, oldestExecutionInWindow: null, nodes: [],
    }));
    const nodes: Node[] = [{ id: 'a', type: 'activity', position: { x: 0, y: 0 }, data: {} }];
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { result, rerender } = renderHook(
      (p: { nodes: Node[] }) => useCoverageHeatmap({ workflowId: 'wf1', enabled: true, windowDays: 30, nodes: p.nodes }),
      { wrapper: wrap(qc), initialProps: { nodes } },
    );
    await waitFor(() => expect(result.current).not.toBeNull());
    const first = result.current;

    rerender({ nodes: nodes.map((n) => ({ ...n, position: { x: 40, y: 10 } })) });

    expect(result.current).toBe(first);
  });

  it('returns null and fetches nothing when disabled', () => {
    const nodes: Node[] = [{ id: 'a', type: 'activity', position: { x: 0, y: 0 }, data: {} }];

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { result } = renderHook(
      () => useCoverageHeatmap({ workflowId: 'wf1', enabled: false, windowDays: 30, nodes }),
      { wrapper: wrap(qc) },
    );

    expect(result.current).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
