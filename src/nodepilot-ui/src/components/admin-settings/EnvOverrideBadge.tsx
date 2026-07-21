import { WarningAltFilled } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';

type Props = {
  /** Source token from the backend, e.g. <c>"env"</c> or <c>"cli"</c>. */
  source: string;
  /** Config-key path used to hint the operator at which env var to inspect. */
  configKey: string;
};

/**
 * Renders a small inline warning badge for fields whose effective value comes from an
 * environment variable or command-line argument — i.e. a UI save will be persisted to
 * the override file but won't take effect because a higher-priority source wins.
 *
 * <para>The badge is intentionally non-blocking: the underlying input stays read-only
 * (parent's responsibility), but the rest of the form still works. This mirrors how
 * Kubernetes operators expect env-injection to override file-based config.</para>
 */
export function EnvOverrideBadge({ source, configKey }: Readonly<Props>) {
  const { t } = useTranslation(['adminSettings']);
  if (source !== 'env' && source !== 'cli') return null;

  // Env-var name mirrors ASP.NET Core conventions: replace ":" with "__".
  const envVarName = configKey.replaceAll(/:/g, '__');
  const tooltip = source === 'env'
    ? t('adminSettings:envBadgeTooltip', { key: envVarName })
    : t('adminSettings:cliBadgeTooltip');

  return (
    <span
      className="inline-flex items-center gap-1 px-2 py-0.5 text-[11px] font-medium rounded-full bg-amber-100 text-amber-800 cursor-help"
      title={tooltip}
      aria-label={tooltip}
    >
      <WarningAltFilled size={11} />
      {t('adminSettings:envBadgeLabel')}
    </span>
  );
}
