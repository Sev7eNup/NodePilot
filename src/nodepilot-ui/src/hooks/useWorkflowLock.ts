import { useCallback } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../api/client';
import type { Workflow } from '../types/api';

interface UseWorkflowLockArgs {
  workflowId: string | undefined;
  /** The loaded workflow. Drives the derived lock state. */
  workflow: Workflow | undefined;
  currentUserId: string | null | undefined;
  /** Whether the current role may edit at all (Admin/Operator). */
  roleCanWrite: boolean;
}

/**
 * Edit-lock lifecycle for a workflow, kept separate from save/dirty (see
 * useWorkflowPersistence). Owns the lock, unlock, force-unlock, disable and enable mutations
 * and exposes only intent commands, their pending flags and the derived lock state, so callers
 * never handle a raw TanStack mutation.
 */
export function useWorkflowLock({ workflowId, workflow, currentUserId, roleCanWrite }: UseWorkflowLockArgs) {
  const queryClient = useQueryClient();
  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['workflows'] });
    queryClient.invalidateQueries({ queryKey: ['workflow', workflowId] });
  };
  const lockMutation = useMutation({ mutationFn: () => api.post<Workflow>(`/workflows/${workflowId}/lock`, {}), onSuccess: invalidate });
  const unlockMutation = useMutation({ mutationFn: () => api.post<Workflow>(`/workflows/${workflowId}/unlock`, {}), onSuccess: invalidate });
  const forceUnlockMutation = useMutation({ mutationFn: () => api.post<Workflow>(`/workflows/${workflowId}/force-unlock`, {}), onSuccess: invalidate });
  const disableMutation = useMutation({ mutationFn: () => api.post(`/workflows/${workflowId}/disable`, {}), onSuccess: invalidate });
  // Re-enables a disabled workflow without taking the edit-lock, for the Publish/Disable toggle.
  // /enable returns 423 while any user holds the lock, so callers disable the button in that state.
  const enableMutation = useMutation({ mutationFn: () => api.post(`/workflows/${workflowId}/enable`, {}), onSuccess: invalidate });

  // Edit-lock state derived from the loaded workflow. `canWrite` requires the current user to
  // hold the lock: a workflow must be checked out before it can be edited, so an editor without
  // the lock still sees a read-only canvas.
  const isLocked = !!workflow?.checkedOutByUserId;
  const isLockedByMe = isLocked && !!currentUserId && workflow!.checkedOutByUserId === currentUserId;
  const isLockedByOther = isLocked && !isLockedByMe;
  const canWrite = roleCanWrite && isLockedByMe;

  const lock = useCallback(() => { lockMutation.mutate(); }, [lockMutation]);
  const unlock = useCallback(() => { unlockMutation.mutate(); }, [unlockMutation]);
  const forceUnlock = useCallback(() => { forceUnlockMutation.mutate(); }, [forceUnlockMutation]);
  const disable = useCallback(() => { disableMutation.mutate(); }, [disableMutation]);
  const enable = useCallback(() => { enableMutation.mutate(); }, [enableMutation]);

  return {
    isLocked,
    isLockedByMe,
    isLockedByOther,
    canWrite,
    lock,
    unlock,
    forceUnlock,
    disable,
    enable,
    isLocking: lockMutation.isPending,
    isUnlocking: unlockMutation.isPending,
    isForceUnlocking: forceUnlockMutation.isPending,
    isDisabling: disableMutation.isPending,
    isEnabling: enableMutation.isPending,
  };
}
