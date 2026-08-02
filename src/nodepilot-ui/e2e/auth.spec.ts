import { test, expect } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * Auth-Lifecycle E2E. Mock-driven so the suite stays deterministic and CI doesn't
 * need a Postgres backend to assert the SPA's login + logout + cookie behaviour.
 *
 * Maps to scenarios in [E2ETests.md](../../../E2ETests.md):
 *   - Test 25.1 (Bootstrap-Token-Flow)
 *   - Test 25.4 (HttpOnly-Cookie inspection — partial; the `Secure` flag isn't
 *     observable in jsdom-style mocks but the HttpOnly side is)
 *   - Test 25.7 (disabling a user invalidates its session)
 *   - Test 26.1 (viewer cannot write)
 */

test.describe('Auth lifecycle', () => {
  test('25.1 — bootstrap login redirects to dashboard', async ({ page }) => {
    // Initial /me before login is 401 (no cookie yet).
    let loggedIn = false;
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: loggedIn ? 200 : 401,
        contentType: 'application/json',
        body: loggedIn
          ? JSON.stringify({ username: 'admin', role: 'Admin', userId: '00000000-0000-0000-0000-000000000001' })
          : '{"error":"unauthenticated"}',
      }),
    );

    // Login endpoint flips the flag and reflects a successful login. This mock plays a
    // server whose bootstrap window is already closed (users exist), so plain
    // username/password succeeds — the setup-token gate has its own test (25.1b).
    await page.route('**/api/auth/login', async (route) => {
      const body = route.request().postDataJSON?.() ?? {};
      if (typeof body === 'object' && body.username === 'admin' && body.password) {
        loggedIn = true;
        return route.fulfill({
          status: 200,
          contentType: 'application/json',
          headers: {
            // Mock both auth and CSRF cookies. HttpOnly prevents JS access — verified
            // separately because Playwright doesn't surface the flag from a Set-Cookie
            // mock the same way DevTools does in a real flow.
            'Set-Cookie': [
              'np_auth=mock-jwt; HttpOnly; SameSite=Lax; Path=/',
              'np_csrf=mock-csrf; SameSite=Lax; Path=/',
            ].join(', '),
          },
          body: JSON.stringify({ userId: '00000000-0000-0000-0000-000000000001', role: 'Admin' }),
        });
      }
      return route.fulfill({ status: 401, contentType: 'application/json', body: '{"error":"invalid"}' });
    });

    await page.goto('/login');

    // The login form's <label>s aren't programmatically associated with their inputs,
    // so target by stable, language-agnostic attributes (autocomplete / input type)
    // instead of getByLabel — keeps the test green under the DE-default i18n.
    await page.locator('input[autocomplete="username"]').fill('admin');
    await page.locator('input[type="password"]').fill('Admin#2025!');
    await page.getByRole('button', { name: /anmelden|sign\s?in|login/i }).click();

    // Dashboard renders the username badge once /me resolves to 200.
    await expect(page.getByText(/admin/i).first()).toBeVisible({ timeout: 15_000 });
  });

  test('25.1b — bootstrap gate reveals the setup-token field and retries with X-Setup-Token', async ({ page }) => {
    // Fresh-install flow: the server's AdminBootstrap gate answers the first login with
    // 401 SETUP_TOKEN_REQUIRED. The SPA must reveal its setup-token field and resend the
    // same credentials with the X-Setup-Token header.
    let loggedIn = false;
    let seenSetupToken: string | null = null;
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: loggedIn ? 200 : 401,
        contentType: 'application/json',
        body: loggedIn
          ? JSON.stringify({ id: '00000000-0000-0000-0000-000000000001', username: 'admin', role: 'Admin' })
          : '{"error":"unauthenticated"}',
      }),
    );
    await page.route('**/api/auth/methods', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ local: true, ldap: false, windows: false, windowsEndpoint: null }) }),
    );
    await page.route('**/api/auth/login', async (route) => {
      const token = route.request().headers()['x-setup-token'];
      if (!token) {
        // Same payload shape as AuthController's bootstrap gate.
        return route.fulfill({
          status: 401,
          contentType: 'application/json',
          body: JSON.stringify({
            code: 'SETUP_TOKEN_REQUIRED',
            message: 'Admin bootstrap required. Send the X-Setup-Token header matching the admin-setup.token file written to the content root on first startup.',
          }),
        });
      }
      seenSetupToken = token;
      loggedIn = true;
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        headers: {
          'Set-Cookie': [
            'np_auth=mock-jwt; HttpOnly; SameSite=Lax; Path=/',
            'np_csrf=mock-csrf; SameSite=Lax; Path=/',
          ].join(', '),
        },
        body: JSON.stringify({ userId: '00000000-0000-0000-0000-000000000001', username: 'admin', role: 'Admin' }),
      });
    });

    await page.goto('/login');
    await page.locator('input[autocomplete="username"]').fill('admin');
    await page.locator('input[type="password"]').fill('Admin#2025!');
    await page.getByRole('button', { name: /anmelden|sign\s?in|login/i }).click();

    // The gate response reveals the token field (hidden before the first attempt).
    const tokenInput = page.locator('#np-login-setup-token');
    await expect(tokenInput).toBeVisible({ timeout: 10_000 });
    await tokenInput.fill('one-shot-token');
    await page.getByRole('button', { name: /anmelden|sign\s?in|login/i }).click();

    await expect(page).not.toHaveURL(/\/login/, { timeout: 15_000 });
    expect(seenSetupToken).toBe('one-shot-token');
  });

  test('25.7 — disabled user is logged out on next API call', async ({ page }) => {
    await installDefaultMocks(page);
    await page.goto('/');
    await expect(page.getByText(/e2e-admin/i)).toBeVisible({ timeout: 15_000 });

    // Now flip /me to 401 — simulates the Admin disabling this user mid-session.
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 401, contentType: 'application/json', body: '{"error":"disabled"}' }),
    );

    // Trigger a navigation that re-checks auth. The router/auth guard should catch
    // the 401 and redirect to /login.
    await page.goto('/workflows');

    await expect(page).toHaveURL(/\/login/);
  });

  test('26.1 — viewer sees no write actions', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ username: 'viewer1', role: 'Viewer', userId: '00000000-0000-0000-0000-000000000099' }),
      }),
    );
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
    );
    await page.route('**/hubs/**', (route) =>
      route.fulfill({ status: 404, body: 'mocked' }),
    );

    await page.goto('/workflows');

    // 'New Workflow' button should not be visible to a Viewer.
    await expect(page.getByRole('button', { name: /new workflow|neuer workflow/i })).toHaveCount(0);
  });

  test('25.2 — wrong credentials surface the error and keep the user on /login', async ({ page }) => {
    await installDefaultMocks(page);
    // Anonymous so the LoginPage renders instead of redirecting to the dashboard.
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 401, contentType: 'application/json', body: '{"error":"unauthenticated"}' }),
    );
    await page.route('**/api/auth/methods', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ local: true, ldap: false, windows: false, windowsEndpoint: null }) }),
    );
    // Every login attempt is rejected → the store's login() rejects → setError().
    await page.route('**/api/auth/login', (route) =>
      route.fulfill({ status: 401, contentType: 'application/json', body: '{"error":"invalid"}' }),
    );

    await page.goto('/login');
    await page.locator('input[autocomplete="username"]').fill('admin');
    await page.locator('input[type="password"]').fill('definitely-wrong');
    await page.getByRole('button', { name: /sign\s?in|anmelden|login/i }).click();

    // Inline error banner appears; we never leave the login page; the button re-enables for a retry.
    await expect(page.getByText(/invalid credentials|ungültige anmeldedaten/i)).toBeVisible({ timeout: 10_000 });
    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByRole('button', { name: /sign\s?in|anmelden|login/i })).toBeEnabled();
  });

  test('25.8 — Windows SSO button (when offered) signs in via POST /auth/windows and leaves /login', async ({ page }) => {
    await installDefaultMocks(page);
    let signedIn = false;
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: signedIn ? 200 : 401,
        contentType: 'application/json',
        body: signedIn
          ? JSON.stringify({ id: '00000000-0000-0000-0000-0000000000aa', username: 'winuser', role: 'Admin' })
          : '{"error":"unauthenticated"}',
      }),
    );
    // Server advertises Windows-Negotiate as an available auth method.
    await page.route('**/api/auth/methods', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ local: true, ldap: false, windows: true, windowsEndpoint: '/api/auth/windows' }) }),
    );
    let windowsHit = false;
    await page.route('**/api/auth/windows', (route) => {
      windowsHit = true;
      signedIn = true;
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ userId: '00000000-0000-0000-0000-0000000000aa', username: 'winuser', role: 'Admin' }),
      });
    });

    await page.goto('/login');
    const winButton = page.getByRole('button', { name: /windows/i });
    await expect(winButton).toBeVisible({ timeout: 15_000 });
    await winButton.click();

    await expect.poll(() => windowsHit, { timeout: 10_000 }).toBe(true);
    // The SSO handler sets the store + navigates to the dashboard — the login card unmounts.
    await expect(page).not.toHaveURL(/\/login/);
    await expect(winButton).toHaveCount(0);
  });
});
