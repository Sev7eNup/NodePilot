import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useSearchParams } from 'react-router';
import { SystemSettingsPage } from '../../pages/SystemSettingsPage';

// The page's own job is tab selection: which section is active, and how `?section=` maps onto
// it. The nine sections each fetch their own config, so they are stubbed down to a marker —
// mounting them would test their data loading, not this page's routing.
vi.mock('../../components/admin-settings/RestartBanner', () => ({ RestartBanner: () => null }));
vi.mock('../../components/admin-settings/IntegrationsSection', () => ({ IntegrationsSection: () => <div>section:integrations</div> }));
vi.mock('../../components/admin-settings/AiKnowledgeSection', () => ({ AiKnowledgeSection: () => <div>section:ai-knowledge</div> }));
vi.mock('../../components/admin-settings/AuthenticationSection', () => ({ AuthenticationSection: () => <div>section:authentication</div> }));
vi.mock('../../components/admin-settings/SecuritySection', () => ({ SecuritySection: () => <div>section:security</div> }));
vi.mock('../../components/admin-settings/LoggingTelemetrySection', () => ({ LoggingTelemetrySection: () => <div>section:logging-telemetry</div> }));
vi.mock('../../components/admin-settings/PerformanceSection', () => ({ PerformanceSection: () => <div>section:performance</div> }));
vi.mock('../../components/admin-settings/DbAdminSection', () => ({ DbAdminSection: () => <div>section:db-admin</div> }));
vi.mock('../../components/admin-settings/RetentionSection', () => ({ RetentionSection: () => <div>section:retention</div> }));
vi.mock('../../components/admin-settings/SystemInfoSection', () => ({ SystemInfoSection: () => <div>section:system-info</div> }));

const SUB_TABS = [
  'integrations', 'ai-knowledge', 'authentication', 'security',
  'logging-telemetry', 'performance', 'db-admin', 'retention', 'system-info',
];

function SearchParamsProbe() {
  const [params] = useSearchParams();
  return <div data-testid="query">{params.toString()}</div>;
}

function renderPage(path = '/settings?tab=system') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/settings" element={<><SystemSettingsPage /><SearchParamsProbe /></>} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('SystemSettingsPage', () => {
  it('renders one tab per sub-tab and opens the default section', () => {
    renderPage();

    expect(screen.getAllByRole('button')).toHaveLength(SUB_TABS.length);
    expect(screen.getByText('section:integrations')).toBeInTheDocument();
  });

  it('labels every tab from the i18n catalogue, never a raw key', () => {
    renderPage();

    for (const button of screen.getAllByRole('button')) {
      // A missing translation makes i18next echo the key back — "adminSettings:subTabSecurity"
      // rendered as a label is exactly the drift this asserts against.
      expect(button.textContent?.trim()).not.toBe('');
      expect(button.textContent).not.toContain('subTab');
      expect(button.textContent).not.toContain('adminSettings:');
    }
  });

  it.each(SUB_TABS)('opens %s from a ?section= deep link', (section) => {
    renderPage(`/settings?tab=system&section=${section}`);

    expect(screen.getByText(`section:${section}`)).toBeInTheDocument();
  });

  it('falls back to the default section for an unknown ?section= value', () => {
    renderPage('/settings?tab=system&section=does-not-exist');

    expect(screen.getByText('section:integrations')).toBeInTheDocument();
  });

  it('writes the selected section into the query string and keeps tab=system', async () => {
    renderPage();

    const securityTab = screen.getAllByRole('button').find((b) => b.textContent?.includes('Security'));
    await userEvent.click(securityTab!);

    expect(screen.getByText('section:security')).toBeInTheDocument();
    expect(screen.getByTestId('query').textContent).toBe('tab=system&section=security');
  });
});
