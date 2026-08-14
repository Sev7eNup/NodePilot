import { describe, it, expect, beforeEach, vi } from 'vitest';
import * as React from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router';
import { useAuthStore } from '../../stores/authStore';
import { ProtectedRoute } from '../../components/ProtectedRoute';

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
});
