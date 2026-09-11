import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { FailureCauses } from '../../../components/dashboard/FailureCauses';
import i18n from '../../../i18n';

const endpoint = 'http://localhost/api/stats/failure-causes';
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterAll(() => server.close());
afterEach(async () => { server.resetHandlers(); vi.restoreAllMocks(); await i18n.changeLanguage('en'); });

const message = 'Connection refused on server-A /var/log/run.log (503)';
const response = {
  totalFailed: 10, remainingCount: 2,
  groups: [{ message, count: 8, latestExecutionId: 'execution-1', latestStartedAt: '2026-09-11T10:00:00Z' }],
};

function Location() {
  const location = useLocation();
  return <div aria-label="location">{location.pathname}{location.search}</div>;
}

function setup(hours = 24) {
  const fetch = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => fetch(
    typeof input === 'string' && input.startsWith('/') ? `http://localhost${input}` : input, init,
  ));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  function Tree({ windowHours }: { windowHours: number }) {
    return <QueryClientProvider client={client}><MemoryRouter><FailureCauses windowHours={windowHours} /><Location /></MemoryRouter></QueryClientProvider>;
  }
  const result = render(<Tree windowHours={hours} />);
  return { ...result, setHours: (windowHours: number) => result.rerender(<Tree windowHours={windowHours} />) };
}

describe('FailureCauses', () => {
  it('shows counts, share of all failures and remaining executions, and links to the latest example', async () => {
    server.use(http.get(endpoint, () => HttpResponse.json(response)));
    setup();
    const link = await screen.findByRole('link', { name: `Open latest failed execution: ${message}` });
    expect(link).toHaveTextContent('8 executions · 80%');
    expect(screen.getByText('2 executions with other messages')).toBeInTheDocument();
    await userEvent.click(link);
    expect(screen.getByLabelText('location')).toHaveTextContent('/executions?id=execution-1');
  });

  it('loads the selected window separately and does not display the previous window while loading', async () => {
    const hours: string[] = [];
    server.use(http.get(endpoint, ({ request }) => {
      const h = new URL(request.url).searchParams.get('windowHours')!;
      hours.push(h);
      return HttpResponse.json(h === '24' ? response : { totalFailed: 0, remainingCount: 0, groups: [] });
    }));
    const { setHours } = setup();
    await screen.findByText(message);
    setHours(168);
    expect(screen.queryByText(message)).not.toBeInTheDocument();
    await screen.findByText('No failed executions in the selected period.');
    expect(hours).toEqual(['24', '168']);
  });

  it('has a loading state', () => {
    server.use(http.get(endpoint, () => new Promise(() => {})));
    setup();
    expect(screen.getByRole('status')).toHaveTextContent(/loading/i);
  });

  it('retries a failed request', async () => {
    let attempts = 0;
    server.use(http.get(endpoint, () => ++attempts === 1 ? HttpResponse.json({}, { status: 500 }) : HttpResponse.json(response)));
    setup();
    expect(await screen.findByRole('alert')).toHaveTextContent('Could not load failure causes.');
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(attempts).toBe(2);
  });

  it('expands long messages with the keyboard without navigating and renders markup as text', async () => {
    const longMessage = '<script>alert(1)</script> ' + 'A long path without whitespace/'.repeat(15) + ' END';
    server.use(http.get(endpoint, () => HttpResponse.json({ ...response, groups: [{ ...response.groups[0], message: longMessage }] })));
    setup();
    const toggle = await screen.findByRole('button', { name: 'Show full message' });
    expect(screen.queryByText(longMessage)).not.toBeInTheDocument();
    toggle.focus();
    await userEvent.keyboard('{Enter}');
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText(longMessage)).toBeInTheDocument();
    expect(screen.getByLabelText('location').textContent).toBe('/');
    expect(document.querySelector('script')).toBeNull();
    await userEvent.keyboard(' ');
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('localizes missing messages and percentages in German', async () => {
    await i18n.changeLanguage('de');
    server.use(http.get(endpoint, () => HttpResponse.json({ totalFailed: 3, remainingCount: 2, groups: [{ ...response.groups[0], message: null, count: 1 }] })));
    setup();
    expect(await screen.findByText('Keine Fehlermeldung vorhanden')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('link')).toHaveTextContent(/1 Ausführung · 33,3/));
  });
});
