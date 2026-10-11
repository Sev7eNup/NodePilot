import { describe, it, expect, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import { AppLogViewerSection } from '../../../components/admin-settings/AppLogViewerSection';
import type { AppLogEntry, AppLogResponse } from '../../../api/diagnostics';

function entry(over: Partial<AppLogEntry>): AppLogEntry {
  return {
    timestamp: '2026-10-12T10:00:00Z', level: 'Warning', source: 'Npgsql.Connection',
    message: 'Database did not answer', template: null, details: null, truncated: false, format: 'cmtrace',
    ...over,
  };
}

function response(over: Partial<AppLogResponse> = {}): AppLogResponse {
  return {
    scan: {
      requestedSince: '2026-10-11T10:00:00Z', coverageStart: '2026-10-11T10:00:00Z', complete: true,
      budgetExhausted: false, bytesScanned: 1024, filesSkipped: 0, fragmentsDropped: 0, unparsedLines: 0,
    },
    summary: { errors: 0, warnings: 0, total: 0 },
    results: { matched: 0, returned: 0, truncated: false },
    entries: [],
    ...over,
  };
}

let body: AppLogResponse = response();
const requests: string[] = [];
const server = setupServer(
  http.get('/api/diagnostics/app-log', ({ request }) => {
    requests.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }),
);
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); body = response(); requests.length = 0; });
afterAll(() => server.close());

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><AppLogViewerSection /></QueryClientProvider>);
}

describe('AppLogViewerSection', () => {
  it('claimsNoProblemsOnlyWhenTheWholeWindowWasRead', async () => {
    renderSection();

    expect(await screen.findByRole('status')).toHaveTextContent(/no problems since/i);
    expect(screen.queryByText(/older entries may be missing/i)).toBeNull();
  });

  it('limitsTheCleanStatementToTheReadPartWhenCoverageIsIncomplete', async () => {
    body = response({
      scan: { ...response().scan, complete: false, budgetExhausted: true, bytesScanned: 8 * 1024 * 1024,
        coverageStart: '2026-10-12T08:00:00Z' },
    });
    renderSection();

    const status = await screen.findByRole('status');
    expect(status).toHaveTextContent(/no warnings or errors in the part that was read/i);
    expect(status).not.toHaveTextContent(/no problems since/i);
    expect(status).toHaveTextContent(/older entries may be missing/i);
    expect(status).toHaveTextContent(/last 8 MB were read/i);
  });

  it('showsTheServerSideTotalsAndGroupsRepeatedCauses', async () => {
    body = response({
      summary: { errors: 1, warnings: 3, total: 40 },
      results: { matched: 3, returned: 3, truncated: false },
      entries: [
        entry({ timestamp: '2026-10-12T10:05:00Z', message: 'Database did not answer after 100 ms' }),
        entry({ timestamp: '2026-10-12T10:01:00Z', message: 'Database did not answer after 250 ms' }),
        entry({ timestamp: '2026-10-12T10:03:00Z', level: 'Error', source: 'NodePilot.Engine.Foo', message: 'Engine broke' }),
      ],
    });
    renderSection();

    expect(await screen.findByRole('status')).toHaveTextContent(/1 error · 3 warnings/i);
    // The two warnings share a cause: one card with a count of 2, not two cards.
    await waitFor(() => expect(screen.getByText('2×')).toBeInTheDocument());
    expect(screen.getByText('Engine broke')).toBeInTheDocument();
    expect(screen.getAllByText(/Database did not answer/)).toHaveLength(1);
  });

  it('saysSoWhenOnlyTheNewestMatchesAreGrouped', async () => {
    body = response({
      summary: { errors: 0, warnings: 1500, total: 2000 },
      results: { matched: 1500, returned: 1000, truncated: true },
      entries: [entry({})],
    });
    renderSection();

    expect(await screen.findByText(/only the newest 1000 matches are grouped/i)).toBeInTheDocument();
  });

  it('historyAsksTheServerToFilterBeforeItLimits', async () => {
    body = response({
      summary: { errors: 0, warnings: 1, total: 1 },
      results: { matched: 1, returned: 1, truncated: false },
      entries: [entry({})],
    });
    renderSection();
    await screen.findByRole('status');

    fireEvent.click(screen.getByRole('button', { name: 'History' }));
    fireEvent.change(screen.getByLabelText('Severity'), { target: { value: 'Error' } });
    fireEvent.change(screen.getByLabelText('Search the log'), { target: { value: 'timeout' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));

    await waitFor(() => expect(requests.some((r) =>
      r.includes('minLevel=Error') && r.includes('q=timeout') && r.includes('take=500'))).toBe(true));
  });
});
