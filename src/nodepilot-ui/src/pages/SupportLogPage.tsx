import { useTranslation } from 'react-i18next';
import { SupportLogViewerSection } from '../components/admin-settings/SupportLogViewerSection';

/**
 * Admin-only standalone page (moved out of Settings → System so it's directly reachable
 * during an incident instead of buried two tabs deep).
 */
export function SupportLogPage() {
  const { t } = useTranslation('supportLog');
  return (
    <div className="np-fade-up">
      <header className="mb-5">
        <p className="text-sm text-on-surface-variant font-label max-w-3xl">{t('subtitle')}</p>
      </header>
      <SupportLogViewerSection />
    </div>
  );
}
