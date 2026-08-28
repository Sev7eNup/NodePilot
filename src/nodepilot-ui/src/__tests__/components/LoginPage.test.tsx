import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { BrowserRouter } from 'react-router';
import { LoginPage } from '../../pages/LoginPage';
import { useAuthStore } from '../../stores/authStore';
import { aiChatScopeKey, useAiChatStore } from '../../stores/aiChatStore';
import { api, ApiError } from '../../api/client';
import { queryClient } from '../../queryClient';
import {
  DB_ADMIN_QUERY_DRAFT_KEY,
  DB_ADMIN_QUERY_HISTORY_KEY,
} from '../../security/sensitiveBrowserState';
import { clearLocalAuthBoundary } from '../../security/authBoundary';

function renderLoginPage() {
  return render(
    <BrowserRouter>
      <LoginPage />
    </BrowserRouter>
  );
}

describe('LoginPage', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    queryClient.clear();
    useAiChatStore.setState({ messagesByThread: {}, threadsByScope: {}, activeThreadByScope: {} });
    useAuthStore.setState({
      userId: null,
      username: null,
      role: null,
      isAuthenticated: false,
    });
    // Default: the methods endpoint reports local-only, which is what the form-only tests need.
    // The Windows tests below override this with their own mock.
    vi.spyOn(api, 'get').mockResolvedValue({
      local: true,
      ldap: false,
      windows: false,
      windowsEndpoint: null,
    });
  });

  it('renders form elements', () => {
    renderLoginPage();

    expect(screen.getByText('NodePilot')).toBeInTheDocument();
    expect(screen.getByText('Username')).toBeInTheDocument();
    expect(screen.getByText('Password')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /sign in/i })).toBeInTheDocument();
  });

  it('submit calls login with credentials', async () => {
    const user = userEvent.setup();
    const loginMock = vi.fn().mockResolvedValue(undefined);
    useAuthStore.setState({ login: loginMock });

    renderLoginPage();

    // Use placeholder or role-based queries since labels don't use htmlFor
    const inputs = screen.getAllByRole('textbox');
    const passwordInput = document.querySelector('input[type="password"]') as HTMLElement;

    await user.type(inputs[0], 'admin');
    await user.type(passwordInput, 'secret123');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(loginMock).toHaveBeenCalledWith('admin', 'secret123');
  });

  it('displays error message on login failure', async () => {
    const user = userEvent.setup();
    const loginMock = vi.fn().mockRejectedValue(new Error('Invalid credentials'));
    useAuthStore.setState({ login: loginMock });

    renderLoginPage();

    const inputs = screen.getAllByRole('textbox');
    const passwordInput = document.querySelector('input[type="password"]') as HTMLElement;

    await user.type(inputs[0], 'admin');
    await user.type(passwordInput, 'wrong');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByText('Invalid credentials')).toBeInTheDocument();
  });

  it('reveals the setup-token field on SETUP_TOKEN_REQUIRED and retries with the token', async () => {
    const user = userEvent.setup();
    const loginMock = vi
      .fn()
      // The page branches on ApiError.code, not on the message text, so reject with the shape
      // the real api client throws.
      .mockRejectedValueOnce(new ApiError('Admin bootstrap required. (SETUP_TOKEN_REQUIRED)', 400, 'SETUP_TOKEN_REQUIRED'))
      .mockResolvedValueOnce(undefined);
    useAuthStore.setState({ login: loginMock });

    renderLoginPage();

    const inputs = screen.getAllByRole('textbox');
    const passwordInput = document.querySelector('input[type="password"]') as HTMLElement;
    await user.type(inputs[0], 'admin');
    await user.type(passwordInput, 'secret123');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    // The bootstrap gate reveals the token field together with its explanatory error.
    expect(await screen.findByText(/first-time setup/i)).toBeInTheDocument();
    const tokenInput = document.getElementById('np-login-setup-token') as HTMLElement;
    expect(tokenInput).toBeInTheDocument();

    await user.type(tokenInput, 'one-shot-token');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(loginMock).toHaveBeenLastCalledWith('admin', 'secret123', 'one-shot-token');
  });

  it('keeps the setup-token field hidden for a plain wrong-password 401', async () => {
    const user = userEvent.setup();
    const loginMock = vi.fn().mockRejectedValue(new Error('Invalid credentials'));
    useAuthStore.setState({ login: loginMock });

    renderLoginPage();

    const inputs = screen.getAllByRole('textbox');
    const passwordInput = document.querySelector('input[type="password"]') as HTMLElement;
    await user.type(inputs[0], 'admin');
    await user.type(passwordInput, 'wrong');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByText('Invalid credentials')).toBeInTheDocument();
    expect(document.getElementById('np-login-setup-token')).not.toBeInTheDocument();
  });

  it('hides the Windows SSO button when the server reports windows: false', async () => {
    renderLoginPage();
    // Wait one tick for the methods fetch to resolve.
    await new Promise((r) => setTimeout(r, 0));
    expect(screen.queryByRole('button', { name: /windows account/i })).not.toBeInTheDocument();
  });

  it('shows the Windows SSO button when the server reports windows: true', async () => {
    vi.spyOn(api, 'get').mockResolvedValue({
      local: true,
      ldap: false,
      windows: true,
      windowsEndpoint: '/api/auth/windows',
    });
    renderLoginPage();
    expect(await screen.findByRole('button', { name: /windows account/i })).toBeInTheDocument();
  });

  it('clicking the Windows SSO button POSTs to /auth/windows and signs the user in', async () => {
    const user = userEvent.setup();
    vi.spyOn(api, 'get').mockResolvedValue({
      local: true,
      ldap: false,
      windows: true,
      windowsEndpoint: '/api/auth/windows',
    });
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-a',
      username: 'FIRMA\\\\alice',
      role: 'Admin',
    });
    const scope = aiChatScopeKey('u-a', 'wf-1');
    const threadId = useAiChatStore.getState().newThread(scope, 'User A');
    useAiChatStore.getState().updateMessages(scope, threadId, () => [
      { role: 'user', content: 'user-a secret' },
    ]);
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT user_a_secret');
    sessionStorage.setItem(DB_ADMIN_QUERY_HISTORY_KEY, '["SELECT user_a_secret"]');
    queryClient.setQueryData(['user-a-result'], { secret: true });
    const postSpy = vi.spyOn(api, 'post').mockResolvedValue({
      userId: 'u-b',
      username: 'FIRMA\\\\bob',
      role: 'Operator',
    });

    renderLoginPage();
    const ssoButton = await screen.findByRole('button', { name: /windows account/i });
    await user.click(ssoButton);

    expect(postSpy).toHaveBeenCalledWith('/auth/windows');
    expect(useAuthStore.getState()).toMatchObject({
      userId: 'u-b',
      username: 'FIRMA\\\\bob',
      role: 'Operator',
      isAuthenticated: true,
    });
    expect(useAiChatStore.getState().messagesByThread).toEqual({});
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_HISTORY_KEY)).toBeNull();
    expect(queryClient.getQueryData(['user-a-result'])).toBeUndefined();
  });

  it('staleWindowsSsoResponse_cannotOverwriteANewerAuthBoundary', async () => {
    const user = userEvent.setup();
    vi.spyOn(api, 'get').mockResolvedValue({
      local: true,
      ldap: false,
      windows: true,
      windowsEndpoint: '/api/auth/windows',
    });
    let resolveWindows!: (identity: { userId: string; username: string; role: string }) => void;
    const staleWindowsResponse = new Promise<{ userId: string; username: string; role: string }>((resolve) => {
      resolveWindows = resolve;
    });
    vi.spyOn(api, 'post').mockReturnValueOnce(staleWindowsResponse);

    renderLoginPage();
    await user.click(await screen.findByRole('button', { name: /windows account/i }));
    expect(api.post).toHaveBeenCalledWith('/auth/windows');

    clearLocalAuthBoundary();
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-b',
      username: 'FIRMA\\\\bob',
      role: 'Operator',
    });
    await act(async () => {
      resolveWindows({ userId: 'u-a', username: 'FIRMA\\\\alice', role: 'Admin' });
      await staleWindowsResponse;
      await Promise.resolve();
    });

    expect(useAuthStore.getState()).toMatchObject({
      userId: 'u-b',
      username: 'FIRMA\\\\bob',
      role: 'Operator',
      isAuthenticated: true,
    });
  });

  it('renders OIDC as a top-level browser navigation and can hide password login', async () => {
    vi.spyOn(api, 'get').mockResolvedValue({
      local: false,
      ldap: false,
      windows: false,
      windowsEndpoint: null,
      oidc: true,
      oidcEndpoint: '/api/auth/oidc',
      oidcDisplayName: 'Contoso ID',
    });

    renderLoginPage();

    const oidcLink = await screen.findByRole('link', { name: /contoso id/i });
    expect(oidcLink).toHaveAttribute('href', '/api/auth/oidc');
    expect(screen.queryByRole('button', { name: /^sign in$/i })).not.toBeInTheDocument();
    expect(document.querySelector('input[type="password"]')).not.toBeInTheDocument();
  });

  it('keeps password login available when LDAP is enabled but local login is disabled', async () => {
    vi.spyOn(api, 'get').mockResolvedValue({
      local: false,
      ldap: true,
      windows: false,
      windowsEndpoint: null,
      oidc: false,
    });

    renderLoginPage();

    expect(await screen.findByRole('button', { name: /^sign in$/i })).toBeInTheDocument();
  });
});
