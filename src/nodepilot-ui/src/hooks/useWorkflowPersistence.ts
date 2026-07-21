import { useState, useEffect, useMemo, useRef, useCallback } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useBlocker } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import type { Node, Edge } from '@xyflow/react';
import { api } from '../api/client';
import type { Workflow } from '../types/api';
import { stripRuntimeDefinition } from '../lib/workflowDefinitionSanitizer';
import { confirmDialog } from '../stores/confirmStore';
import { toast } from '../stores/toastStore';

interface UseWorkflowPersistenceArgs {
  workflowId: string | undefined;
  /** The loaded workflow — only its description is persisted alongside name + graph. */
  workflow: Workflow | undefined;
  nodes: Node[];
  edges: Edge[];
}

/**
 * Single owner of the workflow's save/dirty lifecycle: the editable name, the dirty flag,
 * the persistable (runtime-stripped) definition, the save + atomic-publish mutations, the
 * 5 s autosave debounce, the `beforeunload` guard, and the `useBlocker` discard-confirm —
 * all kept internal. This untangles the former forward-reference (autosave effect → save
 * mutation → persistableDefinition) by giving the whole cluster one declaration site.
 *
 * Exposes a narrow, intent-oriented API only — no `setName`, no `persistableDefinition`,
 * no `blocker` leak out. `syncFromServer` is what the page's load effect calls to adopt
 * the freshly-fetched name and clear dirty (identical refetch semantics as before).
 */
export function useWorkflowPersistence({ workflowId, workflow, nodes, edges }: UseWorkflowPersistenceArgs) {
  const { t } = useTranslation(['editor', 'common']);
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [isDirty, setIsDirty] = useState(false);

  // Block browser navigation (back/forward) and in-app route changes when there are
  // unsaved changes. `useBlocker` is the React Router v7 equivalent of the old `<Prompt>`
  // component — it intercepts navigation before it happens so we can ask the user to
  // confirm. The `beforeunload` handler below covers tab-close/refresh.
  const blocker = useBlocker(isDirty);

  const persistableDefinition = useMemo(() => stripRuntimeDefinition({ nodes, edges }), [nodes, edges]);

  const saveMutation = useMutation({
    mutationFn: () => api.put(`/workflows/${workflowId}`, { name, description: workflow?.description ?? '', definitionJson: JSON.stringify(persistableDefinition) }),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['workflows'] }); setIsDirty(false); },
    onError: (err) => toast.error(t('common:saveFailed', { message: (err as Error).message })),
  });

  // Publish = Save + Enable + Unlock, atomic on the backend so a tab reload between save
  // and enable can't leave the workflow half-published.
  const publishMutation = useMutation({
    mutationFn: () => api.post(`/workflows/${workflowId}/publish`, {
      name,
      description: workflow?.description ?? '',
      definitionJson: JSON.stringify(persistableDefinition),
    }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      queryClient.invalidateQueries({ queryKey: ['workflow', workflowId] });
      setIsDirty(false);
    },
    onError: (err) => toast.error(t('common:saveFailed', { message: (err as Error).message })),
  });

  // Autosave: 5 s after last change, only when dirty and not already saving.
  const autosaveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    if (!isDirty || saveMutation.isPending || !workflowId) return;
    if (autosaveTimer.current) clearTimeout(autosaveTimer.current);
    autosaveTimer.current = setTimeout(() => { saveMutation.mutate(); }, 5000);
    return () => { if (autosaveTimer.current) clearTimeout(autosaveTimer.current); };
  }, [isDirty, nodes, edges, name]); // eslint-disable-line react-hooks/exhaustive-deps

  // Warn on unload when there are unsaved changes.
  useEffect(() => {
    const handler = (e: BeforeUnloadEvent) => { if (isDirty) { e.preventDefault(); } };
    globalThis.addEventListener('beforeunload', handler);
    return () => globalThis.removeEventListener('beforeunload', handler);
  }, [isDirty]);

  // useBlocker confirmation dialog. When `isDirty` is true, `useBlocker` sets
  // `blocker.state === 'blocked'` on any route change. The user can proceed (resetting
  // isDirty first so the next navigation isn't blocked again) or cancel. No sync answer
  // needed: the blocker stays 'blocked' until proceed()/reset() is called, so the async
  // confirmDialog resolves first and the navigation waits (React Router's supported
  // pattern for custom confirmation UIs).
  useEffect(() => {
    if (blocker.state === 'blocked') {
      void confirmDialog(t('editor:discardChangesConfirm')).then((proceed) => {
        if (proceed) {
          setIsDirty(false);
          blocker.proceed?.();
        } else {
          blocker.reset?.();
        }
      });
    }
  }, [blocker.state]); // eslint-disable-line react-hooks/exhaustive-deps

  /** Rename the workflow (marks dirty). Wired to the header name input. */
  const rename = useCallback((value: string) => { setName(value); setIsDirty(true); }, []);
  /** Flag unsaved changes — called by every graph-mutating handler. */
  const markDirty = useCallback(() => { setIsDirty(true); }, []);
  /** Fire-and-forget save (autosave / Ctrl+S). Errors handled by the mutation. */
  const save = useCallback(() => { saveMutation.mutate(); }, [saveMutation]);
  /** Awaitable save for the save-before-run path, so a failed save can abort the run. */
  const saveAsync = useCallback(() => saveMutation.mutateAsync(), [saveMutation]);
  /** Atomic publish (save + enable + unlock). */
  const publish = useCallback(() => { publishMutation.mutate(); }, [publishMutation]);
  /** Adopt the server-loaded name and clear dirty. Called once per workflow load. */
  const syncFromServer = useCallback((serverName: string) => { setName(serverName); setIsDirty(false); }, []);

  return {
    name,
    isDirty,
    isSaving: saveMutation.isPending,
    isPublishing: publishMutation.isPending,
    rename,
    markDirty,
    save,
    saveAsync,
    publish,
    syncFromServer,
  };
}
