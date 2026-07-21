import { Close, Compare, Reset } from '@carbon/icons-react';
import { useState, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { type Node, type Edge } from '@xyflow/react';
import { api } from '../../../api/client';
import { stripRuntimeDefinition } from '../../../lib/workflowDefinitionSanitizer';
import { DefinitionDiffViewer } from '../DefinitionDiffViewer';
import { confirmDialog } from '../../../stores/confirmStore';

type WorkflowVersionRow = {
  version: number;
  isCurrent: boolean;
  createdAt?: string | null;
  createdBy?: string | null;
  changeNote?: string | null;
};

/**
 * Version diff. User picks a historical version from the timeline; the modal compares
 * that saved definition with the current working copy. The actual diff rendering is
 * delegated to the shared, ID-stable {@link DefinitionDiffViewer} (handles/positions-aware).
 */
export function WorkflowDiffModal({ workflowId, currentDefinition, canRestore = false, onClose }: Readonly<{
  workflowId: string;
  currentDefinition: { nodes: Node[]; edges: Edge[] };
  canRestore?: boolean;
  onClose: () => void;
}>) {
  const { t } = useTranslation('editor');
  const [selectedVersion, setSelectedVersion] = useState<number | null>(null);
  const [rollbackError, setRollbackError] = useState<string | null>(null);
  const queryClient = useQueryClient();

  const { data: versions } = useQuery({
    queryKey: ['workflow-versions', workflowId],
    queryFn: () => api.get<WorkflowVersionRow[]>(`/workflows/${workflowId}/versions`),
  });

  const { data: baseDef } = useQuery({
    queryKey: ['workflow-version', workflowId, selectedVersion],
    queryFn: async () => {
      const raw = await api.get<{ definition?: { nodes?: Node[]; edges?: Edge[] } }>(`/workflows/${workflowId}/versions/${selectedVersion}`);
      return stripRuntimeDefinition({
        nodes: raw.definition?.nodes ?? [],
        edges: raw.definition?.edges ?? [],
      });
    },
    enabled: selectedVersion != null,
  });

  const rollbackMutation = useMutation({
    mutationFn: async (version: number) => {
      setRollbackError(null);
      await api.post(`/workflows/${workflowId}/rollback/${version}`, { reason: t('diff.rollbackReason', { version }) });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workflow-versions', workflowId] });
      queryClient.invalidateQueries({ queryKey: ['workflow', workflowId] });
      onClose();
    },
    onError: (err: Error) => {
      setRollbackError(err.message || t('diff.rollbackFailed'));
    },
  });

  const requestRestore = async (version: number) => {
    if (!canRestore || rollbackMutation.isPending) return;
    if (!(await confirmDialog(t('diff.restoreConfirm', { version })))) return;
    rollbackMutation.mutate(version);
  };

  const currentDef = useMemo(() => stripRuntimeDefinition(currentDefinition), [currentDefinition]);

  return (
    <div
      className="np-anim-backdrop fixed inset-0 z-50 flex items-center justify-center bg-black/40"
      onClick={onClose}
      onKeyDown={(e) => e.key === 'Escape' && onClose()}
      role="presentation"
      tabIndex={-1}
    >
      <div
        className="w-[960px] max-w-[95vw] h-[80vh] bg-surface-lowest rounded-lg shadow-2xl border border-outline-variant/30 overflow-hidden flex flex-col"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.stopPropagation()}
        role="presentation"
      >
        <div className="px-5 py-3 border-b border-outline-variant/20 flex items-center justify-between">
          <div className="flex items-center gap-2">
            <Compare size={16} className="text-on-surface-variant" />
            <h2 className="font-headline text-sm font-bold text-on-surface">{t('diff.title')}</h2>
          </div>
          <div className="flex items-center gap-2">
            {!!selectedVersion && (
              <button
                onClick={() => requestRestore(selectedVersion)}
                disabled={!canRestore || rollbackMutation.isPending}
                className="flex items-center gap-1.5 px-3 py-1 rounded bg-primary text-on-primary text-xs font-semibold hover:opacity-90 disabled:opacity-50 transition-opacity"
                title={canRestore ? t('diff.restoreTitle', { version: selectedVersion }) : t('diff.restoreTitleBlocked')}
              >
                <Reset size={12} />
                {rollbackMutation.isPending ? t('diff.restoring') : t('diff.restoreVersion', { version: selectedVersion })}
              </button>
            )}
            <button onClick={onClose} className="text-on-surface-variant hover:text-on-surface" aria-label={t('common:close')}>
              <Close size={14} />
            </button>
          </div>
        </div>
        {rollbackError && (
          <div className="px-5 py-2 bg-error-container text-on-error-container text-xs font-label">
            {rollbackError}
          </div>
        )}
        <div className="flex-1 overflow-hidden grid grid-cols-[280px_1fr]">
          <div className="border-r border-outline-variant/20 overflow-y-auto bg-surface-low">
            <div className="px-3 py-2 text-[10px] font-label font-bold text-outline uppercase tracking-widest">
              {t('diff.compareAgainst')}
            </div>
            {versions?.filter((v) => !v.isCurrent).map((v) => (
              <button
                key={v.version}
                onClick={() => setSelectedVersion(v.version)}
                className={`w-full text-left px-3 py-2 border-t border-outline-variant/10 hover:bg-surface-high transition-colors ${
                  selectedVersion === v.version ? 'bg-primary-fixed' : ''
                }`}
              >
                <div className="font-label text-xs font-semibold text-on-surface">
                  {t('diff.version', { version: v.version })}
                </div>
                <div className="font-label text-[10px] text-on-surface-variant">
                  {v.createdAt ? new Date(v.createdAt).toLocaleString() : ''}
                  {v.createdBy ? ` - ${v.createdBy}` : ''}
                </div>
                {v.changeNote && (
                  <div className="font-label text-[10px] text-on-surface-variant italic truncate mt-0.5">
                    {v.changeNote}
                  </div>
                )}
              </button>
            ))}
            {versions?.filter((v) => !v.isCurrent).length === 0 && (
              <div className="px-3 py-4 text-[11px] text-outline">{t('diff.noHistory')}</div>
            )}
          </div>
          <div className="overflow-y-auto p-4">
            {!selectedVersion && (
              <div className="flex items-center justify-center h-full text-outline font-label text-sm">
                {t('diff.pickVersion')}
              </div>
            )}
            {!!selectedVersion && !baseDef && (
              <div className="text-outline font-label text-sm">{t('common:loadingDots')}</div>
            )}
            {baseDef && (
              <DefinitionDiffViewer base={baseDef} current={currentDef} />
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
