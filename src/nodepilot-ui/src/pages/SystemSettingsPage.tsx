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
import { useSearchParams } from 'react-router';
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

// Order is grouped by topic, not by implementation history: outbound integrations →
// security → operations → data. `integrations` stays first because it is also the
// default/fallback section for a bare `?tab=system`, and `system-info` sits LAST because
// it is the only read-only tab here — it belongs after everything that edits config
// rather than splitting that run.
// Order is presentation only: nothing indexes into this array and deep links address a
// section by `?section=<id>`, so reordering breaks no bookmark.
const TABS: SubTab[] = [
  // External connections — AI knowledge sources hang off the LLM profile configured next door.
  'integrations',
  'ai-knowledge',
  // Security — the two access/hardening tabs stay adjacent.
  'authentication',
  'security',
  // Operations.
  'logging-telemetry',
  'performance',
  // Data lifecycle — retention is a database-side concern, so it follows the DB tab.
  'db-admin',
  'retention',
  // Read-only, always last.
  'system-info',
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

// i18n key per sub-tab. Doubles as the whitelist for the `?section=` deep link: a value is
// accepted iff it is a key here, so a new sub-tab cannot be link-addressable without a label.
const LABEL_KEYS: Record<SubTab, string> = {
  'integrations': 'subTabIntegrations',
  'ai-knowledge': 'subTabAiKnowledge',
  'retention': 'subTabRetention',
  'system-info': 'subTabSystemInfo',
  'authentication': 'subTabAuthentication',
  'logging-telemetry': 'subTabLoggingTelemetry',
  'security': 'subTabSecurity',
  'performance': 'subTabPerformance',
  'db-admin': 'subTabDbAdmin',
};

const DEFAULT_SUB_TAB: SubTab = 'integrations';

function isSubTab(value: string | null): value is SubTab {
  return value !== null && Object.prototype.hasOwnProperty.call(LABEL_KEYS, value);
}

export function SystemSettingsPage() {
  const { t } = useTranslation(['adminSettings']);
  // Deep-link: /settings?tab=system&section=<subTab> opens the requested sub-tab directly.
  // The dashboard's "LLM config" shortcut targets `integrations` (SMTP + LLM cards).
  const [searchParams, setSearchParams] = useSearchParams();
  const sectionParam = searchParams.get('section');
  const active: SubTab = isSubTab(sectionParam) ? sectionParam : DEFAULT_SUB_TAB;
  const setActive = (next: SubTab) => {
    const params = new URLSearchParams(searchParams);
    params.set('tab', 'system');
    params.set('section', next);
    setSearchParams(params);
  };

  const labelFor = (tab: SubTab): string => t(`adminSettings:${LABEL_KEYS[tab]}`);

  return (
    <div className="space-y-4">
      <RestartBanner />

      <div className="np-tab-list">
        {TABS.map((id) => {
          const Icon = ICONS[id];
          return (
            <button
              key={id}
              type="button"
              onClick={() => setActive(id)}
              className={`np-tab ${active === id ? 'is-active' : ''}`}
            >
              <Icon size={14} />
              {labelFor(id)}
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
