import { useState, useEffect, useLayoutEffect, useMemo, useRef, useCallback } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useBlocker } from 'react-router';
import { useTranslation } from 'react-i18next';
import type { Node, Edge } from '@xyflow/react';
import { api } from '../api/client';
import type { Workflow } from '../types/api';
import { stripRuntimeDefinition } from '../lib/workflowDefinitionSanitizer';
import { confirmDialog } from '../stores/confirmStore';
import { toast } from '../stores/toastStore';

interface UseWorkflowPersistenceArgs {
  workflowId: string | undefined;
  /** The loaded workflow - only its description is persisted alongside name + graph. */
  workflow: Workflow | undefined;
  nodes: Node[];
  edges: Edge[];
  /** Pauses the debounce while an async graph producer has not decided which draft to apply. */
  suspendAutoSave?: boolean;
}

interface WorkflowSnapshot {
  workflowId: string;
  revision: number;
  body: {
    name: string;
    description: string;
    definitionJson: string;
  };
}

export interface AsyncGraphEditToken {
  readonly workflowId: string;
  readonly generation: number;
  readonly revision: number;
}

/**
 * Single owner of the workflow's save/dirty lifecycle. Every request gets an immutable,
 * revisioned snapshot. A save loops until the newest revision is durable; publish freezes edit
 * affordances and a server refetch is adopted only when it cannot replace a local draft.
 */
