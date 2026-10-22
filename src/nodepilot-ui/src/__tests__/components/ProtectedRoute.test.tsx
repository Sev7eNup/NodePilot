import { describe, it, expect, beforeEach, vi } from 'vitest';
import * as React from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router';
import { startAuthBoundarySynchronization, useAuthStore } from '../../stores/authStore';
import { ProtectedRoute } from '../../components/ProtectedRoute';
import { api } from '../../api/client';
import { AUTH_BOUNDARY_STORAGE_KEY, type AuthBoundaryEvent } from '../../security/authBoundary';

describe('ProtectedRoute', () => {
  beforeEach(() => {
    useAuthStore.setState({
      userId: null,
      username: null,
      role: null,
      isAuthenticated: false,
    });
    sessionStorage.clear();
  });

  it('unauthenticated redirects to login', () => {
    render(
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route path="/login" element={<div>Login Page</div>} />
          <Route
            path="/"
            element={
              <ProtectedRoute>
                <div>Dashboard</div>
              </ProtectedRoute>
            }
          />
        </Routes>
      </MemoryRouter>
    );

    expect(screen.getByText('Login Page')).toBeInTheDocument();
    expect(screen.queryByText('Dashboard')).not.toBeInTheDocument();
  });

  it('authenticated renders children', () => {
    useAuthStore.setState({ isAuthenticated: true, username: 'admin', role: 'Admin' });

    render(
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route path="/login" element={<div>Login Page</div>} />
          <Route
            path="/"
            element={
              <ProtectedRoute>
                <div>Dashboard</div>
              </ProtectedRoute>
            }
          />
        </Routes>
      </MemoryRouter>
    );

    expect(screen.getByText('Dashboard')).toBeInTheDocument();
    expect(screen.queryByText('Login Page')).not.toBeInTheDocument();
  });

  it('localIdentitySwitch_remountsProtectedComponentStateEvenWhenReactBatchesTheTransition', () => {
    const unmounted = vi.fn();
    function StatefulProtectedChild() {
      const [draft, setDraft] = React.useState('');
      React.useEffect(() => () => unmounted(), []);
      return <input aria-label="private draft" value={draft} onChange={(event) => setDraft(event.target.value)} />;
    }
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-a',
      username: 'alice',
      role: 'Admin',
    });
    render(
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route path="/login" element={<div>Login Page</div>} />
          <Route path="/" element={(
            <ProtectedRoute>
              <StatefulProtectedChild />
            </ProtectedRoute>
          )} />
        </Routes>
      </MemoryRouter>,
    );
    fireEvent.change(screen.getByRole('textbox', { name: 'private draft' }), {
      target: { value: 'User A local state' },
    });

    act(() => {
      useAuthStore.getState().acceptAuthenticatedIdentity({
        userId: 'u-b',
        username: 'bob',
        role: 'Operator',
      });
    });

    expect(unmounted).toHaveBeenCalledOnce();
    expect(screen.getByRole('textbox', { name: 'private draft' })).toHaveValue('');
  });

  it('observingTheSameSessionInAnotherTabPreservesProtectedDrafts', async () => {
    function Draft() {
      const [value, setValue] = React.useState('');
      return <input aria-label="private draft" value={value} onChange={(event) => setValue(event.target.value)} />;
    }
    vi.stubGlobal('BroadcastChannel', undefined);
    const stop = startAuthBoundarySynchronization();
    const get = vi.spyOn(api, 'get').mockResolvedValue({ id: 'u-a', username: 'alice', role: 'Admin' });
    const storageWrite = vi.spyOn(Storage.prototype, 'setItem');
    try {
      useAuthStore.getState().acceptAuthenticatedIdentity({
        userId: 'u-a', username: 'alice', role: 'Admin',
      }, { broadcastIdentity: false });
      render(<MemoryRouter><ProtectedRoute><Draft /></ProtectedRoute></MemoryRouter>);
      fireEvent.change(screen.getByRole('textbox', { name: 'private draft' }), {
        target: { value: 'Unsaved work in the first tab' },
      });
      storageWrite.mockClear();

      // Deliver the real initialization message as if a second tab had sent it.
      await act(() => useAuthStore.getState().initialize());
      const messages = storageWrite.mock.calls
        .filter(([key]) => key === AUTH_BOUNDARY_STORAGE_KEY)
        .map(([, value]) => JSON.parse(value) as AuthBoundaryEvent);
      act(() => {
        for (const event of messages) {
          window.dispatchEvent(new StorageEvent('storage', {
            key: AUTH_BOUNDARY_STORAGE_KEY,
            newValue: JSON.stringify({ ...event, sourceId: 'second-tab', eventId: 'same-session-probe' }),
          }));
        }
      });

      expect(screen.getByRole('textbox', { name: 'private draft' })).toHaveValue('Unsaved work in the first tab');
      expect(get).toHaveBeenCalledTimes(1);
    } finally {
      stop();
      vi.restoreAllMocks();
      vi.unstubAllGlobals();
    }
  });
});
