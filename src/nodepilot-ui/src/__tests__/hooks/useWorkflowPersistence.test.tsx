import * as React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

// Control the react-router `useBlocker` return value per test. The hook owns the in-app
// navigation discard-confirm; WorkflowEditorPage.test.tsx stubs useBlocker to 'unblocked'
// (it runs under MemoryRouter, which has no data router), so the blocked→confirm→proceed/reset
// branch is exercised here instead.
const routerMock = vi.hoisted(() => ({
  blocker: { state: 'unblocked' as string, proceed: vi.fn(), reset: vi.fn() },
}));
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useBlocker: () => routerMock.blocker };
});

// Store-driven confirm replaces the native confirm(); default-resolve true (user confirms).
vi.mock('../../stores/confirmStore', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../../stores/confirmStore')>();
  return { ...mod, confirmDialog: vi.fn().mockResolvedValue(true) };
});
import { confirmDialog } from '../../stores/confirmStore';

import { useWorkflowPersistence } from '../../hooks/useWorkflowPersistence';

function makeWrapper() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
}

function renderPersistence() {
  return renderHook(
    () => useWorkflowPersistence({ workflowId: 'wf-1', workflow: undefined, nodes: [], edges: [] }),
    { wrapper: makeWrapper() },
  );
}

describe('useWorkflowPersistence — useBlocker discard guard', () => {
  beforeEach(() => {
    // Fresh spies + a blocked navigation for each test; the effect fires on mount.
    routerMock.blocker = { state: 'blocked', proceed: vi.fn(), reset: vi.fn() };
    vi.mocked(confirmDialog).mockClear();
  });
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('confirming the discard proceeds with the blocked navigation', async () => {
    renderPersistence();
    // confirmDialog resolves async — the blocker stays 'blocked' until proceed() is called.
    await waitFor(() => expect(routerMock.blocker.proceed).toHaveBeenCalledTimes(1));
    expect(confirmDialog).toHaveBeenCalledTimes(1);
    expect(routerMock.blocker.reset).not.toHaveBeenCalled();
  });

  it('cancelling the discard resets the blocker and stays put', async () => {
    vi.mocked(confirmDialog).mockResolvedValueOnce(false);
    renderPersistence();
    await waitFor(() => expect(routerMock.blocker.reset).toHaveBeenCalledTimes(1));
    expect(confirmDialog).toHaveBeenCalledTimes(1);
    expect(routerMock.blocker.proceed).not.toHaveBeenCalled();
  });
});
