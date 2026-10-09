import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { onlineManager, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactElement, ReactNode, ComponentProps } from 'react';
import { adminSettings, SettingsApiError, type SettingsSectionResponse } from '../../../api/adminSettings';
import { useSectionForm } from '../../../components/admin-settings/SectionFormHelpers';
import { EtagConflictDialog } from '../../../components/admin-settings/EtagConflictDialog';

const snapshot = (value: string, etag: string): SettingsSectionResponse<{ value: string }> => ({
  sectionPath: 'Test', payload: { value }, etag, effectiveSource: {}, isHotReloadable: false,
});

afterEach(() => { onlineManager.setOnline(true); vi.restoreAllMocks(); });

describe('settings draft ownership', () => {
  it('retries conflicts through the same guarded save pipeline without adopting a rejected snapshot', async () => {
    vi.spyOn(adminSettings, 'getSection').mockResolvedValue(snapshot('initial', 'v1'));
    let rejectRetry!: (reason: unknown) => void;
    const put = vi.spyOn(adminSettings, 'putSection')
      .mockRejectedValueOnce(new SettingsApiError('conflict', 412, { code: 'conflict', current: snapshot('other', 'v2') }))
      .mockImplementationOnce(() => new Promise((_, reject) => { rejectRetry = reject; }))
      .mockResolvedValueOnce(snapshot('my draft', 'v4'));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    const hook = renderHook(() => useSectionForm('Test', { value: '' }), {
      wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
    });
    const dialog = () => (hook.result.current.dialog as ReactElement<ComponentProps<typeof EtagConflictDialog>>).props;
    await waitFor(() => expect(hook.result.current.form?.value).toBe('initial'));
    act(() => hook.result.current.set!({ value: 'my draft' }));
    act(() => { hook.result.current.save!({ Value: 'my draft' }); hook.result.current.save!({ Value: 'duplicate' }); });
    await waitFor(() => expect(dialog().open).toBe(true));
    expect(put).toHaveBeenCalledTimes(1);
    act(() => { dialog().onKeepMine(); dialog().onKeepMine(); });
    await waitFor(() => expect(put).toHaveBeenCalledTimes(2));
    expect(hook.result.current.data?.etag).toBe('v1');
    expect(hook.result.current.form?.value).toBe('my draft');
    act(() => rejectRetry(new SettingsApiError('conflict', 412, { code: 'conflict', current: snapshot('third', 'v3') })));
    await waitFor(() => expect(dialog().serverSnapshot?.etag).toBe('v3'));
    act(() => dialog().onKeepMine());
    await waitFor(() => expect(hook.result.current.data?.etag).toBe('v4'));
    expect(put.mock.calls.map((call) => call.slice(1))).toEqual([
      [{ Value: 'my draft' }, 'v1'], [{ Value: 'my draft' }, 'v2'], [{ Value: 'my draft' }, 'v3'],
    ]);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['admin-settings', 'status'] });
    hook.unmount(); client.clear();
  });

  it('keeps an unsaved draft and its original ETag after an actual reconnect refetch', async () => {
    const initial = snapshot('initial', 'v1');
    const remote = snapshot('someone else', 'v2');
    const get = vi.spyOn(adminSettings, 'getSection').mockResolvedValue(remote);
    const put = vi.spyOn(adminSettings, 'putSection').mockRejectedValue(
      new SettingsApiError('conflict', 412, { code: 'conflict', current: remote }));
    // Match production focus/staleness defaults. The initial snapshot is old enough to refetch.
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 10_000, refetchOnWindowFocus: false } } });
    client.setQueryData(['admin-settings', 'Test'], initial, { updatedAt: Date.now() - 20_000 });
    onlineManager.setOnline(false);
    const hook = renderHook(() => useSectionForm('Test', { value: '' }), {
      wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
    });
    await waitFor(() => expect(hook.result.current.form?.value).toBe('initial'));
    act(() => hook.result.current.set!({ value: 'my draft' }));
    act(() => onlineManager.setOnline(true));
    await waitFor(() => expect(get).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(client.getQueryData(['admin-settings', 'Test'])).toEqual(remote));
    expect(hook.result.current.form?.value).toBe('my draft');
    act(() => hook.result.current.save!({ Value: 'my draft' }));
    await waitFor(() => expect(put).toHaveBeenCalledWith('Test', { Value: 'my draft' }, 'v1'));
    hook.unmount(); client.clear();
  });
});
