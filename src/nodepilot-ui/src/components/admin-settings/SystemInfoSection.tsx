import { BareMetalServer, Branch, DataBase, Information, Locked, Password } from '@carbon/icons-react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation, Trans } from 'react-i18next';
import { adminSettings } from '../../api/adminSettings';

/**
 * Read-only system-info tab — surfaces the bootstrap-only configuration (DB connection
 * host, secrets provider, cluster state, JWT issuer/audience, override file path).
 * No Save flow; this is purely a "where does my data live?" panel for operators who
 * are about to escalate a config problem to someone else.
 */
export function SystemInfoSection() {
  const { t } = useTranslation(['adminSettings']);
  const { data, isLoading, error } = useQuery({
    queryKey: ['admin-settings', 'system-info'],
    queryFn: () => adminSettings.getSystemInfo(),
  });

  if (isLoading) return <p className="text-sm">{t('adminSettings:loading')}</p>;
  if (error || !data) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-md p-3 text-red-900 text-sm">
        {error instanceof Error ? error.message : t('adminSettings:systemInfo.loadFailed')}
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <Card icon={BareMetalServer} title={t('adminSettings:systemInfo.runtime')}>
        <InfoRow label={t('adminSettings:systemInfo.appVersion')} value={data.appVersion} />
        <InfoRow label={t('adminSettings:systemInfo.overrideFile')} value={data.overridesPath} mono />
      </Card>
      <Card icon={DataBase} title={t('adminSettings:systemInfo.database')}>
        <InfoRow label={t('adminSettings:systemInfo.provider')} value={data.databaseProvider} mono />
        <InfoRow label="Host" value={data.databaseHost ?? '—'} mono />
      </Card>
      <Card icon={Locked} title={t('adminSettings:systemInfo.secrets')}>
        <InfoRow label={t('adminSettings:systemInfo.activeProvider')} value={data.secretsProvider} mono />
      </Card>
      <Card icon={Branch} title={t('adminSettings:systemInfo.cluster')}>
        <InfoRow label={t('adminSettings:enabled')} value={data.clusterEnabled ? 'true' : 'false'} mono />
        <InfoRow label={t('adminSettings:systemInfo.nodeId')} value={data.clusterNodeId} mono />
        <InfoRow label={t('adminSettings:systemInfo.isLeader')} value={data.clusterIsLeader ? 'true' : 'false'} mono />
      </Card>
      <Card icon={Password} title="JWT">
        <InfoRow label={t('adminSettings:systemInfo.issuer')} value={data.jwtIssuer} mono />
        <InfoRow label={t('adminSettings:systemInfo.audience')} value={data.jwtAudience} mono />
      </Card>
      <div className="text-xs text-on-surface-variant flex items-start gap-2">
        <Information size={14} className="mt-0.5 shrink-0" />
        <p>
          <Trans i18nKey="systemInfo.bootstrapNote" ns="adminSettings" components={[<code key="0" />]} />
        </p>
      </div>
    </div>
  );
}

function Card({ icon: Icon, title, children }: Readonly<{ icon: React.ComponentType<{ size?: number }>; title: string; children: React.ReactNode }>) {
  return (
    <div className="np-card p-4">
      <h3 className="font-semibold text-on-surface flex items-center gap-2 mb-3">
        <Icon size={18} /> {title}
      </h3>
      <div className="space-y-1.5">{children}</div>
    </div>
  );
}

function InfoRow({ label, value, mono }: Readonly<{ label: string; value: string; mono?: boolean }>) {
  return (
    <div className="grid grid-cols-3 gap-2 text-sm">
      <span className="text-on-surface-variant col-span-1">{label}</span>
      <span className={`col-span-2 ${mono ? 'font-mono text-xs' : ''} break-all`}>{value}</span>
    </div>
  );
}
