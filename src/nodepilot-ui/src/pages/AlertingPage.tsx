import { Add, ChevronDown, ChevronUp, Edit, History, Power, Search, TrashCan } from '@carbon/icons-react';
import { useCallback, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { alertingApi, type NotificationRule } from '../api/alerting';
import { MobileCardList } from '../components/common/MobileCardList';
import { AlertingRuleEditor } from '../components/alerting/AlertingRuleEditor';
import { SystemAlertsSection } from '../components/alerting/SystemAlertsSection';
import { DeliveriesModal } from '../components/alerting/DeliveriesModal';
import { useRole } from '../lib/rbac';
import { useIsMobile } from '../hooks/useMediaQuery';
import { confirmDialog } from '../stores/confirmStore';

/**
 * Admin/Operator view of alerting rules. A rule fires a delivery to its routes when an event of
 * its types matches its filter (and survives cooldown/flap suppression). Read is Admin/Operator;
 * create/edit/delete/test-fire are Admin-only (mirrors the AlertingController authorization).
 */
export function AlertingPage() {
  const { t } = useTranslation(['alerts', 'common']);
  const queryClient = useQueryClient();
  const { canAdmin } = useRole();
  const isMobile = useIsMobile();
  const [searchParams, setSearchParams] = useSearchParams();
  const tab: 'system' | 'custom' = searchParams.get('tab') === 'custom' ? 'custom' : 'system';
  const setTab = (next: 'system' | 'custom') => {
    const params = new URLSearchParams(searchParams);
    params.set('tab', next);
    setSearchParams(params);
  };
  const [search, setSearch] = useState('');
  const [editing, setEditing] = useState<NotificationRule | null>(null);
  const [showEditor, setShowEditor] = useState(false);
  const [showDeliveries, setShowDeliveries] = useState(false);

  const { data: rules, isLoading } = useQuery({
    queryKey: ['alerting-rules'],
    queryFn: () => alertingApi.list(),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => alertingApi.delete(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['alerting-rules'] }),
  });

  const toggleMutation = useMutation({
    mutationFn: (r: NotificationRule) => (r.isEnabled ? alertingApi.disable(r.id) : alertingApi.enable(r.id)),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['alerting-rules'] }),
  });

  const openCreate = () => { setEditing(null); setShowEditor(true); };
  const openEdit = (r: NotificationRule) => { setEditing(r); setShowEditor(true); };

  // Click-header-to-sort (same inline pattern as UsersPage/MachinesPage/CustomActivitiesPage).
  type SortKey = 'name' | 'description' | 'enabled' | 'events' | 'scope' | 'routes';
  const [sortBy, setSortBy] = useState<SortKey>('name');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');
  const handleSort = (col: SortKey) => {
    if (sortBy === col) setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    else { setSortBy(col); setSortDir('asc'); }
  };
  const sortIcon = (col: SortKey) =>
    sortBy === col ? (sortDir === 'asc' ? <ChevronUp size={12} /> : <ChevronDown size={12} />) : <span className="w-3" />;

  const scopeLabel = useCallback((r: NotificationRule): string =>
    r.scopeKind === 'Global' ? t('alerts:scopes.Global')
    : `${t(`alerts:scopes.${r.scopeKind}`)} (${r.targets.length})`, [t]);
  const routesLabel = useCallback((r: NotificationRule): string =>
    r.routes.map((rt) => t(`alerts:channels.${rt.channel}`, rt.channel)).join(', ') || t('common:dash'), [t]);
  const eventsLabel = useCallback((r: NotificationRule): string =>
    r.eventTypes.map((et) => t(`alerts:eventTypeLabels.${et}`, et)).join(', '), [t]);

  const filteredSorted = useMemo(() => {
    const term = search.trim().toLowerCase();
    let rows = rules ?? [];
    if (term) rows = rows.filter((r) => r.name.toLowerCase().includes(term));
    return [...rows].sort((a, b) => {
      let cmp = 0;
      switch (sortBy) {
        case 'name':        cmp = a.name.localeCompare(b.name); break;
        case 'description': cmp = (a.description ?? '').localeCompare(b.description ?? ''); break;
        case 'enabled':     cmp = Number(b.isEnabled) - Number(a.isEnabled); break;
        case 'events':      cmp = eventsLabel(a).localeCompare(eventsLabel(b)); break;
        case 'scope':       cmp = scopeLabel(a).localeCompare(scopeLabel(b)); break;
        case 'routes':      cmp = routesLabel(a).localeCompare(routesLabel(b)); break;
      }
      return sortDir === 'asc' ? cmp : -cmp;
    });
  }, [rules, search, sortBy, sortDir, eventsLabel, scopeLabel, routesLabel]);

  return (
    <div className="w-fit max-w-full mx-auto np-fade-up">
      <div className="flex items-center justify-between mb-6">
        <div>
          <p className="text-sm text-on-surface-variant mt-1 max-w-3xl">{t('alerts:subtitle')}</p>
        </div>
        <div className="flex items-center gap-2">
          <button onClick={() => setShowDeliveries(true)} title={t('alerts:deliveries.title')}
            className="flex items-center gap-2 px-3 py-2 sm:px-4 border border-outline-variant text-on-surface rounded-md hover:bg-surface-container text-sm">
            <History size={16} /> <span className="hidden sm:inline">{t('alerts:deliveries.button')}</span>
          </button>
          {canAdmin && tab === 'custom' && (
            <button onClick={openCreate} title={t('alerts:newRule')}
              className="flex items-center gap-2 px-3 py-2 sm:px-4 bg-blue-600 text-white rounded-md hover:bg-blue-700 text-sm">
              <Add size={16} /> <span className="hidden sm:inline">{t('alerts:newRule')}</span>
            </button>
          )}
        </div>
      </div>
      {/* System vs. custom split (ADR 0008). */}
      <div className="flex gap-1 mb-4 border-b border-outline-variant">
        {(['system', 'custom'] as const).map((key) => (
          <button key={key} onClick={() => setTab(key)}
            className={`px-3 py-2 text-sm font-medium -mb-px border-b-2 ${
              tab === key ? 'border-blue-600 text-on-surface' : 'border-transparent text-on-surface-variant hover:text-on-surface'}`}>
            {t(`alerts:system.tab${key === 'system' ? 'System' : 'Custom'}`)}
          </button>
        ))}
      </div>
      {tab === 'system' && <SystemAlertsSection />}
      {tab === 'custom' && (rules?.length ?? 0) > 0 && (
        <div className="np-card p-3 mb-3 flex items-center gap-3">
          <div className="relative w-full sm:flex-1 sm:min-w-[220px]">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-outline" />
            <input type="text" value={search} onChange={(e) => setSearch(e.target.value)}
              placeholder={t('alerts:searchPlaceholder')}
              className="w-full pl-8 pr-3 py-1.5 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500" />
          </div>
        </div>
      )}
      {tab === 'custom' && (isLoading ? (
        <p className="text-outline">{t('common:loadingDots')}</p>
      ) : !rules || rules.length === 0 ? (
        <div className="np-card p-8 text-center text-outline">{t('alerts:empty')}</div>
      ) : filteredSorted.length === 0 ? (
        <div className="np-card p-8 text-center text-outline">{t('alerts:noMatch')}</div>
      ) : isMobile ? (
        <MobileCardList
          items={filteredSorted}
          getKey={(r) => r.id}
          renderTitle={(r) => (
            <div className="min-w-0">
              <div className="text-sm font-semibold text-on-surface truncate">{r.name}</div>
              {r.description && <div className="text-xs text-on-surface-variant truncate">{r.description}</div>}
            </div>
          )}
          renderFields={(r) => [
            { label: t('alerts:tableHeaders.enabled'), value: <span className={`text-xs font-medium ${r.isEnabled ? 'text-green-600' : 'text-outline'}`}>{r.isEnabled ? t('common:yes') : t('common:no')}</span> },
            { label: t('alerts:tableHeaders.events'), value: <span className="text-xs text-on-surface-variant">{eventsLabel(r)}</span> },
            { label: t('alerts:tableHeaders.scope'), value: <span className="text-sm text-on-surface-variant">{scopeLabel(r)}</span> },
            { label: t('alerts:tableHeaders.routes'), value: <span className="text-xs text-on-surface-variant">{routesLabel(r)}</span> },
          ]}
          renderActions={canAdmin ? (r) => (
            <>
              <button onClick={() => toggleMutation.mutate(r)}
                className={`p-2 rounded-lg ${r.isEnabled ? 'text-emerald-600 hover:bg-emerald-500/15' : 'text-outline hover:bg-surface-low'}`}
                title={r.isEnabled ? t('alerts:disable') : t('alerts:enable')}>
                <Power size={16} />
              </button>
              <button onClick={() => openEdit(r)} className="p-2 text-blue-600 hover:bg-blue-50 rounded-lg" title={t('common:edit')}>
                <Edit size={16} />
              </button>
              <button onClick={async () => { if (await confirmDialog({ message: t('alerts:deleteConfirm', { name: r.name }), danger: true })) deleteMutation.mutate(r.id); }}
                className="p-2 text-red-600 hover:bg-red-500/15 rounded-lg" title={t('common:delete')}>
                <TrashCan size={16} />
              </button>
            </>
          ) : undefined}
        />
      ) : (
        <div className="np-card overflow-hidden"><div className="overflow-x-auto">
          <table className="w-fit">
            <thead className="np-col-header text-left text-xs font-semibold uppercase tracking-wide text-on-surface-variant">
              <tr>
                <th className="px-2 py-2">
                  <button onClick={() => handleSort('name')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('alerts:tableHeaders.name')}{sortIcon('name')}
                  </button>
                </th>
                <th className="px-2 py-2">
                  <button onClick={() => handleSort('description')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('alerts:tableHeaders.description')}{sortIcon('description')}
                  </button>
                </th>
                <th className="px-2 py-2">
                  <button onClick={() => handleSort('enabled')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('alerts:tableHeaders.enabled')}{sortIcon('enabled')}
                  </button>
                </th>
                <th className="px-2 py-2">
                  <button onClick={() => handleSort('events')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('alerts:tableHeaders.events')}{sortIcon('events')}
                  </button>
                </th>
                <th className="px-2 py-2">
                  <button onClick={() => handleSort('scope')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('alerts:tableHeaders.scope')}{sortIcon('scope')}
                  </button>
                </th>
                <th className="px-2 py-2">
                  <button onClick={() => handleSort('routes')} className="flex items-center gap-1 hover:text-on-surface transition-colors">
                    {t('alerts:tableHeaders.routes')}{sortIcon('routes')}
                  </button>
                </th>
                <th className="px-2 py-2">{t('alerts:tableHeaders.actions')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-outline-variant/30">
              {filteredSorted.map((r) => (
                <tr key={r.id} className="hover:bg-surface-low">
                  <td className="px-2 py-2 overflow-hidden">
                    <div className="text-sm font-semibold text-on-surface-variant truncate">{r.name}</div>
                  </td>
                  <td className="px-2 py-2 text-xs text-on-surface-variant whitespace-nowrap">{r.description || t('common:dash')}</td>
                  <td className="px-2 py-2">
                    <span className={`text-xs font-medium ${r.isEnabled ? 'text-green-600' : 'text-outline'}`}>
                      {r.isEnabled ? t('common:yes') : t('common:no')}
                    </span>
                  </td>
                  <td className="px-2 py-2 text-xs text-on-surface-variant whitespace-nowrap">{eventsLabel(r)}</td>
                  <td className="px-2 py-2 text-sm text-on-surface-variant whitespace-nowrap">{scopeLabel(r)}</td>
                  <td className="px-2 py-2 text-xs text-on-surface-variant whitespace-nowrap">{routesLabel(r)}</td>
                  <td className="px-2 py-2">
                    {canAdmin && (
                      <div className="flex items-center gap-1">
                        <button onClick={() => toggleMutation.mutate(r)}
                          className={`p-1.5 rounded-lg ${r.isEnabled ? 'text-emerald-600 hover:bg-emerald-500/15' : 'text-outline hover:bg-surface-low'}`}
                          title={r.isEnabled ? t('alerts:disable') : t('alerts:enable')}>
                          <Power size={16} />
                        </button>
                        <button onClick={() => openEdit(r)} className="p-1.5 text-blue-600 hover:bg-blue-50 rounded-lg" title={t('common:edit')}>
                          <Edit size={16} />
                        </button>
                        <button onClick={async () => { if (await confirmDialog({ message: t('alerts:deleteConfirm', { name: r.name }), danger: true })) deleteMutation.mutate(r.id); }}
                          className="p-1.5 text-red-600 hover:bg-red-500/15 rounded-lg" title={t('common:delete')}>
                          <TrashCan size={16} />
                        </button>
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div></div>
      ))}
      {showEditor && canAdmin && (
        <AlertingRuleEditor rule={editing} onClose={() => setShowEditor(false)} />
      )}
      {showDeliveries && <DeliveriesModal onClose={() => setShowDeliveries(false)} />}
    </div>
  );
}
