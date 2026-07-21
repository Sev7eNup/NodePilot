import { describe, it, expect, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Workflow } from '../../types/api';

/**
 * Harness approach:
 * useWorkflowLock uses useMutation/useQueryClient, so renderHook is wrapped in a fresh
 * QueryClientProvider per test (matching the properties/useNodeAnnotations precedent). The api
 * client is mocked so an accidental mutation can never hit the network — but these tests only
 * assert the DERIVED lock state (isLocked/isLockedByMe/isLockedByOther/canWrite), which is
 * computed at render time and never fires a mutation. `canWrite` is the edit gate, so every
 * lock-owner × role combination is asserted.
 */
vi.mock('../../api/client', () => ({
  api: {
    get: vi.fn(),
    post: vi.fn().mockResolvedValue({}),
    put: vi.fn().mockResolvedValue({}),
    delete: vi.fn().mockResolvedValue({}),
  },
}));

import { useWorkflowLock } from '../../hooks/useWorkflowLock';

function makeWorkflow(checkedOutByUserId: string | null): Workflow {
  return { id: 'wf-1', name: 'WF', checkedOutByUserId } as Workflow;
}

function renderLock(args: {
  workflow: Workflow | undefined;
  currentUserId: string | null | undefined;
  roleCanWrite: boolean;
}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );
  return renderHook(
    () =>
      useWorkflowLock({
        workflowId: 'wf-1',
        workflow: args.workflow,
        currentUserId: args.currentUserId,
        roleCanWrite: args.roleCanWrite,
      }),
    { wrapper },
  );
}

describe('useWorkflowLock derived state', () => {
  it('reports no lock when checkedOutByUserId is null', () => {
    const { result } = renderLock({
      workflow: makeWorkflow(null),
      currentUserId: 'user-1',
      roleCanWrite: true,
    });

    expect(result.current.isLocked).toBe(false);
    expect(result.current.isLockedByMe).toBe(false);
    expect(result.current.isLockedByOther).toBe(false);
    expect(result.current.canWrite).toBe(false);
  });

  it('grants canWrite when locked by me and the role may write', () => {
    const { result } = renderLock({
      workflow: makeWorkflow('user-1'),
      currentUserId: 'user-1',
      roleCanWrite: true,
    });

    expect(result.current.isLocked).toBe(true);
    expect(result.current.isLockedByMe).toBe(true);
    expect(result.current.isLockedByOther).toBe(false);
    expect(result.current.canWrite).toBe(true);
  });

  it('denies canWrite when locked by me but the role is read-only (Viewer)', () => {
    const { result } = renderLock({
      workflow: makeWorkflow('user-1'),
      currentUserId: 'user-1',
      roleCanWrite: false,
    });

    expect(result.current.isLockedByMe).toBe(true);
    expect(result.current.isLockedByOther).toBe(false);
    // Role gate wins even though the current user holds the lock.
    expect(result.current.canWrite).toBe(false);
  });

  it('reports lockedByOther (and no write) when another user holds the lock', () => {
    const { result } = renderLock({
      workflow: makeWorkflow('user-2'),
      currentUserId: 'user-1',
      roleCanWrite: true,
    });

    expect(result.current.isLocked).toBe(true);
    expect(result.current.isLockedByMe).toBe(false);
    expect(result.current.isLockedByOther).toBe(true);
    expect(result.current.canWrite).toBe(false);
  });

  it('never reports lockedByMe when there is no current user id', () => {
    for (const currentUserId of [null, undefined] as const) {
      const { result } = renderLock({
        workflow: makeWorkflow('user-1'),
        currentUserId,
        roleCanWrite: true,
      });

      expect(result.current.isLocked).toBe(true);
      expect(result.current.isLockedByMe).toBe(false);
      // With an unknown viewer, an existing lock reads as held by another user.
      expect(result.current.isLockedByOther).toBe(true);
      expect(result.current.canWrite).toBe(false);
    }
  });
});
