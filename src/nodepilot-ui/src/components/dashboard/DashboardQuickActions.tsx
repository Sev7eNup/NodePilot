import {
  Add,
  ArrowDownLeft,
  CalendarSettings,
  DocumentUnknown,
  MagicWandFilled,
  Notification,
  Play,
  User,
  WarningAltFilled,
} from '@carbon/icons-react';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import { useRole } from '../../lib/rbac';
import { toast } from '../../stores/toastStore';

interface WorkflowCreated { id: string }

/**
 * Shortcut bar: the most common operator actions one click away instead of forcing
 * navigation into a sub-page first. Mutating actions are gated to Admin/Operator;
 * Viewers get browse-only navigation. The "new workflow" action
 * reuses the same inline-name-input pattern as WorkflowsPage and the same POST /workflows
 * body, then drops the user straight into the editor.
 */
export function DashboardQuickActions({
  longRunningCount,
}: Readonly<{ longRunningCount: number }>) {
  const { t } = useTranslation(['dashboard']);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { canWrite, isViewer, isAdmin } = useRole();
  const [showNew, setShowNew] = useState(false);
  const [newName, setNewName] = useState('');

  const createMutation = useMutation({
    mutationFn: (name: string) =>
      api.post<WorkflowCreated>('/workflows', {
        name,
        description: '',
        definitionJson: JSON.stringify({ nodes: [], edges: [] }),
      }),
    onSuccess: (workflow) => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      queryClient.invalidateQueries({ queryKey: ['dashboard-stats'] });
      setShowNew(false);
      setNewName('');
      navigate(`/workflows/${workflow.id}`);
    },
    onError: (err: unknown) => {
      toast.error(t('dashboard:quickActions.createFailed', { error: (err as Error)?.message ?? '' }));
    },
  });

  const submitNew = () => {
    const name = newName.trim();
    if (!name) return;
    createMutation.mutate(name);
  };

  const reviewLongRunning = () => {
    // No global cancel-long-running endpoint yet. The operations cockpit is the live
    // surface for active runs; /executions intentionally shows terminal history only.
    navigate('/operations');
  };

  const actionBtn = 'np-btn np-btn-sm np-btn-secondary';
  const primaryBtn = 'np-btn np-btn-sm np-btn-primary';
  const iconBtn = 'np-btn np-btn-icon-sm np-btn-ghost';

  return (
    <div className="flex flex-wrap items-center gap-2 mb-5">
      <span className="text-xs uppercase tracking-wider font-semibold text-on-surface-variant mr-1">
        {t('dashboard:quickActions.label')}
      </span>
      {canWrite && (
        <>
          <button type="button" className={primaryBtn} onClick={() => setShowNew((v) => !v)}>
            <Add size={15} /> {t('dashboard:quickActions.newWorkflow')}
          </button>
          {showNew && (
            <span className="flex items-center gap-1.5">
              <input
                autoFocus
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') submitNew(); if (e.key === 'Escape') { setShowNew(false); setNewName(''); } }}
                placeholder={t('dashboard:quickActions.newWorkflowPrompt')}
                className="px-2 py-1 text-sm border border-outline-variant rounded-md bg-surface-lowest w-56"
              />
              <button type="button" className={iconBtn} onClick={submitNew} disabled={!newName.trim() || createMutation.isPending}>
                <ArrowDownLeft size={15} />
              </button>
            </span>
          )}
          <button type="button" className={actionBtn} onClick={() => navigate('/workflows')}>
            <Play size={15} /> {t('dashboard:quickActions.triggerWorkflow')}
          </button>
          <button type="button" className={actionBtn} onClick={() => navigate('/executions?status=Failed')}>
            <DocumentUnknown size={15} /> {t('dashboard:quickActions.showFailed')}
          </button>
          <button
            type="button"
            className={`np-btn np-btn-sm ${longRunningCount > 0 ? 'np-btn-attention' : 'np-btn-secondary'}`}
            onClick={reviewLongRunning}
            disabled={longRunningCount === 0}
            title={longRunningCount === 0 ? t('dashboard:longRunningTitle') : undefined}
          >
            <WarningAltFilled size={15} /> {t('dashboard:quickActions.reviewLongRunning')}
            {longRunningCount > 0 && <span className="tabular-nums">({longRunningCount})</span>}
          </button>
          <button type="button" className={actionBtn} onClick={() => navigate('/maintenance-windows')}>
            <CalendarSettings size={15} /> {t('dashboard:quickActions.newMaintenance')}
          </button>
          <button type="button" className={actionBtn} onClick={() => navigate('/alerts')}>
            <Notification size={15} /> {t('dashboard:quickActions.newAlert')}
          </button>
          {isAdmin && (
            <button
              type="button"
              className={actionBtn}
              onClick={() => navigate('/settings?tab=system&section=integrations')}
              title={t('dashboard:quickActions.llmConfig')}
            >
              <MagicWandFilled size={15} /> {t('dashboard:quickActions.llmConfig')}
            </button>
          )}
        </>
      )}
      {isViewer && (
        <>
          <button type="button" className={actionBtn} onClick={() => navigate('/workflows')}>
            <Play size={15} /> {t('dashboard:quickActions.triggerWorkflow')}
          </button>
        </>
      )}
      {/* All-roles shortcut to personal settings — sits right after the alert rule for
          Admin/Op (filling the bar toward the content edge) and after the browse button
          for Viewer. Reuses the same `btn` style as every other button in the bar. */}
      <button
        type="button"
        className={actionBtn}
        onClick={() => navigate('/settings?tab=personal')}
        title={t('dashboard:quickActions.personal')}
      >
        <User size={15} /> {t('dashboard:quickActions.personal')}
      </button>
    </div>
  );
}
