import {
  Certificate,
  Chat,
  DataBase,
  Document,
  Information,
  Locked,
  Meter,
  Plug,
  TrashCan,
} from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import { RestartBanner } from '../components/admin-settings/RestartBanner';
import { IntegrationsSection } from '../components/admin-settings/IntegrationsSection';
import { RetentionSection } from '../components/admin-settings/RetentionSection';
import { SystemInfoSection } from '../components/admin-settings/SystemInfoSection';
import { AuthenticationSection } from '../components/admin-settings/AuthenticationSection';
import { LoggingTelemetrySection } from '../components/admin-settings/LoggingTelemetrySection';
import { SecuritySection } from '../components/admin-settings/SecuritySection';
import { PerformanceSection } from '../components/admin-settings/PerformanceSection';
import { DbAdminSection } from '../components/admin-settings/DbAdminSection';
import { AiKnowledgeSection } from '../components/admin-settings/AiKnowledgeSection';

type SubTab = 'integrations' | 'ai-knowledge' | 'retention' | 'system-info'
  | 'authentication' | 'logging-telemetry' | 'security' | 'performance' | 'db-admin';

// Tabs are progressively activated as their section is implemented. Disabled tabs
// keep the operator informed about what's on the roadmap.
const TABS: { id: SubTab; ready: boolean }[] = [
  { id: 'integrations',     ready: true },
  { id: 'ai-knowledge',     ready: true },
  { id: 'retention',        ready: true },
  { id: 'system-info',      ready: true },
  { id: 'authentication',   ready: true },
  { id: 'logging-telemetry', ready: true },
  { id: 'security',         ready: true },
  { id: 'performance',      ready: true },
  { id: 'db-admin',         ready: true },
];

const ICONS: Record<SubTab, React.ComponentType<{ size?: number }>> = {
  'integrations': Plug,
  'ai-knowledge': Chat,
  'retention': TrashCan,
  'system-info': Information,
  'authentication': Locked,
  'logging-telemetry': Document,
  'security': Certificate,
  'performance': Meter,
  'db-admin': DataBase,
};

export function SystemSettingsPage() {
  const { t } = useTranslation(['adminSettings']);
  // Deep-link: /settings?tab=system&section=<subTab> opens the requested sub-tab directly.
  // The dashboard's "LLM config" shortcut targets `integrations` (SMTP + LLM cards).
  const [searchParams, setSearchParams] = useSearchParams();
  const sectionParam = searchParams.get('section');
  const initialSub: SubTab =
    sectionParam === 'integrations' || sectionParam === 'ai-knowledge' || sectionParam === 'retention'
      || sectionParam === 'system-info' || sectionParam === 'authentication'
      || sectionParam === 'logging-telemetry' || sectionParam === 'security'
      || sectionParam === 'performance' || sectionParam === 'db-admin'
      ? (sectionParam as SubTab) : 'integrations';
  const active = initialSub;
  const setActive = (next: SubTab) => {
    const params = new URLSearchParams(searchParams);
    params.set('tab', 'system');
    params.set('section', next);
    setSearchParams(params);
  };

  const labelFor = (tab: SubTab): string => {
    const key = tab === 'integrations' ? 'subTabIntegrations'
      : tab === 'ai-knowledge' ? 'subTabAiKnowledge'
      : tab === 'retention' ? 'subTabRetention'
      : tab === 'system-info' ? 'subTabSystemInfo'
      : tab === 'authentication' ? 'subTabAuthentication'
      : tab === 'logging-telemetry' ? 'subTabLoggingTelemetry'
      : tab === 'security' ? 'subTabSecurity'
      : tab === 'db-admin' ? 'subTabDbAdmin'
      : 'subTabPerformance';
    return t(`adminSettings:${key}`);
  };

  return (
    <div className="space-y-4">
      <RestartBanner />

      <div className="np-tab-list">
        {TABS.map(({ id, ready }) => {
          const Icon = ICONS[id];
          const isActive = active === id;
          if (ready) {
            return (
              <button
                key={id}
                type="button"
                onClick={() => setActive(id)}
                className={`np-tab ${isActive ? 'is-active' : ''}`}
              >
                <Icon size={14} />
                {labelFor(id)}
              </button>
            );
          }
          return (
            <button
              key={id}
              type="button"
              disabled
              title={t('adminSettings:comingSoon')}
              className="np-tab"
            >
              <Icon size={14} />
              {labelFor(id)}
              <span className="text-[10px] uppercase tracking-wide px-1.5 py-0.5 rounded bg-surface-low text-on-surface-variant">
                {t('adminSettings:comingSoon')}
              </span>
            </button>
          );
        })}
      </div>

      <div>
        {active === 'integrations' && <IntegrationsSection />}
        {active === 'ai-knowledge' && <AiKnowledgeSection />}
        {active === 'retention' && <RetentionSection />}
        {active === 'system-info' && <SystemInfoSection />}
        {active === 'authentication' && <AuthenticationSection />}
        {active === 'logging-telemetry' && <LoggingTelemetrySection />}
        {active === 'security' && <SecuritySection />}
        {active === 'performance' && <PerformanceSection />}
        {active === 'db-admin' && <DbAdminSection />}
      </div>
    </div>
  );
}

// ComingSoonPlaceholder is no longer used now that all four V2 tabs are wired up.
// Keeping the function around would emit an unused-import lint; if a future tab is
// added with a placeholder during scaffolding, re-add it here.
