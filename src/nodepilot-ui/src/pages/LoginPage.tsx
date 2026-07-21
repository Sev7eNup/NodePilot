import {
  ArrowRight,
  Certificate,
  CircleDash,
  FingerprintRecognition,
  Locked,
  User,
  WarningFilled,
} from '@carbon/icons-react';
import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '../stores/authStore';
import { api } from '../api/client';
import { BrandLogo } from '../components/BrandLogo';
import type { AuthMethodsResponse, LoginResponse } from '../types/api';

const inputClass =
  'w-full pl-10 pr-3 py-2.5 bg-surface-low/60 border border-outline-variant rounded-xl text-sm text-on-surface ' +
  'placeholder:text-outline/50 focus:outline-none focus:ring-2 focus:ring-primary/45 focus:border-primary transition-shadow';

export function LoginPage() {
  const { t } = useTranslation(['auth']);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [methods, setMethods] = useState<AuthMethodsResponse | null>(null);
  const login = useAuthStore((s) => s.login);
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();

  useEffect(() => {
    const oidcError = searchParams.get('oidcError');
    if (oidcError === 'access_not_assigned') setError(t('auth:oidcAccessNotAssigned'));
    else if (oidcError) setError(t('auth:oidcSignInFailed'));
  }, [searchParams, t]);

  useEffect(() => {
    // Failure to load /auth/methods is not fatal — the local form still works because
    // the server defaults to local-only. Treat any error as "local only" so the page
    // always renders cleanly even when the methods endpoint is missing on an old build.
    api
      .get<AuthMethodsResponse>('/auth/methods')
      .then(setMethods)
      .catch(() => setMethods({ local: true, ldap: false, windows: false, windowsEndpoint: null, oidc: false }));
  }, []);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setSubmitting(true);
    try {
      await login(username, password);
      navigate('/');
      // On success we navigate away, so we deliberately leave `submitting` set to keep
      // the button in its busy state until the route unmounts the page.
    } catch {
      setError(t('auth:invalidCredentials'));
      setSubmitting(false);
    }
  };

  const handleWindowsSignIn = async () => {
    setError('');
    if (!methods?.windowsEndpoint) return;
    try {
      // Browser handles the Negotiate handshake transparently when the URL is in the
      // Local Intranet zone and the user has a valid Kerberos ticket. The server emits
      // np_auth + np_csrf cookies on success; the body carries identity only (no JWT —
      // Windows SSO is ambient-credential driven, so the token is never echoed back).
      const response = await api.post<LoginResponse>('/auth/windows');
      useAuthStore.setState({
        userId: response.userId,
        username: response.username,
        role: response.role,
        isAuthenticated: true,
      });
      navigate('/');
    } catch {
      setError(t('auth:windowsSignInFailed'));
    }
  };

  const hasPasswordLogin = methods === null || methods.local || methods.ldap;
  const hasFederatedLogin = methods?.windows || methods?.oidc;

  return (
    <div className="np-shell np-login min-h-screen bg-surface flex items-center justify-center px-4 py-10">
      {/* Atmospheric, skin-aware backdrop (see .np-login-* in index.css). */}
      <div className="np-login-aurora" aria-hidden />
      <div className="np-login-grid" aria-hidden />
      <div className="np-login-card np-fade-up relative z-10 w-full max-w-sm rounded-3xl p-8">
        <div className="text-center mb-7">
          <span className="np-login-logo inline-block">
            <BrandLogo alt={t('auth:logoAlt')} className="w-16 h-16 mx-auto mb-4" />
          </span>
          <h1 className="font-headline text-3xl font-bold tracking-tight bg-gradient-to-r from-primary to-primary-container bg-clip-text text-transparent">
            {t('auth:title')}
          </h1>
          <p className="text-sm font-label text-on-surface-variant mt-1.5">{t('auth:tagline')}</p>
        </div>

        {methods?.oidc && (
          <a
            href={methods.oidcEndpoint ?? '/api/auth/oidc'}
            className="w-full flex items-center justify-center gap-2 rounded-xl py-2.5 mb-3 text-sm font-medium text-on-surface bg-surface-low/70 ring-1 ring-outline-variant/50 hover:bg-surface-high hover:ring-outline-variant transition-colors"
          >
            <Certificate size={16} className="text-primary" />
            {t('auth:oidcSignIn', { provider: methods.oidcDisplayName ?? t('auth:singleSignOn') })}
          </a>
        )}

        {methods?.windows && (
          <button
            type="button"
            onClick={handleWindowsSignIn}
            className="w-full flex items-center justify-center gap-2 rounded-xl py-2.5 mb-3 text-sm font-medium text-on-surface bg-surface-low/70 ring-1 ring-outline-variant/50 hover:bg-surface-high hover:ring-outline-variant transition-colors"
            title={t('auth:windowsSignInHint')}
          >
            <FingerprintRecognition size={16} className="text-primary" />
            {t('auth:windowsSignIn')}
          </button>
        )}

        {methods?.windows && (
          <p className="text-xs text-outline text-center mb-4">{t('auth:windowsSignInHint')}</p>
        )}

        {hasFederatedLogin && hasPasswordLogin && (
          <>
            <div className="flex items-center gap-3 mb-5 text-[11px] uppercase tracking-wider text-outline">
              <div className="flex-1 h-px bg-outline-variant/40" />
              <span>{t('auth:orDivider')}</span>
              <div className="flex-1 h-px bg-outline-variant/40" />
            </div>
          </>
        )}

        {error && (
          <div className="flex items-center gap-2 rounded-xl bg-red-500/10 text-red-600 dark:text-red-400 px-3 py-2.5 mb-4 text-sm ring-1 ring-red-500/20">
            <WarningFilled size={16} className="shrink-0" />
            <span>{error}</span>
          </div>
        )}

        {hasPasswordLogin ? <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label htmlFor="np-login-username" className="block text-xs font-semibold uppercase tracking-wide text-on-surface-variant mb-1.5">
              {t('auth:username')}
            </label>
            <div className="relative">
              <User size={16} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-on-surface-variant/70" />
              <input
                id="np-login-username"
                type="text"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                className={inputClass}
                required
                autoFocus
                autoComplete="username"
              />
            </div>
          </div>

          <div>
            <label htmlFor="np-login-password" className="block text-xs font-semibold uppercase tracking-wide text-on-surface-variant mb-1.5">
              {t('auth:password')}
            </label>
            <div className="relative">
              <Locked size={16} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-on-surface-variant/70" />
              <input
                id="np-login-password"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className={inputClass}
                required
                autoComplete="current-password"
                placeholder="••••••••"
              />
            </div>
          </div>

          <button
            type="submit"
            disabled={submitting}
            className="group relative w-full flex items-center justify-center gap-2 overflow-hidden rounded-xl py-2.5 text-sm font-semibold text-on-primary bg-gradient-to-r from-primary to-primary-container shadow-lg shadow-primary/30 transition-all hover:-translate-y-0.5 hover:shadow-primary/45 focus:outline-none focus:ring-2 focus:ring-primary/50 disabled:opacity-70 disabled:hover:translate-y-0"
          >
            {/* Sliding light sheen on hover. */}
            <span className="pointer-events-none absolute inset-0 -translate-x-full bg-gradient-to-r from-transparent via-white/25 to-transparent transition-transform duration-700 group-hover:translate-x-full" />
            {submitting && <CircleDash size={16} className="animate-spin" />}
            {t('auth:signIn')}
            {!submitting && <ArrowRight size={16} className="transition-transform group-hover:translate-x-0.5" />}
          </button>

          <p className="text-xs text-outline text-center pt-1">
            {t('auth:firstLoginHint')}
          </p>
        </form> : (
          !hasFederatedLogin && methods !== null && (
            <p className="text-sm text-on-surface-variant text-center">{t('auth:noMethodsAvailable')}</p>
          )
        )}
      </div>
    </div>
  );
}