export function useWorkflowPersistence({
  workflowId, workflow, nodes, edges, suspendAutoSave = false,
}: UseWorkflowPersistenceArgs) {
  const { t } = useTranslation(['editor', 'common']);
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [isDirty, setIsDirty] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [isPublishQueued, setIsPublishQueued] = useState(false);
  const revisionRef = useRef(0);
  const dirtyRef = useRef(false);
  const savingRef = useRef(false);
  const publishRef = useRef(false);
  const syncedWorkflowIdRef = useRef<string | undefined>(undefined);
  const renderedWorkflowIdRef = useRef(workflowId);
  const draftGenerationRef = useRef(0);

  const blocker = useBlocker(isDirty);
  const persistableDefinition = useMemo(() => stripRuntimeDefinition({ nodes, edges }), [nodes, edges]);

  // Always points at the newest rendered draft. Request bodies copy from this ref once; a
  // follow-up save captures it again after the preceding request completes.
  const draftRef = useRef({ workflowId, name, description: workflow?.description ?? '', persistableDefinition });
  useLayoutEffect(() => {
    if (renderedWorkflowIdRef.current !== workflowId) {
      renderedWorkflowIdRef.current = workflowId;
      // Even switching away and back to the same ID invalidates work from the prior visit.
      draftGenerationRef.current += 1;
    }
    draftRef.current = { workflowId, name, description: workflow?.description ?? '', persistableDefinition };
  }, [workflowId, name, workflow?.description, persistableDefinition]);

  useLayoutEffect(() => () => { draftGenerationRef.current += 1; }, []);

  const updateDirty = useCallback((value: boolean) => {
    dirtyRef.current = value;
    setIsDirty(value);
  }, []);

  const captureSnapshot = useCallback((): WorkflowSnapshot | null => {
    const draft = draftRef.current;
    if (!draft.workflowId) return null;
    return {
      workflowId: draft.workflowId,
      revision: revisionRef.current,
      body: {
        name: draft.name,
        description: draft.description,
        definitionJson: JSON.stringify(draft.persistableDefinition),
      },
    };
  }, []);

  const saveMutation = useMutation({
    mutationFn: (snapshot: WorkflowSnapshot) => api.put(`/workflows/${snapshot.workflowId}`, snapshot.body),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['workflows'] }); },
    onError: (err) => toast.error(t('common:saveFailed', { message: (err as Error).message })),
  });

  const saveLoopRef = useRef<Promise<unknown> | null>(null);
  const saveLatest = useCallback((): Promise<unknown> => {
    if (saveLoopRef.current) return saveLoopRef.current;

    const run = (async () => {
      savingRef.current = true;
      setIsSaving(true);
      try {
        let response: unknown;
        for (;;) {
          const snapshot = captureSnapshot();
          if (!snapshot) return response;
          response = await saveMutation.mutateAsync(snapshot);

          // A route change owns a different draft. Never carry a follow-up across it.
          if (draftRef.current.workflowId !== snapshot.workflowId) return response;
          if (revisionRef.current === snapshot.revision) {
            updateDirty(false);
            return response;
          }
        }
      } finally {
        savingRef.current = false;
        setIsSaving(false);
      }
    })();

    saveLoopRef.current = run;
    const clear = () => { if (saveLoopRef.current === run) saveLoopRef.current = null; };
    // Use both branches rather than run.finally(), whose returned rejected promise would be
    // unobserved when a fire-and-forget save fails.
    void run.then(clear, clear);
    return run;
  }, [captureSnapshot, saveMutation, updateDirty]);

  const publishMutation = useMutation({
    mutationFn: (snapshot: WorkflowSnapshot) => api.post(`/workflows/${snapshot.workflowId}/publish`, snapshot.body),
    onSuccess: (_response, snapshot) => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      queryClient.invalidateQueries({ queryKey: ['workflow', snapshot.workflowId] });
      if (draftRef.current.workflowId === snapshot.workflowId && revisionRef.current === snapshot.revision)
        updateDirty(false);
    },
    onError: (err) => toast.error(t('common:saveFailed', { message: (err as Error).message })),
    onSettled: () => {
      publishRef.current = false;
      setIsPublishQueued(false);
    },
  });

  // Autosave five seconds after the newest edit. A running save performs its own follow-up.
  const autosaveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    if (!isDirty || isSaving || isPublishQueued || suspendAutoSave || !workflowId) return;
    if (autosaveTimer.current) clearTimeout(autosaveTimer.current);
    autosaveTimer.current = setTimeout(() => { void saveLatest().catch(() => undefined); }, 5000);
    return () => { if (autosaveTimer.current) clearTimeout(autosaveTimer.current); };
  }, [isDirty, isSaving, isPublishQueued, suspendAutoSave, nodes, edges, name, workflowId, saveLatest]);

  useEffect(() => {
    const handler = (e: BeforeUnloadEvent) => { if (isDirty) e.preventDefault(); };
    globalThis.addEventListener('beforeunload', handler);
    return () => globalThis.removeEventListener('beforeunload', handler);
  }, [isDirty]);

  useEffect(() => {
    if (blocker.state !== 'blocked') return;
    void confirmDialog(t('editor:discardChangesConfirm')).then((proceed) => {
      if (proceed) {
        updateDirty(false);
        blocker.proceed?.();
      } else {
        blocker.reset?.();
      }
    });
  }, [blocker, blocker.state, t, updateDirty]);

  const rename = useCallback((value: string) => {
    revisionRef.current += 1;
    // Keep same-tick intents (rename followed by Ctrl+S) snapshot-safe before React rerenders.
    draftRef.current = { ...draftRef.current, name: value };
    setName(value);
    updateDirty(true);
  }, [updateDirty]);

  const markDirty = useCallback(() => {
    revisionRef.current += 1;
    updateDirty(true);
  }, [updateDirty]);

  /** Captures ownership of the current draft before an async graph computation starts. */
  const beginAsyncGraphEdit = useCallback((): AsyncGraphEditToken | null => {
    const draftWorkflowId = draftRef.current.workflowId;
    if (!draftWorkflowId) return null;
    return {
      workflowId: draftWorkflowId,
      generation: draftGenerationRef.current,
      revision: revisionRef.current,
    };
  }, []);

  /**
   * Applies an async result only to the exact draft it was computed from. The request snapshot
   * is updated before React renders, so an in-flight save observes exactly one new revision and
   * follows up with this graph instead of re-saving the previous canvas.
   */
  const applyAsyncGraphEdit = useCallback((
    token: AsyncGraphEditToken,
    nextNodes: Node[],
    nextEdges: Edge[],
  ): boolean => {
    if (publishRef.current
        || draftRef.current.workflowId !== token.workflowId
        || draftGenerationRef.current !== token.generation
        || revisionRef.current !== token.revision) return false;

    draftRef.current = {
      ...draftRef.current,
      persistableDefinition: stripRuntimeDefinition({ nodes: nextNodes, edges: nextEdges }),
    };
    revisionRef.current += 1;
    updateDirty(true);
    return true;
  }, [updateDirty]);

  const save = useCallback(() => { void saveLatest().catch(() => undefined); }, [saveLatest]);
  const saveAsync = useCallback(() => saveLatest(), [saveLatest]);

  const publish = useCallback(() => {
    if (publishRef.current) return;
    publishRef.current = true;
    // A late async producer must never apply across this lifecycle boundary.
    draftGenerationRef.current += 1;
    setIsPublishQueued(true);
    void (async () => {
      try {
        // Let the pending save finish first. The atomic publish then snapshots the latest draft.
        if (saveLoopRef.current) await saveLoopRef.current;
        const snapshot = captureSnapshot();
        if (!snapshot) {
          publishRef.current = false;
          setIsPublishQueued(false);
          return;
        }
        publishMutation.mutate(snapshot);
      } catch {
        // The save mutation already surfaced its error. Never publish an older revision.
        publishRef.current = false;
        setIsPublishQueued(false);
      }
    })();
  }, [captureSnapshot, publishMutation]);

  /**
   * Returns whether the page may atomically adopt the server name and DefinitionJson. Lifecycle
   * refetches during a save/publish or while dirty must not replace the locally owned canvas.
   */
  const syncFromServer = useCallback((serverName: string): boolean => {
    const isNewWorkflow = syncedWorkflowIdRef.current !== workflowId;
    if (!isNewWorkflow && (dirtyRef.current || savingRef.current || publishRef.current)) return false;

    syncedWorkflowIdRef.current = workflowId;
    if (isNewWorkflow) revisionRef.current = 0;
    draftRef.current = { ...draftRef.current, name: serverName };
    setName(serverName);
    updateDirty(false);
    return true;
  }, [workflowId, updateDirty]);

  return {
    name,
    isDirty,
    isSaving,
    isPublishing: isPublishQueued || publishMutation.isPending,
    rename,
    markDirty,
    beginAsyncGraphEdit,
    applyAsyncGraphEdit,
    save,
    saveAsync,
    publish,
    syncFromServer,
  };
}
