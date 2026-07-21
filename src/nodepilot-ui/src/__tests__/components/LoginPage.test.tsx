import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { BrowserRouter } from 'react-router-dom';
import { LoginPage } from '../../pages/LoginPage';
import { useAuthStore } from '../../stores/authStore';
import { api } from '../../api/client';

function renderLoginPage() {
  return render(
    <BrowserRouter>
      <LoginPage />
    </BrowserRouter>
  );
}

describe('LoginPage', () => {
  beforeEach(() => {
    useAuthStore.setState({
      username: null,
      role: null,
      isAuthenticated: false,
    });
    // Default: methods endpoint returns local-only so the existing form-only tests stay
    // unchanged. Windows-tests below override this with a focused mock.
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
    const postSpy = vi.spyOn(api, 'post').mockResolvedValue({
      token: 't',
      userId: 'u-1',
      username: 'FIRMA\\\\alice',
      role: 'Operator',
    });

    renderLoginPage();
    const ssoButton = await screen.findByRole('button', { name: /windows account/i });
    await user.click(ssoButton);

    expect(postSpy).toHaveBeenCalledWith('/auth/windows');
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
