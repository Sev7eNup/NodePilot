import { Add, Edit, Power, TrashCan } from '@carbon/icons-react';
import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useSystemAlertCatalog } from '../../hooks/useSystemAlertCatalog';
import { systemAlertingApi, type SystemAlertSource, type SystemAlertPolicy } from '../../api/systemAlerting';
import { useRole } from '../../lib/rbac';
import { confirmDialog } from '../../stores/confirmStore';
import { toast } from '../../stores/toastStore';
import { SystemPolicyEditor } from './SystemPolicyEditor';

type SourceStatus = 'unavailable' | 'notConfigured' | 'active' | 'disabled';

function statusOf(source: SystemAlertSource, policies: SystemAlertPolicy[]): SourceStatus {
  if (!source.available) return 'unavailable';
  if (policies.length === 0) return 'notConfigured';
  return policies.some((p) => p.isEnabled) ? 'active' : 'disabled';
}

const STATUS_CLASS: Record<SourceStatus, string> = {
  active: 'bg-emerald-500/15 text-emerald-600 border-emerald-500/30',
  disabled: 'bg-surface-container text-on-surface-variant border-outline-variant',
  notConfigured: 'bg-surface-container text-outline border-outline-variant',
  unavailable: 'bg-amber-500/10 text-amber-600 border-amber-500/30',
};

export function SystemAlertsSection() {
  const { t } = useTranslation(['alerts', 'common']);
  const { canAdmin } = useRole();
  const qc = useQueryClient();
  const { data: catalog, isLoading } = useSystemAlertCatalog();
  const { data: policies } = useQuery({ queryKey: ['system-alert-policies'], queryFn: () => systemAlertingApi.list() });
  const [editing, setEditing] = useState<{ source: SystemAlertSource; policy: SystemAlertPolicy | null } | null>(null);

  const bySource = useMemo(() => {
    const map = new Map<string, SystemAlertPolicy[]>();
    for (const p of policies ?? []) {
      const list = map.get(p.sourceId) ?? [];
      list.push(p);
      map.set(p.sourceId, list);
    }
    return map;
  }, [policies]);

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ['system-alert-policies'] });
    qc.invalidateQueries({ queryKey: ['system-alert-catalog'] });
  };

  const toggle = useMutation({
    mutationFn: (p: SystemAlertPolicy) => (p.isEnabled ? systemAlertingApi.disable(p.id) : systemAlertingApi.enable(p.id)),
    onSuccess: invalidate,
    onError: (err: Error) => toast.error(err.message),
  });
  const remove = useMutation({
    mutationFn: (id: string) => systemAlertingApi.delete(id),
    onSuccess: invalidate,
    onError: (err: Error) => toast.error(err.message),
  });

  if (isLoading) return <div className="text-sm text-on-surface-variant py-6">…</div>;
  if (!catalog || catalog.sources.length === 0) return <div className="text-sm text-on-surface-variant py-6">{t('alerts:system.empty')}</div>;

  const grouped = new Map<string, SystemAlertSource[]>();
  for (const s of catalog.sources) {
    const list = grouped.get(s.category) ?? [];
    list.push(s);
    grouped.set(s.category, list);
  }

  return (
    <div className="space-y-6">
      <p className="text-sm text-on-surface-variant">{t('alerts:system.subtitle')}</p>
      {[...grouped.entries()].map(([category, sources]) => (
        <div key={category}>
          <h3 className="text-xs font-semibold uppercase tracking-wide text-outline mb-2">
            {t(`alerts:system.categories.${category}`, category)}
          </h3>
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {sources.map((source) => {
              const list = bySource.get(source.sourceId) ?? [];
              const status = statusOf(source, list);
              return (
                <div key={source.sourceId} className="rounded-lg border border-outline-variant bg-surface-lowest p-3">
                  <div className="flex items-start justify-between gap-2">
                    <div className="min-w-0">
                      <div className="font-medium text-on-surface truncate">
                        {t(`alerts:system.sourceLabels.${source.sourceId}`, source.sourceId)}
                      </div>
                      <div className="text-[11px] text-outline">
                        {list.length > 0 ? t('alerts:system.policiesCount', { count: list.length }) : t('alerts:system.notConfigured')}
                      </div>
                    </div>
                    <span className={`shrink-0 px-2 py-0.5 rounded-full text-[11px] font-medium border ${STATUS_CLASS[status]}`}>
                      {t(`alerts:system.${status}`)}
                    </span>
                  </div>
                  {list.length > 0 && (
                    <ul className="mt-2 space-y-1">
                      {list.map((p) => (
                        <li key={p.id} className="flex items-center justify-between gap-2 rounded border border-outline-variant/50 px-2 py-1">
                          <span className="text-sm truncate">
                            <span className={`inline-block w-1.5 h-1.5 rounded-full mr-1.5 ${p.isEnabled ? 'bg-emerald-500' : 'bg-outline'}`} />
                            {p.name}
                          </span>
                          {canAdmin && (
                            <span className="flex items-center gap-0.5 shrink-0">
                              <button onClick={() => toggle.mutate(p)} title={p.isEnabled ? t('alerts:system.disable') : t('alerts:system.enable')}
                                className="p-1 text-outline hover:text-primary"><Power size={13} /></button>
                              <button onClick={() => setEditing({ source, policy: p })} title={t('common:edit')}
                                className="p-1 text-outline hover:text-primary"><Edit size={13} /></button>
                              <button onClick={async () => { if (await confirmDialog({ message: t('alerts:system.deletePolicyConfirm'), danger: true })) remove.mutate(p.id); }}
                                title={t('common:delete')} className="p-1 text-outline hover:text-red-600"><TrashCan size={13} /></button>
                            </span>
                          )}
                        </li>
                      ))}
                    </ul>
                  )}
                  {canAdmin && (
                    <button onClick={() => setEditing({ source, policy: null })}
                      className="mt-2 flex items-center gap-1 text-xs text-primary hover:underline">
                      <Add size={12} /> {t('alerts:system.addPolicy')}
                    </button>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      ))}
      {editing && <SystemPolicyEditor source={editing.source} policy={editing.policy} onClose={() => setEditing(null)} />}
    </div>
  );
}
