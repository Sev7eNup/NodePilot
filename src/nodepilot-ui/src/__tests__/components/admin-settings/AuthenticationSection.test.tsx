import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import { AuthenticationSection } from '../../../components/admin-settings/AuthenticationSection';

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

const snapshot = {
  sectionPath: 'Authentication',
  payload: {
    ldap: {
      enabled: true, server: 'dc01.firma.local', port: 636, useSsl: true,
      endpoints: ['dc01.firma.local', 'dc02.firma.local'],
      baseDn: 'DC=firma,DC=local', upnSuffix: 'firma.local', bindTimeoutSeconds: 5,
      serviceBindDn: 'CN=svc-ldap,OU=Services,DC=firma,DC=local',
      servicePassword: '********',
      allowedGroupSids: ['S-1-5-21-1-2-3-1001'],
      directorySyncIntervalMinutes: 5,
      globalRoleMappings: [{ groupSid: 'S-1-5-21-1-2-3-4567', role: 'Admin' }],
      jitUserDefaultRootRole: 'FolderViewer',
    },
    windows: { enabled: true, allowNtlmFallback: false, ntlmDisabledByPolicy: true },
    oidc: {
      enabled: true, authority: 'https://login.example.test/tenant', clientId: 'nodepilot',
      clientSecret: '********', displayName: 'Example ID', nameClaimType: 'preferred_username',
      groupsClaimType: 'groups', scopes: ['openid', 'profile'],
      allowedGroupIds: ['group-access'],
      globalRoleMappings: [{ groupId: 'group-admins', role: 'Admin' }],
    },
    scim: {
      enabled: true,
      bearerToken: '********',
      previousBearerToken: '********',
      authority: 'https://login.example.test/tenant',
    },
    localLoginMode: 'BreakGlassOnly',
    sessionAbsoluteLifetimeHours: 8,
    maxAuthorizationStalenessMinutes: 15,
  },
  etag: '"auth-1"', isHotReloadable: false, effectiveSource: {},
};

function renderSection() {
  server.use(http.get('/api/admin/settings/Authentication', () => HttpResponse.json(snapshot)));
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><AuthenticationSection /></QueryClientProvider>);
}

describe('AuthenticationSection', () => {
  it('renders LDAP + Windows cards with persisted values', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('dc01.firma.local')).toBeInTheDocument());
    expect(screen.getByDisplayValue('DC=firma,DC=local')).toBeInTheDocument();
    expect(screen.getByDisplayValue('firma.local')).toBeInTheDocument();
    expect(screen.getByDisplayValue('dc02.firma.local')).toBeInTheDocument();
    expect(screen.getAllByDisplayValue('********')).toHaveLength(4);
    expect(screen.getAllByDisplayValue('https://login.example.test/tenant')).toHaveLength(2);
    expect(screen.getByDisplayValue('Example ID')).toBeInTheDocument();
    expect(screen.getByDisplayValue(/Break-glass accounts only/i)).toBeInTheDocument();
  });

  it('renders existing role-mappings and supports adding a new row', async () => {
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('S-1-5-21-1-2-3-4567')).toBeInTheDocument());
    fireEvent.click(screen.getAllByRole('button', { name: /add mapping/i })[0]);
    // Two rows now — the original SID plus a blank new one.
    const sidInputs = screen.getAllByPlaceholderText('S-1-5-21-...');
    expect(sidInputs.length).toBe(3);
  });

  it('Save sends PascalCase payload with __unchanged__ for the kept secret', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/Authentication', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...snapshot, etag: '"auth-2"' });
    }));
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('dc01.firma.local')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /speichern|save/i }));

    await waitFor(() => {
       
      const body = putBody as any;
      expect(body?.Ldap?.Server).toBe('dc01.firma.local');
      expect(body?.Ldap?.Endpoints).toEqual(['dc01.firma.local', 'dc02.firma.local']);
      expect(body?.Ldap?.ServicePassword).toBe('__unchanged__');
      expect(body?.Ldap?.AllowedGroupSids).toEqual(['S-1-5-21-1-2-3-1001']);
      expect(body?.Ldap?.DirectorySyncIntervalMinutes).toBe(5);
      expect(body?.Ldap?.DirectorySyncMaxConcurrency).toBe(16);
      expect(body?.MaxAuthorizationStalenessMinutes).toBe(15);
      expect(body?.Windows?.Enabled).toBe(true);
      expect(body?.Windows?.AllowNtlmFallback).toBe(false);
      expect(body?.Windows?.NtlmDisabledByPolicy).toBe(true);
      expect(body?.Oidc?.Enabled).toBe(true);
      expect(body?.Oidc?.Authority).toBe('https://login.example.test/tenant');
      expect(body?.Oidc?.ClientSecret).toBe('__unchanged__');
      expect(body?.Oidc?.AllowedGroupIds).toEqual(['group-access']);
      expect(body?.Oidc?.GlobalRoleMappings?.[0]).toEqual({ GroupId: 'group-admins', Role: 'Admin' });
      expect(body?.Scim?.Enabled).toBe(true);
      expect(body?.Scim?.BearerToken).toBe('__unchanged__');
      expect(body?.Scim?.PreviousBearerToken).toBe('__unchanged__');
      expect(body?.LocalLoginMode).toBe('BreakGlassOnly');
      expect(body?.SessionAbsoluteLifetimeHours).toBe(8);
      expect(body?.Ldap?.GlobalRoleMappings?.[0]?.GroupSid).toBe('S-1-5-21-1-2-3-4567');
    });
  });

  it('runs the LDAP readiness probe with the current draft', async () => {
    let probeBody: unknown = null;
    server.use(http.post('/api/admin/settings/test/ldap', async ({ request }) => {
      probeBody = await request.json();
      return HttpResponse.json({ ok: true, message: 'LDAP ready', durationMs: 18, errorKind: null });
    }));
    renderSection();
    await waitFor(() => expect(screen.getByDisplayValue('dc01.firma.local')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /test/i }));
    const runButtons = await screen.findAllByRole('button', { name: /run test|test ausf/i });
    fireEvent.click(runButtons.at(-1)!);

    await waitFor(() => expect(screen.getByText('LDAP ready')).toBeInTheDocument());
    const body = probeBody as any;
    expect(body?.Settings?.Endpoints).toEqual(['dc01.firma.local', 'dc02.firma.local']);
    expect(body?.Settings?.UseSsl).toBe(true);
  });
});
