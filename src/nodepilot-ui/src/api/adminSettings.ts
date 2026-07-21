// Typed client for the Admin Settings API. Handles ETag round-tripping (read returns
// the ETag in the body so the next PUT can echo it in If-Match) and the 412/428 paths
// distinct from the generic api.* client — those status codes are part of the contract
// here rather than "unexpected error".

const BASE_URL = '/api';

const MUTATING_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

function readCsrfToken(): string {
  if (typeof document === 'undefined') return '';
  const match = /(?:^|;\s*)np_csrf=([^;]+)/.exec(document.cookie);
  return match ? decodeURIComponent(match[1]) : '';
}

async function adminFetch(path: string, options?: RequestInit): Promise<Response> {
  const method = (options?.method ?? 'GET').toUpperCase();
  const headers: Record<string, string> = {
    ...(options?.body !== undefined ? { 'Content-Type': 'application/json' } : {}),
  };
  if (MUTATING_METHODS.has(method)) {
    const csrf = readCsrfToken();
    if (csrf) headers['X-CSRF-Token'] = csrf;
  }
  const response = await fetch(`${BASE_URL}${path}`, {
    ...options,
    credentials: 'include',
    headers: { ...headers, ...options?.headers },
  });
  if (response.status === 401) {
    if (typeof window !== 'undefined' && !globalThis.location.pathname.startsWith('/login')) {
      globalThis.location.href = '/login';
    }
    throw new SettingsApiError('Unauthorized', 401, null);
  }
  return response;
}

export type SettingsStatus = {
  overridesPath: string;
  restartRequired: boolean;
  restartRequiredSince: string | null;
  restartRequiredFor: string[];
  lastSavedAt: string | null;
  lastSavedBy: string | null;
};

export type SettingsSectionResponse<TPayload> = {
  sectionPath: string;
  payload: TPayload;
  etag: string;
  isHotReloadable: boolean;
  effectiveSource: Record<string, string>;
};

export type SettingsValidationError = {
  validatorName?: string;
  configKey?: string;
  fields?: string[];
  message: string;
};

export type SettingsValidationErrorResponse = {
  code: string;
  message?: string;
  errors?: SettingsValidationError[];
};

export type SettingsTestProbeResult = {
  ok: boolean;
  message: string;
  durationMs: number;
  errorKind: string | null;
};

/**
 * Thrown for non-2xx responses from the admin settings API. Carries enough metadata for
 * the UI to disambiguate between the documented status codes:
 *  - 400 → validation problem; `body.errors` populated
 *  - 404 → unknown section
 *  - 412 → ETag mismatch; `body.current` carries the latest server snapshot
 *  - 428 → missing If-Match header; client bug
 */
export class SettingsApiError extends Error {
  public readonly status: number;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  public readonly body: any;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  constructor(message: string, status: number, body: any) {
    super(message);
    this.name = 'SettingsApiError';
    this.status = status;
    this.body = body;
  }
}

async function readJson<T>(response: Response): Promise<T> {
  if (response.status === 204) return undefined as unknown as T;
  return (await response.json()) as T;
}

async function expectOk<T>(response: Response): Promise<T> {
  if (response.ok) return readJson<T>(response);
  let body: unknown = null;
  try { body = await response.json(); }
  catch { /* response had no JSON body */ }
  throw new SettingsApiError(
    `Admin Settings API returned ${response.status}`,
    response.status,
    body,
  );
}

export const adminSettings = {
  async getStatus(): Promise<SettingsStatus> {
    return expectOk(await adminFetch('/admin/settings/status'));
  },

  async getSnapshot(): Promise<SettingsSectionResponse<unknown>[]> {
    return expectOk(await adminFetch('/admin/settings'));
  },

  async getSection<TPayload>(section: string): Promise<SettingsSectionResponse<TPayload>> {
    return expectOk(await adminFetch(`/admin/settings/${encodeURIComponent(section)}`));
  },

  // Write-payload is intentionally `unknown`-shaped: ASP.NET Core deserialises with
  // PropertyNameCaseInsensitive, so a UI-side camelCase mismatch wouldn't fail the
  // server, but the strict TPayload constraint would block our PascalCase write
  // shape that matches the C# DTO 1:1. The Promise's TResponse still gives the
  // caller a typed Read-shape on success.
  async putSection<TResponse>(
    section: string,
    payload: unknown,
    etag: string,
  ): Promise<SettingsSectionResponse<TResponse>> {
    return expectOk(await adminFetch(
      `/admin/settings/${encodeURIComponent(section)}`,
      {
        method: 'PUT',
        body: JSON.stringify(payload),
        headers: { 'If-Match': etag },
      },
    ));
  },

  async testSmtp(payload: unknown): Promise<SettingsTestProbeResult> {
    return expectOk(await adminFetch('/admin/settings/test/smtp', {
      method: 'POST',
      body: JSON.stringify(payload),
    }));
  },

  async testLlm(payload: unknown): Promise<SettingsTestProbeResult> {
    return expectOk(await adminFetch('/admin/settings/test/llm', {
      method: 'POST',
      body: JSON.stringify(payload),
    }));
  },

  async testLdap(payload: unknown): Promise<SettingsTestProbeResult> {
    return expectOk(await adminFetch('/admin/settings/test/ldap', {
      method: 'POST',
      body: JSON.stringify(payload),
    }));
  },

  async getSystemInfo(): Promise<SystemInfo> {
    return expectOk(await adminFetch('/admin/settings/system-info'));
  },
};

export type SystemInfo = {
  appVersion: string;
  overridesPath: string;
  databaseProvider: string;
  databaseHost: string | null;
  secretsProvider: string;
  clusterEnabled: boolean;
  clusterNodeId: string;
  clusterIsLeader: boolean;
  jwtIssuer: string;
  jwtAudience: string;
};
