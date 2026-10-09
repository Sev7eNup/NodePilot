import { afterEach, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useSchedulePreview } from '../../hooks/useSchedulePreview';

afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); });

function wrapper() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return ({ children }: { children: React.ReactNode }) =>
    <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

it('shares one server request between the node and property panel', async () => {
  const fire = '2027-01-03T07:00:00Z';
  const fetch = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => Response.json({ fires: [fire] }));
  const { result } = renderHook(() => [useSchedulePreview('0 0 8 ? * 1'), useSchedulePreview('0 0 8 ? * 1')], { wrapper: wrapper() });
  await waitFor(() => expect(result.current.every(preview => preview.fireTimes.length === 1)).toBe(true));
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(result.current[0].fireTimes[0].toISOString()).toBe(new Date(fire).toISOString());
});

it('hides the old schedule immediately and requests only the settled edit', async () => {
  vi.useFakeTimers();
  const fetch = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => Response.json({ fires: ['2027-01-03T07:00:00Z'] }));
  const { result, rerender } = renderHook(({ cron }) => useSchedulePreview(cron), {
    initialProps: { cron: '0 0 8 ? * 1' }, wrapper: wrapper(),
  });
  await act(async () => { await vi.advanceTimersByTimeAsync(10); });
  expect(result.current.fireTimes).toHaveLength(1);
  rerender({ cron: '0 0 9 ? * 1' });
  expect(result.current.fireTimes).toHaveLength(0);
  expect(result.current.isLoading).toBe(true);
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
  rerender({ cron: '0 0 10 ? * 1' });
  await act(async () => { await vi.advanceTimersByTimeAsync(249); });
  expect(fetch).toHaveBeenCalledTimes(1);
  await act(async () => { await vi.advanceTimersByTimeAsync(11); });
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(new URL(String(fetch.mock.calls[1][0]), 'http://localhost').searchParams.get('cron')).toBe('0 0 10 ? * 1');
  await act(async () => { await vi.advanceTimersByTimeAsync(10); });
  expect(result.current.fireTimes).toHaveLength(1);
});
