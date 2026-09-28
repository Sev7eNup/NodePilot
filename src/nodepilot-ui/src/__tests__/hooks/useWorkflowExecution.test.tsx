import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import type { Workflow } from '../../types/api';
import { api } from '../../api/client';
import { useWorkflowExecution } from '../../hooks/useWorkflowExecution';

afterEach(() => vi.restoreAllMocks());

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
