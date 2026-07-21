import { useCallback } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../api/client';
import type { Workflow } from '../types/api';

interface UseWorkflowLockArgs {
  workflowId: string | undefined;
  /** The loaded workflow — drives the derived lock state. */
  workflow: Workflow | undefined;
  currentUserId: string | null | undefined;
  /** Whether the current role may edit at all (Admin/Operator). */
  roleCanWrite: boolean;
}

/**
 * Edit-lock lifecycle for a workflow, kept separate from save/dirty (see
 * useWorkflowPersistence). Owns the lock/unlock/force-unlock/disable/enable mutations
 * *internally* and exposes only intent commands + their pending flags plus the derived
 * lock state — callers (page + EditorHeader/EditorStatusBanners/CommandPalette) never see
 * a raw TanStack mutation, so the module isn't a shallow pass-through.
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
  // Re-enable a Disabled workflow without going through the edit-lock — used by the
  // Publish/Disable toggle when the workflow is disabled but nobody has it checked out.
  // /enable returns 423 if any user holds the lock, so callers disable the button in that
  // state to avoid a wasted round-trip.
  const enableMutation = useMutation({ mutationFn: () => api.post(`/workflows/${workflowId}/enable`, {}), onSuccess: invalidate });

  // Edit-lock state, derived from the loaded workflow. `canWrite` only flips on when the
  // current user holds the lock — non-Viewers without a lock still see a read-only canvas
  // (consistent with the SCOrch-style "must Check Out before editing" rule).
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
