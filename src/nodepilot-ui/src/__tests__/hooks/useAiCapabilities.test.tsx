import * as React from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { getKnowledgeCapabilities, type KnowledgeCapabilities } from '../../api/ai';
import { useAiCapabilities, refreshAiCapabilities, AI_CAPABILITIES_QUERY_KEY } from '../../hooks/useAiCapabilities';

vi.mock('../../api/ai', async (orig) => {
  const actual = await orig<typeof import('../../api/ai')>();
  return { ...actual, getKnowledgeCapabilities: vi.fn() };
});
const capsMock = getKnowledgeCapabilities as unknown as ReturnType<typeof vi.fn>;

const CAPS: KnowledgeCapabilities = {
  enabled: true, llm: true, docs: true, operational: false, sourceCode: false, db: false,
};

function makeWrapper(client: QueryClient) {
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
}

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('useAiCapabilities', () => {
  it('fetchesAndCaches_underTheSharedQueryKey', async () => {
    capsMock.mockResolvedValue(CAPS);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { result } = renderHook(() => useAiCapabilities(), { wrapper: makeWrapper(client) });

    await waitFor(() => expect(result.current.data).toEqual(CAPS));
    // Cache-sharing contract: the sidebar, AiChatPage and every gated button read one entry.
    expect(client.getQueryData(AI_CAPABILITIES_QUERY_KEY)).toEqual(CAPS);
    expect(AI_CAPABILITIES_QUERY_KEY).toEqual(['ai-knowledge-capabilities']);
  });
});

describe('refreshAiCapabilities', () => {
  it('invalidatesImmediately_andAgainAfterTheOptionsMonitorGrace', () => {
    vi.useFakeTimers();
    const client = new QueryClient();
    const invalidate = vi.spyOn(client, 'invalidateQueries').mockResolvedValue();

    refreshAiCapabilities(client);
    expect(invalidate).toHaveBeenCalledTimes(1);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: AI_CAPABILITIES_QUERY_KEY });

    // A second invalidation runs after the backend's runtime.json watcher has fired.
    vi.advanceTimersByTime(2000);
    expect(invalidate).toHaveBeenCalledTimes(2);
    expect(invalidate).toHaveBeenLastCalledWith({ queryKey: AI_CAPABILITIES_QUERY_KEY });
  });
});
