import * as React from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { aiApi, type KnowledgeCapabilities } from '../../api/ai';
import { api, ApiError } from '../../api/client';
import { AI_CAPABILITIES_QUERY_KEY } from '../../hooks/useAiCapabilities';
import { useAiScriptStream } from '../../hooks/useAiScriptStream';
import { useAuthStore } from '../../stores/authStore';

// The hook returns the streaming callback only when the AI button may be shown: the LLM
// endpoint is usable and the caller is not a Viewer (POST /api/ai/generate-script is
// Admin/Operator-only). `undefined` reaches ScriptEditorDialog.onAiGenerate, and the
// missing callback hides the button.

function caps(llm: boolean): KnowledgeCapabilities {
  return { enabled: false, llm, docs: false, operational: false, sourceCode: false, db: false,
    scriptContextTargetHost: 'llm.example.test', scriptContextRecipientKey: 'recipient-before-change' };
}

// The cache is seeded up front, so the query never fetches and no MSW handler is needed.
function renderStreamHook(role: 'Admin' | 'Operator' | 'Viewer', llm: boolean) {
  useAuthStore.setState({ isAuthenticated: true, username: 'tester', role });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(AI_CAPABILITIES_QUERY_KEY, caps(llm));
  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return renderHook(
    () => useAiScriptStream({ workflowId: 'wf-1', stepId: 'step-1', upstreamVars: [] }),
    { wrapper },
  );
}

describe('useAiScriptStream — gating', () => {
  afterEach(() => vi.restoreAllMocks());
  beforeEach(() => {
    useAuthStore.setState({ isAuthenticated: false, username: null, role: null });
  });

  it('operatorWithUsableLlm_returnsTheCallback', () => {
    const { result } = renderStreamHook('Operator', true);
    expect(typeof result.current?.generate).toBe('function');
    expect(result.current?.targetHost).toBe('llm.example.test');
  });

  it('llmNotUsable_returnsUndefined', () => {
    const { result } = renderStreamHook('Admin', false);
    expect(result.current).toBeUndefined();
  });

  it('viewer_returnsUndefined_evenWithUsableLlm', () => {
    const { result } = renderStreamHook('Viewer', true);
    expect(result.current).toBeUndefined();
  });

  it('sends the acknowledged recipient with shared editor content and omits it without sharing', async () => {
    const generate = vi.spyOn(aiApi, 'generateScriptStream').mockResolvedValue();
    const { result } = renderStreamHook('Operator', true);
    await act(async () => result.current!.generate('Refactor', 'Get-Date', vi.fn(), new AbortController().signal, 'acknowledged-key'));
    expect(generate.mock.calls[0][0]).toMatchObject({
      currentScript: 'Get-Date', includeCurrentScript: true, expectedRecipientKey: 'acknowledged-key',
    });
    await act(async () => result.current!.generate('Create', null, vi.fn(), new AbortController().signal, 'acknowledged-key'));
    expect(generate.mock.calls[1][0]).toMatchObject({
      currentScript: null, includeCurrentScript: false, expectedRecipientKey: null,
    });
  });

  it('refreshes capabilities after a recipient conflict and preserves the error for renewed consent', async () => {
    const refresh = vi.spyOn(api, 'get').mockResolvedValue({
      ...caps(true), scriptContextTargetHost: 'new.example.test', scriptContextRecipientKey: 'new-recipient',
    });
    const conflict = new ApiError('Recipient changed', 409, 'LLM_RECIPIENT_CHANGED');
    vi.spyOn(aiApi, 'generateScriptStream').mockRejectedValue(conflict);
    const { result } = renderStreamHook('Operator', true);
    await act(async () => {
      await expect(result.current!.generate('Refactor', 'Get-Date', vi.fn(), new AbortController().signal, 'old-key')).rejects.toBe(conflict);
    });
    await waitFor(() => expect(result.current?.recipientKey).toBe('new-recipient'));
    expect(refresh).toHaveBeenCalledWith('/ai/knowledge/capabilities');
    expect(result.current?.targetHost).toBe('new.example.test');
  });
});
