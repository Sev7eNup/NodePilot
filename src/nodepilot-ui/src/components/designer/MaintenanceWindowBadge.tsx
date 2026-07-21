import { CalendarSettings } from '@carbon/icons-react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';

type AffectingWindow = { id: string; name: string; mode: string; isEnabled: boolean; activeNow: boolean };

/**
 * Read-only "which maintenance windows affect this workflow" banner. Self-fetches from
 * <c>GET /api/maintenance-windows/affecting/{workflowId}</c> and renders nothing when no window
 * targets the workflow — so it never adds chrome unless it has something to say. This is the
 * inverse view of the central windows admin page: operators see here why a run might be blocked.
 */
export function MaintenanceWindowBadge({ workflowId }: Readonly<{ workflowId: string | undefined }>) {
  const { t } = useTranslation(['maintenance']);
  const { data } = useQuery({
    queryKey: ['maintenance-affecting', workflowId],
    queryFn: () => api.get<AffectingWindow[]>(`/maintenance-windows/affecting/${workflowId}`),
    enabled: !!workflowId,
    staleTime: 30_000,
  });

  if (!data || data.length === 0) return null;
  const activeCount = data.filter((w) => w.activeNow).length;

  return (
    <div className="wd-strip flex items-center gap-2 px-4 py-1.5 bg-warning-container/60 border-b border-warning/30 text-xs text-on-warning-container">
      <CalendarSettings size={13} className="shrink-0" />
      <span className="font-medium">{t('maintenance:affectedBy', { count: data.length })}</span>
      <span className="flex flex-wrap gap-1">
        {data.map((w) => (
          <span
            key={w.id}
            className={`px-1.5 py-0.5 rounded ${w.activeNow ? 'bg-warning-container font-semibold border border-warning/40' : 'bg-warning-container/60'}`}
            title={w.mode}
          >
            {w.name}{w.activeNow ? ` · ${t('maintenance:activeNow')}` : ''}
          </span>
        ))}
      </span>
      {activeCount > 0 && <span className="ml-auto w-2 h-2 rounded-full bg-warning shrink-0" aria-hidden />}
    </div>
  );
}
