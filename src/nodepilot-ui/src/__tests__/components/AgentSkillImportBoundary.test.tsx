import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, expect, it, vi } from 'vitest';
import { AgentsSection } from '../../components/admin-settings/AgentsSection';
import { api } from '../../api/client';
import { useAuthStore } from '../../stores/authStore';
import { clearLocalAuthBoundary } from '../../security/authBoundary';

vi.mock('../../components/admin-settings/SectionFormHelpers', () => ({
  useSectionForm: () => ({ loading: true }), ErrorsAndSave: () => null,
}));
afterEach(() => vi.restoreAllMocks());

it.each([false, true])('imports only within the initiating identity (switch=%s)', async (switchIdentity) => {
  useAuthStore.setState({ isAuthenticated: true, userId: 'admin-a', username: 'a', role: 'Admin' });
  vi.spyOn(api, 'get').mockResolvedValue([]);
  const post = vi.spyOn(api, 'post').mockResolvedValue({});
  let finishRead!: (data: ArrayBuffer) => void;
  const reading = new Promise<ArrayBuffer>(resolve => { finishRead = resolve; });
  const file = new File(['package'], 'skill.zip', { type: 'application/zip' });
  const read = vi.fn(() => reading);
  Object.defineProperty(file, 'arrayBuffer', { value: read });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const view = render(<QueryClientProvider client={client}><AgentsSection /></QueryClientProvider>);
  fireEvent.click(screen.getByRole('button', { name: /import skill/i }));
  fireEvent.change(view.container.querySelector('input[type="file"]')!, { target: { files: [file] } });
  fireEvent.submit(screen.getByRole('form', { name: /import skill/i }));
  await waitFor(() => expect(read).toHaveBeenCalled());
  if (switchIdentity) {
    act(() => clearLocalAuthBoundary());
    view.unmount();
    useAuthStore.setState({ isAuthenticated: true, userId: 'admin-b', username: 'b', role: 'Admin' });
  }
  await act(async () => { finishRead(new Uint8Array([1, 2, 3]).buffer); await reading; });
  await waitFor(() => expect(client.isMutating()).toBe(0));
  if (switchIdentity) expect(post).not.toHaveBeenCalled();
  else expect(post).toHaveBeenCalledWith('/agents/skills', { version: '1.0.0', package: 'AQID' });
});
