import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import { RestartBanner } from '../../../components/admin-settings/RestartBanner';
import { useConfirmStore } from '../../../stores/confirmStore';

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

  const pendingStatus = (extra: Record<string, unknown>) => ({
    overridesPath: '/x',
    restartRequired: true,
    restartRequiredSince: null,
    restartRequiredFor: ['Smtp'],
    lastSavedAt: null, lastSavedBy: null,
    ...extra,
  });

  it('hides the restart button when the server cannot restart itself', async () => {
    server.use(http.get('/api/admin/settings/status', () =>
      HttpResponse.json(pendingStatus({ canRestartService: false, restartUnavailableReason: 'NOT_A_WINDOWS_SERVICE' })),
    ));
    renderBanner();
    await waitFor(() => expect(screen.getByText('Smtp')).toBeInTheDocument());
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('hides the restart button for a server that predates the capability', async () => {
    server.use(http.get('/api/admin/settings/status', () => HttpResponse.json(pendingStatus({}))));
    renderBanner();
    await waitFor(() => expect(screen.getByText('Smtp')).toBeInTheDocument());
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('posts the restart only after the confirmation is accepted', async () => {
    let posts = 0;
    server.use(
      http.get('/api/admin/settings/status', () => HttpResponse.json(pendingStatus({ canRestartService: true }))),
      http.post('/api/admin/settings/restart-service', () => {
        posts += 1;
        return HttpResponse.json({ code: 'SERVICE_RESTART_SCHEDULED' }, { status: 202 });
      }),
    );
    renderBanner();
    const button = await screen.findByRole('button');

    await userEvent.click(button);
    await waitFor(() => expect(useConfirmStore.getState().pending).not.toBeNull());
    useConfirmStore.getState().settle(false);
    expect(posts).toBe(0);

    await userEvent.click(button);
    await waitFor(() => expect(useConfirmStore.getState().pending).not.toBeNull());
    useConfirmStore.getState().settle(true);

    await waitFor(() => expect(posts).toBe(1));
    await waitFor(() => expect(screen.getByRole('status')).toBeInTheDocument());
    expect(button).toBeDisabled();
  });
});
