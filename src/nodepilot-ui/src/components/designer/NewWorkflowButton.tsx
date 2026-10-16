import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { Add, CircleDash } from '@carbon/icons-react';
import { api } from '../../api/client';
import type { Workflow } from '../../types/api';

/**
 * Floating "New workflow" pill on the canvas with its name popover. Mirrors
 * WorkflowsPage.createMutation, except the target folder comes from the open workflow, because
 * defaulting to Root would return 403 for folder-scoped users. On success the workflow list is
 * invalidated and the new, empty workflow is opened, where the header allows inline renaming.
 */
export function NewWorkflowButton({ folderId, fullscreen }: Readonly<{
  folderId: string | null | undefined;
  fullscreen: boolean;
}>) {
  const { t } = useTranslation(['editor', 'workflows', 'common']);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState('');
  const createWorkflowMutation = useMutation({
    mutationFn: (workflowName: string) =>
      api.post<Workflow>('/workflows', {
        name: workflowName,
        description: '',
        definitionJson: JSON.stringify({ nodes: [], edges: [] }),
        folderId: folderId ?? undefined,
      }),
    onSuccess: (created) => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      queryClient.invalidateQueries({ queryKey: ['shared-folders'] });
      setOpen(false);
      setName('');
      navigate(`/workflows/${created.id}`);
    },
  });
  const submit = () => {
    const trimmed = name.trim();
    if (!trimmed || createWorkflowMutation.isPending) return;
    createWorkflowMutation.mutate(trimmed);
  };
  const close = () => { setOpen(false); setName(''); };

  // The only free canvas corner. In fullscreen it stacks below the exit pill.
  return (
    <div className={`absolute ${fullscreen ? 'top-14' : 'top-3'} right-3 z-40 flex flex-col items-end`}>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="bg-surface-lowest/90 backdrop-blur border border-outline-variant/30 rounded-full px-3 py-1.5 text-xs font-label font-semibold text-on-surface-variant hover:bg-surface-high shadow-md transition-colors flex items-center gap-1.5"
        title={t('editor:newWorkflow')}
      >
        <Add size={14} /> {t('editor:newWorkflow')}
      </button>
      {open && (
        <div className="mt-1.5 w-64 bg-surface-lowest border border-outline-variant/40 rounded-lg shadow-xl p-3 flex flex-col gap-2">
          <label className="text-xs font-label font-medium text-on-surface-variant">{t('editor:newWorkflow')}</label>
          <input
            autoFocus
            type="text"
            data-testid="new-workflow-name-input"
            value={name}
            onChange={(e) => setName(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') { e.preventDefault(); submit(); }
              else if (e.key === 'Escape') { e.preventDefault(); close(); }
            }}
            placeholder={t('workflows:workflowName')}
            className="w-full px-2.5 py-1.5 text-sm rounded border border-outline-variant/50 bg-surface text-on-surface focus:outline-none focus:border-primary"
          />
          <div className="flex justify-end gap-2">
            <button
              type="button"
              onClick={close}
              className="px-2.5 py-1 text-xs rounded border border-outline text-on-surface hover:bg-surface-container-high"
            >
              {t('common:cancel')}
            </button>
            <button
              type="button"
              onClick={submit}
              disabled={!name.trim() || createWorkflowMutation.isPending}
              className="px-2.5 py-1 text-xs rounded bg-primary text-on-primary hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed flex items-center gap-1.5"
            >
              {createWorkflowMutation.isPending && <CircleDash size={12} className="animate-spin" />}
              {t('workflows:create')}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
