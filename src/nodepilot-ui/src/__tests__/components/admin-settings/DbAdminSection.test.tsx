import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import { DbAdminSection } from '../../../components/admin-settings/DbAdminSection';

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

function snapshot(overrides: Partial<{ allowWriteQueries: boolean; queryTimeoutSeconds: number; queryMaxRows: number }> = {}) {
  return {
    sectionPath: 'DbAdmin',
    payload: {
      allowWriteQueries: overrides.allowWriteQueries ?? false,
      queryTimeoutSeconds: overrides.queryTimeoutSeconds ?? 30,
      queryMaxRows: overrides.queryMaxRows ?? 10_000,
    },
    etag: '"d-1"',
    isHotReloadable: true,
    effectiveSource: {},
  };
}

function renderSection(snap = snapshot()) {
  server.use(http.get('/api/admin/settings/DbAdmin', () => HttpResponse.json(snap)));
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <DbAdminSection />
    </QueryClientProvider>,
  );
}

describe('DbAdminSection', () => {
  it('renders the three controls with current values', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('30')).toBeInTheDocument());
    expect(screen.getByDisplayValue('10000')).toBeInTheDocument();
    // Toggle is unchecked by default
    const toggle = screen.getByRole('checkbox') as HTMLInputElement;
    expect(toggle.checked).toBe(false);
  });

  it('toggling write-queries off→on opens the confirm dialog instead of immediately enabling', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('30')).toBeInTheDocument());

    const toggle = screen.getByRole('checkbox') as HTMLInputElement;
    fireEvent.click(toggle);

    // The dialog should appear; the underlying checkbox should NOT yet be checked.
    await waitFor(() => expect(screen.getByRole('heading', { name: /Enable write queries|Schreib-Queries aktivieren/ })).toBeInTheDocument());
    expect(toggle.checked).toBe(false);
  });

  it('confirm dialog requires the exact ALLOW WRITE phrase before enabling', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('30')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('checkbox'));

    await waitFor(() => expect(screen.getByRole('heading', { name: /Enable write queries|Schreib-Queries aktivieren/ })).toBeInTheDocument());

    // Confirm button is disabled until the phrase is typed exactly.
    const confirmBtn = screen.getByRole('button', { name: /Yes, enable|Ja, aktivieren/ });
    expect(confirmBtn).toBeDisabled();

    const phraseInput = document.querySelectorAll('input[type="text"]')[0] as HTMLInputElement;
    fireEvent.change(phraseInput, { target: { value: 'allow write' } }); // wrong case
    expect(confirmBtn).toBeDisabled();

    fireEvent.change(phraseInput, { target: { value: 'ALLOW WRITE' } });
    expect(confirmBtn).not.toBeDisabled();

    fireEvent.click(confirmBtn);

    const toggle = screen.getByRole('checkbox') as HTMLInputElement;
    expect(toggle.checked).toBe(true);
  });

  it('disabling write-queries when they are server-enabled does NOT trigger the confirm dialog', async () => {
    renderSection(snapshot({ allowWriteQueries: true }));
    await waitFor(() => expect(screen.getByDisplayValue('30')).toBeInTheDocument());

    const toggle = screen.getByRole('checkbox') as HTMLInputElement;
    expect(toggle.checked).toBe(true);
    fireEvent.click(toggle);

    // No confirmation gate on the "remove power" direction — the dialog stays absent.
    expect(screen.queryByRole('heading', { name: /Enable write queries|Schreib-Queries aktivieren/ })).not.toBeInTheDocument();
    expect(toggle.checked).toBe(false);
  });

  it('Save sends PascalCase keys matching the backend DTO shape', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/DbAdmin', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...snapshot(), etag: '"d-2"' });
    }));

    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('30')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /speichern|save/i }));

    await waitFor(() => {
       
      const body = putBody as any;
      expect(body?.AllowWriteQueries).toBe(false);
      expect(body?.QueryTimeoutSeconds).toBe(30);
      expect(body?.QueryMaxRows).toBe(10_000);
    });
  });
});
