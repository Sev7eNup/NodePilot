import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ScheduleTriggerConfig } from '../../../components/designer/properties/triggers/ScheduleTriggerConfig';
import { formatDate } from '../../../lib/format';

afterEach(() => vi.restoreAllMocks());

function show(cronExpression: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}>
    <ScheduleTriggerConfig config={{ cronExpression }} onUpdate={vi.fn()} />
  </QueryClientProvider>);
}

describe('ScheduleTriggerConfig authoritative preview', () => {
  it('uses the server instant for Quartz numeric weekdays', async () => {
    const fire = '2027-01-03T07:00:00Z';
    const fetch = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => Response.json({ fires: [fire] }));
    show('0 0 8 ? * 1');
    await waitFor(() => expect(fetch).toHaveBeenCalled());
    const url = new URL(String(fetch.mock.calls[0][0]), 'http://localhost');
    expect(url.pathname).toBe('/api/triggers/schedule/next-fires');
    expect(url.searchParams.get('cron')).toBe('0 0 8 ? * 1');
    expect(await screen.findByText(formatDate(new Date(fire), { hour12: false }))).toBeInTheDocument();
  });

  it('does not invent future fires after the server exhausts a year restriction', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async () => Response.json({ fires: [] }));
    show('0 0 8 ? * MON 2001');
    expect(await screen.findByText('(no upcoming fires)')).toBeInTheDocument();
  });

  it('shows server validation instead of accepting a short Unix expression', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(Response.json({ error: 'Invalid Quartz expression' }, { status: 400 }));
    show('0 2 * * *');
    expect(await screen.findByText(/Invalid Quartz expression/)).toBeInTheDocument();
  });
});
