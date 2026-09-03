import { describe, it, expect, beforeAll, afterAll, afterEach, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { Sidebar } from '../../../components/layout/Sidebar';
import { useAuthStore } from '../../../stores/authStore';

const BASE = 'http://localhost';

// The api client fetches relative `/api/...` URLs; jsdom needs an absolute base, so rewrite
// leading-slash requests onto BASE where MSW is listening (mirrors DashboardPage.test).
function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/')) return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

// Only the totals the sidebar badges read; the real endpoint returns more fields.
const STATS = {
  workflowsTotal: 24, workflowsEnabled: 20, machinesTotal: 128, machinesReachable: 120,
  executionsTotal: 999, runningCount: 3, pendingCount: 0, longRunningCount: 0,
};
const RULES = Array.from({ length: 7 }, (_, i) => ({ id: `r${i}`, name: `rule ${i}` }));

const server = setupServer(
  http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(STATS)),
  http.get(`${BASE}/api/alerting/rules`, () => HttpResponse.json(RULES)),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  vi.restoreAllMocks();
  useAuthStore.setState({ userId: null, username: null, role: null, isAuthenticated: false });
});
afterAll(() => server.close());

function renderSidebar(role = 'Admin', initialPath = '/') {
  useAuthStore.setState({ userId: 'u1', username: 'admin', role, isAuthenticated: true });
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  patchFetch();
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[initialPath]}>
        <Sidebar />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('Sidebar', () => {
  it('renders all four labeled sections', async () => {
    renderSidebar('Admin');
    expect(await screen.findByText('Workspace')).toBeInTheDocument();
    expect(screen.getByText('Infrastructure')).toBeInTheDocument();
    expect(screen.getByText('Monitoring')).toBeInTheDocument();
    expect(screen.getByText('Administration')).toBeInTheDocument();
  });

  it('shows live count badges from the stats + alerting endpoints', async () => {
    renderSidebar('Admin');
    expect(await screen.findByText('24')).toBeInTheDocument();  // workflows total
    expect(await screen.findByText('3')).toBeInTheDocument();   // running executions
    expect(await screen.findByText('128')).toBeInTheDocument(); // machines total
    expect(await screen.findByText('7')).toBeInTheDocument();   // alerting rules
  });

  it('renders Carbon nav icons and marks the current route', async () => {
    const { container } = renderSidebar('Admin', '/executions');
    const executions = await screen.findByRole('link', { name: /executions/i });
    expect(executions).toHaveAttribute('aria-current', 'page');
    // Nav icons are Carbon SVG components, not Bootstrap icon-font <i> elements.
    expect(executions.querySelector('.np-nav-icon > svg')).toBeInTheDocument();
    expect(container.querySelector('a[href="/workflows"] .np-nav-icon > svg')).toBeInTheDocument();
    expect(container.querySelector('.np-nav-icon > i')).not.toBeInTheDocument();
  });

  it('live-filters nav items by label and drops empty sections', async () => {
    renderSidebar('Admin');
    fireEvent.change(screen.getByLabelText('Search menu'), { target: { value: 'audit' } });
    expect(await screen.findByText('Audit Log')).toBeInTheDocument();
    expect(screen.queryByText('Dashboard')).not.toBeInTheDocument();
    expect(screen.queryByText('Workspace')).not.toBeInTheDocument();
    expect(screen.getByText('Administration')).toBeInTheDocument(); // still has the match
  });

  it('shows a no-results message when nothing matches', async () => {
    renderSidebar('Admin');
    await screen.findByText('Workspace');
    fireEvent.change(screen.getByLabelText('Search menu'), { target: { value: 'zzz-nope' } });
    expect(screen.getByText('No matches')).toBeInTheDocument();
  });

  it('focuses the search on Ctrl-K', async () => {
    renderSidebar('Admin');
    const input = await screen.findByLabelText('Search menu');
    expect(input).not.toHaveFocus();
    fireEvent.keyDown(window, { key: 'k', ctrlKey: true });
    expect(input).toHaveFocus();
  });

  it('renders the account panel with role title and a toggling "…" menu', async () => {
    renderSidebar('Admin');
    expect(await screen.findByText('admin')).toBeInTheDocument();
    expect(screen.getByText('System Administrator')).toBeInTheDocument();
    // Menu closed: only the footer Logout entry is present.
    expect(screen.getAllByText('Logout')).toHaveLength(1);
    fireEvent.click(screen.getByLabelText('Account menu'));
    // Menu open: the popover adds Settings and Logout entries.
    expect(screen.getAllByText('Logout')).toHaveLength(2);
  });

  it('invokes logout from the footer button', async () => {
    const logout = vi.fn();
    useAuthStore.setState({ logout });
    renderSidebar('Admin');
    fireEvent.click(await screen.findByText('Logout'));
    expect(logout).toHaveBeenCalled();
  });

  // /docs is a second document served by the API, not a route of this SPA. It has to be a real
  // anchor: a react-router Link would navigate client-side, match no route and render the
  // not-found page, so the request would never reach the server. The trailing slash matters too
  // — the docs bundle resolves its assets against the document url.
  it('links to the bundled documentation from the footer controls', async () => {
    renderSidebar('Admin');

    const link = await screen.findByRole('link', { name: 'Documentation' });
    expect(link).toHaveAttribute('href', '/docs/');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  });

  it('hides admin-only items and the alerts badge for a Viewer', async () => {
    renderSidebar('Viewer');
    // dashboard-stats is open to every role, so the workflows badge still resolves.
    expect(await screen.findByText('24')).toBeInTheDocument();
    // The alerting-rule count is Admin/Operator-only, so a Viewer gets no alerts badge.
    expect(screen.queryByText('7')).not.toBeInTheDocument();
    // Admin-only nav items are filtered out.
    expect(screen.queryByText('Users')).not.toBeInTheDocument();
    expect(screen.queryByText('Audit Log')).not.toBeInTheDocument();
    // Non-admin content stays; Administration survives because Settings is not admin-only.
    expect(screen.getByText('Dashboard')).toBeInTheDocument();
    expect(screen.getByText('Administration')).toBeInTheDocument();
  });
});
