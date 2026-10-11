import { BareMetalServer, WorkflowAutomation } from '@carbon/icons-react';
import { useSearchParams } from 'react-router';
import { useTranslation } from 'react-i18next';
import { AppLogViewerSection } from '../components/admin-settings/AppLogViewerSection';
import { SupportLogViewerSection } from '../components/admin-settings/SupportLogViewerSection';

type Source = 'workflow' | 'server';

/**
 * Admin-only standalone page (moved out of Settings -> System so it's directly reachable
 * during an incident instead of buried two tabs deep). One menu entry, two logs: what the
 * workflows did (support log) and what the server itself reports (application log). The
 * tab lives in the URL (`?source=server`) so it can be linked.
 */
export function SupportLogPage() {
  const { t } = useTranslation('supportLog');
  const [params, setParams] = useSearchParams();
  const source: Source = params.get('source') === 'server' ? 'server' : 'workflow';

  const choose = (next: Source) => setParams(next === 'server' ? { source: 'server' } : {}, { replace: true });

  return (
    <div className="np-fade-up space-y-4">
      <header>
        <p className="text-sm text-on-surface-variant font-label max-w-3xl">{t(`source.${source}.subtitle`)}</p>
      </header>
      <div className="np-tab-list">
        <button type="button" onClick={() => choose('workflow')}
          className={`np-tab ${source === 'workflow' ? 'is-active' : ''}`}>
          <WorkflowAutomation size={14} />
          {t('source.workflow.label')}
        </button>
        <button type="button" onClick={() => choose('server')}
          className={`np-tab ${source === 'server' ? 'is-active' : ''}`}>
          <BareMetalServer size={14} />
          {t('source.server.label')}
        </button>
      </div>
      {source === 'server' ? <AppLogViewerSection /> : <SupportLogViewerSection />}
    </div>
  );
}
