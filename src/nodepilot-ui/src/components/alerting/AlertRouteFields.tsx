import { TrashCan } from '@carbon/icons-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { UNCHANGED_SECRET } from '../../api/alerting';
import type { NotificationChannel } from '../../lib/eventFields';

/** Route fields shared by the custom-rule editor and the system-policy editor. */
export type AlertRouteBase = {
  channel: NotificationChannel;
  target: string;
  hasStoredSecret: boolean;
  secretInput: string;
};

/**
 * Secret value for the save body. Only webhook routes carry one, so a secret typed before the channel
 * was switched away is not persisted. A kept secret is sent as the unchanged sentinel, a cleared one as null.
 */
export function routeSecretForSave(r: AlertRouteBase): string | null {
  if (r.channel !== 'GenericWebhook') return null;
  if (r.secretInput) return r.secretInput;
  return r.hasStoredSecret ? UNCHANGED_SECRET : null;
}

/** One editable route row: channel, target, webhook secret with a clear action, and remove. */
export function AlertRouteFields({
  route, channels, onChange, onRemove, children,
}: Readonly<{
  route: AlertRouteBase;
  channels: readonly string[];
  onChange: (patch: Partial<AlertRouteBase>) => void;
  onRemove: () => void;
  children?: ReactNode;
}>) {
  const { t } = useTranslation(['alerts', 'common']);
  return (
    <div className="flex flex-wrap items-center gap-2 border border-outline-variant/60 rounded-md p-2 bg-surface-lowest">
      <select value={route.channel} onChange={(e) => onChange({ channel: e.target.value as NotificationChannel })}
        className="text-xs border border-outline-variant/50 rounded px-1 py-1.5 bg-surface-lowest">
        {channels.map((c) => (
          <option key={c} value={c}>{t(`alerts:channels.${c}`)}</option>
        ))}
      </select>
      <input type="text" value={route.target} onChange={(e) => onChange({ target: e.target.value })}
        placeholder={route.channel === 'Email' ? t('alerts:emailPlaceholder') : t('alerts:webhookPlaceholder')}
        className="flex-1 min-w-[160px] px-2 py-1.5 border border-outline-variant rounded text-xs font-mono" />
      {route.channel === 'GenericWebhook' && (
        <div className="flex items-center gap-1">
          <input type="password" value={route.secretInput} onChange={(e) => onChange({ secretInput: e.target.value })}
            placeholder={route.hasStoredSecret ? t('alerts:secretStored') : t('alerts:secretPlaceholder')}
            className="w-40 px-2 py-1.5 border border-outline-variant rounded text-xs font-mono" />
          {route.hasStoredSecret && !route.secretInput && (
            <button type="button" onClick={() => onChange({ hasStoredSecret: false, secretInput: '' })}
              className="text-[11px] px-1.5 py-1 text-outline hover:text-red-600 whitespace-nowrap">
              {t('alerts:clearSecret')}
            </button>
          )}
        </div>
      )}
      <button type="button" onClick={onRemove} className="p-1.5 text-outline hover:text-red-600" title={t('common:delete')}>
        <TrashCan size={14} />
      </button>
      {children && <div className="basis-full border-t border-outline-variant/50 pt-2">{children}</div>}
    </div>
  );
}
