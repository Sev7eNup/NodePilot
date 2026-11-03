import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import type { Workflow } from '../../types/api';
import { api } from '../../api/client';
import { useWorkflowExecution } from '../../hooks/useWorkflowExecution';

afterEach(() => vi.restoreAllMocks());

it('does not execute another workflow when navigation wins a pending save', async () => {
  let finish!: () => void;
  const save = new Promise<void>((resolve) => { finish = resolve; });
  const post = vi.spyOn(api, 'post').mockResolvedValue({ id: 'execution-a' });
  const pin = vi.fn();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { result, rerender } = renderHook(({ id }) => useWorkflowExecution({
    workflowId: id, workflow: { id, isEnabled: true, definitionJson: '{"nodes":[],"edges":[]}' } as Workflow,
    canWrite: true, isDirty: true, nodes: [], edges: [], saveAsync: () => save,
    pinCanvasExecution: pin, clearReplay: vi.fn(),
  }), { initialProps: { id: 'a' }, wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider> });
  let pending!: Promise<void>;
  act(() => { pending = result.current.run(); });
  rerender({ id: 'b' });
  await act(async () => { finish(); await pending; });
  expect(post).not.toHaveBeenCalled();
  expect(pin).not.toHaveBeenCalled();
});

it('opens saved manual parameters before the published canvas has hydrated', async () => {
  const post = vi.spyOn(api, 'post');
  vi.spyOn(api, 'get').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 1 });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const workflow = {
    id: 'published', isEnabled: true,
    definitionJson: JSON.stringify({ nodes: [{ id: 'trigger', data: { activityType: 'manualTrigger', config: { parameters: [{ name: 'interval', type: 'string', default: '30' }] } } }], edges: [] }),
  } as Workflow;
  const { result, unmount } = renderHook(() => useWorkflowExecution({
    workflowId: workflow.id, workflow, canWrite: false, isDirty: false,
    nodes: [], edges: [], saveAsync: vi.fn(), pinCanvasExecution: vi.fn(), clearReplay: vi.fn(),
  }), { wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider> });
  await act(() => result.current.run());
  expect(result.current.showRunDialog).toBe(true);
  expect(post).not.toHaveBeenCalled();
  unmount();
  client.clear();
});
