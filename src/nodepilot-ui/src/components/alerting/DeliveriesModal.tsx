import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ModalShell } from '../common/ModalShell';
import { alertingApi } from '../../api/alerting';

/**
 * Read-only delivery ledger: recent notification delivery attempts (newest first), filterable by
 * status. Opened from the AlertingPage header. No secrets — only channel + target are shown.
 */
export function DeliveriesModal({ onClose }: Readonly<{ onClose: () => void }>) {
  const { t } = useTranslation(['alerts', 'common']);
  const [status, setStatus] = useState('');

  const { data: rows, isLoading } = useQuery({
    queryKey: ['alerting-deliveries', status],
    queryFn: () => alertingApi.deliveries({ status: status || undefined, limit: 200 }),
  });

  const statusClass = (s: string) =>
    s === 'Sent' ? 'text-green-600' : s === 'Failed' ? 'text-red-600' : 'text-amber-600';

  return (
    <ModalShell onClose={onClose} maxWidth="max-w-4xl">
      <div className="flex items-center justify-between mb-4 gap-3">
        <h3 className="text-lg font-semibold text-on-surface">{t('alerts:deliveries.title')}</h3>
        <select value={status} onChange={(e) => setStatus(e.target.value)}
          className="px-2 py-1.5 border border-outline-variant rounded-md text-sm bg-surface-lowest">
          <option value="">{t('alerts:deliveries.allStatuses')}</option>
          <option value="Pending">{t('alerts:deliveries.status.Pending')}</option>
          <option value="Sent">{t('alerts:deliveries.status.Sent')}</option>
          <option value="Failed">{t('alerts:deliveries.status.Failed')}</option>
        </select>
      </div>

      {isLoading ? (
        <p className="text-outline">{t('common:loadingDots')}</p>
      ) : !rows || rows.length === 0 ? (
        <div className="np-card p-8 text-center text-outline">{t('alerts:deliveries.empty')}</div>
      ) : (
        <div className="max-h-[60vh] overflow-y-auto border border-outline-variant/40 rounded-md">
          <table className="w-full text-sm">
            <thead className="np-col-header text-left text-xs font-semibold uppercase tracking-wide text-on-surface-variant sticky top-0 bg-surface-container">
              <tr>
                <th className="px-3 py-2">{t('alerts:deliveries.colTime')}</th>
                <th className="px-3 py-2">{t('alerts:deliveries.colRule')}</th>
                <th className="px-3 py-2">{t('alerts:deliveries.colRoute')}</th>
                <th className="px-3 py-2">{t('alerts:deliveries.colStatus')}</th>
                <th className="px-3 py-2">{t('alerts:deliveries.colAttempt')}</th>
                <th className="px-3 py-2">{t('alerts:deliveries.colError')}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-outline-variant/30">
              {rows.map((d) => (
                <tr key={d.id} className="hover:bg-surface-low">
                  <td className="px-3 py-2 text-xs text-on-surface-variant whitespace-nowrap">{new Date(d.createdAt).toLocaleString()}</td>
                  <td className="px-3 py-2 text-on-surface-variant">
                    {d.ruleName ?? '—'}{d.isTest && <span className="ml-1 text-[10px] text-outline">[test]</span>}
                  </td>
                  <td className="px-3 py-2 text-xs font-mono text-on-surface-variant truncate max-w-[260px]" title={`${d.channel}:${d.target}`}>
                    {d.channel}:{d.target}
                  </td>
                  <td className={`px-3 py-2 text-xs font-medium ${statusClass(d.status)}`}>{t(`alerts:deliveries.status.${d.status}`, d.status)}</td>
                  <td className="px-3 py-2 text-xs text-on-surface-variant">{d.attempt}</td>
                  <td className="px-3 py-2 text-xs text-red-600/90 truncate max-w-[280px]" title={d.error ?? ''}>{d.error ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="flex justify-end mt-4">
        <button onClick={onClose} className="px-4 py-2 text-sm text-on-surface-variant hover:bg-surface-container rounded-md">
          {t('common:close')}
        </button>
      </div>
    </ModalShell>
  );
}
