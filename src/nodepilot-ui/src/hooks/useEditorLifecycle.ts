import { useTranslation } from 'react-i18next';
import type { Node, Edge } from '@xyflow/react';
import type { Workflow } from '../types/api';
import { lintWorkflow, type LintResult } from '../lib/workflowLint';
import { getPrePublishLint } from '../lib/prePublishChecks';
import { confirmDialog } from '../stores/confirmStore';
import type { useWorkflowLock } from './useWorkflowLock';
import type { useWorkflowPersistence } from './useWorkflowPersistence';

/**
 * Edit-lock, save and publish state of the open workflow with its actions, computed once per
 * render. The header, the command palette and the banners read the same object, so a flag cannot
 * mean different things in different places.
 */
export interface EditorLifecycle {
  roleCanWrite: boolean;
  /** Holds the lock and no publish or async layout owns the draft. */
  canWrite: boolean;
  isAdmin: boolean;
  isDirty: boolean;
  isSaving: boolean;
  isLockedByMe: boolean;
  isLockedByOther: boolean;
  isLocking: boolean;
  /** Also true while an async layout owns the draft. */
  isUnlocking: boolean;
  /** Also true while an async layout owns the draft. */
  isPublishing: boolean;
  isEnabling: boolean;
  isDisabling: boolean;
  isForceUnlocking: boolean;
  save: () => void;
  lock: () => void;
  unlock: () => void;
  forceUnlock: () => void;
  disable: () => void;
  /**
   * Single entry point for publish (locked by me) and enable (no lock). Runs the pre-publish
   * checklist first; on a productive workflow it asks to disable instead.
   */
  requestPublish: () => void;
}

interface UseEditorLifecycleArgs {
  workflow: Workflow | undefined;
  roleCanWrite: boolean;
  isAdmin: boolean;
  canWrite: boolean;
  isTidying: boolean;
  lockState: ReturnType<typeof useWorkflowLock>;
  persistence: Pick<ReturnType<typeof useWorkflowPersistence>, 'isDirty' | 'isSaving' | 'isPublishing' | 'save' | 'publish'>;
  /** The graph as it is right now, for the pre-publish lint. */
  getLiveGraph: () => { nodes: Node[]; edges: Edge[] };
  allWorkflows: Array<{ id: string; name: string }> | undefined;
  openPrePublish: (lint: LintResult) => void;
  closePrePublish: () => void;
}

/**
 * Builds the {@link EditorLifecycle} plus the gated variants the keyboard shortcuts use. Each
 * trigger checks the current state itself, so a shortcut can fire blindly.
 */
export function useEditorLifecycle({
  workflow, roleCanWrite, isAdmin, canWrite, isTidying, lockState, persistence,
  getLiveGraph, allWorkflows, openPrePublish, closePrePublish,
}: UseEditorLifecycleArgs) {
  const { t } = useTranslation(['editor', 'common']);
  const {
    isLockedByMe, isLockedByOther, lock, unlock, forceUnlock, disable, enable,
    isLocking, isUnlocking, isForceUnlocking, isDisabling, isEnabling,
  } = lockState;
  const { isDirty, isSaving, isPublishing, save, publish } = persistence;

  // On a productive workflow the toggle is a kill switch and stays direct: stopping production
  // is not an "is everything ready" question.
  const requestPublish = async () => {
    if (!roleCanWrite || isLockedByOther || isTidying) return;
    if (workflow?.isEnabled) {
      if (await confirmDialog(t('editor:stopWorkflowConfirm'))) disable();
      return;
    }
    if (isPublishing || isEnabling) return;
    // Linted here rather than per render: the verdict must describe the graph as it is now.
    const live = getLiveGraph();
    const fresh = getPrePublishLint(
      lintWorkflow(live.nodes, live.edges, allWorkflows), live.nodes, live.edges, workflow);
    // Clean lint goes straight through; otherwise the checklist shows the issues once.
    if (fresh.errors.length === 0 && fresh.warnings.length === 0) {
      if (isLockedByMe) publish();
      else enable();
      return;
    }
    openPrePublish(fresh);
  };

  // Errors block the checklist's confirm button, so they are not re-checked here.
  const confirmPrePublish = () => {
    closePrePublish();
    if (isTidying) return;
    if (isLockedByMe) publish();
    else enable();
  };

  const lifecycle: EditorLifecycle = {
    roleCanWrite, canWrite, isAdmin, isDirty, isSaving,
    isLockedByMe, isLockedByOther, isLocking,
    isUnlocking: isUnlocking || isTidying,
    isPublishing: isPublishing || isTidying,
    isEnabling, isDisabling, isForceUnlocking,
    save, lock, unlock, forceUnlock, disable,
    requestPublish: () => { void requestPublish(); },
  };

  return {
    lifecycle,
    confirmPrePublish,
    triggerSave: () => {
      if (canWrite && isDirty && !isSaving && !isTidying) save();
    },
    triggerLock: () => {
      if (roleCanWrite && !isLockedByMe && !isLockedByOther && !isLocking) lock();
    },
    triggerUnlock: () => {
      if (isLockedByMe && !isUnlocking && !isTidying) unlock();
    },
    triggerForceUnlock: async () => {
      if (!isAdmin || !isLockedByOther || isForceUnlocking) return;
      if (await confirmDialog(t('editor:banners.forceUnlockConfirm', { user: workflow?.checkedOutByUserName ?? t('common:unknown') }))) {
        forceUnlock();
      }
    },
  };
}
