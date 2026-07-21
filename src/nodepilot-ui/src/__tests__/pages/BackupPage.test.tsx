import { describe, it, expect, beforeAll, afterAll, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { BackupPage } from '../../pages/BackupPage';

const BASE = 'http://localhost';

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/')) return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

const server = setupServer(
  http.get(`${BASE}/api/backup/manifest`, () =>
    HttpResponse.json({
      sections: [
        { section: 'workflows', count: 3 },
        { section: 'credentials', count: 2 },
      ],
    })
  )
);

beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

function renderPage() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  patchFetch();
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <BackupPage />
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

function LocationProbe() {
  const location = useLocation();
  return <output data-testid="location">{location.pathname}{location.search}</output>;
}

describe('BackupPage', () => {
  it('writes the selected primary tab to the URL', () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: /^Restore$/i }));
    expect(screen.getByTestId('location')).toHaveTextContent('/?tab=restore');
  });

  it('lists backupable sections with counts from the manifest', async () => {
    renderPage();
    expect(await screen.findByText('Workflows')).toBeInTheDocument();
    expect(screen.getByText('Credentials')).toBeInTheDocument();
    expect(screen.getByText('3')).toBeInTheDocument();
  });

  it('rejects mismatched passphrases without downloading', async () => {
    const { container } = renderPage();
    await screen.findByText('Workflows');

    const [pass, confirm] = container.querySelectorAll<HTMLInputElement>('input[type=password]');
    fireEvent.change(pass, { target: { value: 'longenoughpass' } });
    fireEvent.change(confirm, { target: { value: 'differentpass1' } });
    fireEvent.click(screen.getByRole('button', { name: /Download backup/i }));

    expect(await screen.findByText('Passphrases do not match.')).toBeInTheDocument();
  });

  it('rejects a too-short passphrase', async () => {
    const { container } = renderPage();
    await screen.findByText('Workflows');

    const [pass, confirm] = container.querySelectorAll<HTMLInputElement>('input[type=password]');
    fireEvent.change(pass, { target: { value: 'short' } });
    fireEvent.change(confirm, { target: { value: 'short' } });
    fireEvent.click(screen.getByRole('button', { name: /Download backup/i }));

    expect(await screen.findByText(/Passphrase must be at least 12 characters/i)).toBeInTheDocument();
  });

  it('previews a restore and renders the per-section diff with integrity status', async () => {
    server.use(
      http.post(`${BASE}/api/backup/preview`, () =>
        HttpResponse.json({
          integrityVerified: false,
          appVersion: '1.2.3',
          sections: [{ section: 'workflows', inBackup: 3, new: 2, conflicts: 1 }],
          warnings: [],
        })
      )
    );
    const { container } = renderPage();
    await screen.findByText('Workflows');

    fireEvent.click(screen.getByRole('button', { name: /^Restore$/i }));

    const fileInput = container.querySelector<HTMLInputElement>('input[type=file]')!;
    const file = new File(['{}'], 'backup.npbackup', { type: 'application/json' });
    fireEvent.change(fileInput, { target: { files: [file] } });

    fireEvent.click(screen.getByRole('button', { name: /Preview/i }));

    expect(await screen.findByText(/Integrity unverified/i)).toBeInTheDocument();
    // The conflict count for the workflows section is surfaced.
    await waitFor(() => expect(screen.getByText('1')).toBeInTheDocument());
  });
});
