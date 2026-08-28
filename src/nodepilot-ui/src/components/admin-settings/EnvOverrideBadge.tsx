import { WarningAltFilled } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';

type Props = {
  /** Source token from the backend, e.g. <c>"env"</c> or <c>"cli"</c>. */
  source: string;
/** Config-key path that hints which environment variable to inspect. */
  configKey: string;
};

/**
 * Small inline warning badge for fields whose effective value comes from an environment
 * variable or command-line argument. A UI save still writes to the override file, but the
 * value won't take effect because the higher-priority source wins.
 *
 * The badge itself is non-blocking; the parent is responsible for keeping the input read-only.
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
