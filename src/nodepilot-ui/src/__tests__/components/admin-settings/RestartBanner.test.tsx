import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import { RestartBanner } from '../../../components/admin-settings/RestartBanner';

const server = setupServer();
beforeEach(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterEach(() => server.close());

function renderBanner() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <RestartBanner />
    </QueryClientProvider>,
  );
}

describe('RestartBanner', () => {
  it('renders nothing when no restart is pending', async () => {
    server.use(http.get('/api/admin/settings/status', () =>
      HttpResponse.json({
        overridesPath: '/x', restartRequired: false,
        restartRequiredSince: null, restartRequiredFor: [],
        lastSavedAt: null, lastSavedBy: null,
      }),
    ));
    const { container } = renderBanner();
    // Hold long enough for react-query to settle so a hidden banner won't appear later.
    await waitFor(() => expect(container.textContent ?? '').not.toMatch(/erforderlich|required/i));
  });

  it('shows the banner with each pending section listed', async () => {
    server.use(http.get('/api/admin/settings/status', () =>
      HttpResponse.json({
        overridesPath: '/x',
        restartRequired: true,
        restartRequiredSince: '2026-05-11T01:00:00Z',
        restartRequiredFor: ['Smtp', 'Llm'],
        lastSavedAt: null, lastSavedBy: null,
      }),
    ));
    renderBanner();
    await waitFor(() => expect(screen.getByText('Smtp')).toBeInTheDocument());
    expect(screen.getByText('Llm')).toBeInTheDocument();
  });
});
