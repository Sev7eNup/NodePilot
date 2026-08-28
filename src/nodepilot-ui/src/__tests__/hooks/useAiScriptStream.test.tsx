import * as React from 'react';
import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { KnowledgeCapabilities } from '../../api/ai';
import { AI_CAPABILITIES_QUERY_KEY } from '../../hooks/useAiCapabilities';
import { useAiScriptStream } from '../../hooks/useAiScriptStream';
import { useAuthStore } from '../../stores/authStore';

// The hook returns the streaming callback only when the AI button may be shown: the LLM
// endpoint is usable and the caller is not a Viewer (POST /api/ai/generate-script is
// Admin/Operator-only). `undefined` reaches ScriptEditorDialog.onAiGenerate, and the
// missing callback hides the button.

function caps(llm: boolean): KnowledgeCapabilities {
  return { enabled: false, llm, docs: false, operational: false, sourceCode: false, db: false, scriptContextTargetHost: 'llm.example.test' };
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
});
