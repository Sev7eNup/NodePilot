import {
  Add,
  BareMetalServer,
  ColorPalette,
  Edit,
  Password,
  Translate,
  TrashCan,
  User,
} from '@carbon/icons-react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import type { Credential } from '../types/api';
import { useRole } from '../lib/rbac';
import { useThemeStore, THEMES, type Theme } from '../stores/themeStore';
import { useLangStore } from '../stores/langStore';
import { confirmDialog } from '../stores/confirmStore';
import { toast } from '../stores/toastStore';
import { formatDateOnly } from '../lib/format';
import type { AppLang } from '../i18n';
import { SystemSettingsPage } from './SystemSettingsPage';

type TopTab = 'personal' | 'system';

/** Mirror of the backend warn-window default (Alerting CredentialExpiring gauge, 14 days). */
const EXPIRY_WARN_DAYS = 14;

/**
 * Expiry state chip for a credential row: red once the expiry timestamp lies in the
 * past, amber inside the warn window, otherwise just the date as muted subtle text.
 */
function CredentialExpiryBadge({ expiresAt }: { expiresAt: string | null }) {
  const { t } = useTranslation(['credentials']);
  // Day-granular badge → a mount-time "now" suffices (same pattern as EditorStatusBanners,
  // sans interval) and keeps Date.now() out of render for react-hooks/purity.
  const [nowMs] = useState(() => Date.now());
  if (!expiresAt) return null;
  const ms = new Date(expiresAt).getTime() - nowMs;
  if (Number.isNaN(ms)) return null;
  if (ms < 0) {
    return (
      <span className="text-[11px] px-1.5 py-0.5 rounded-md bg-red-500/15 text-red-600 font-medium">
        {t('credentials:expired')}
      </span>
    );
  }
  const days = Math.ceil(ms / 86_400_000);
  if (days <= EXPIRY_WARN_DAYS) {
    return (
      <span className="text-[11px] px-1.5 py-0.5 rounded-md bg-amber-500/15 text-amber-600 font-medium">
        {t('credentials:expiresInDays', { days })}
      </span>
    );
  }
  return <span className="text-[11px] text-outline">{formatDateOnly(expiresAt)}</span>;
}

export function SettingsPage() {
  const { t } = useTranslation(['settings', 'adminSettings']);
  const { isAdmin } = useRole();
  // Deep-link support: the dashboard banner shortcuts navigate to /settings?tab=system
  // (LLM config lives under the System → Integrations sub-tab) or ?tab=personal. The
  // Keep tab state in the URL so the app-header breadcrumb, deep links and browser
  // back/forward navigation always describe the same view.
  const [searchParams, setSearchParams] = useSearchParams();
  const topTab: TopTab = isAdmin && searchParams.get('tab') === 'system' ? 'system' : 'personal';
  const setTopTab = (next: TopTab) => {
    const params = new URLSearchParams(searchParams);
    params.set('tab', next);
    if (next === 'personal') params.delete('section');
    else if (!params.get('section')) params.set('section', 'integrations');
    setSearchParams(params);
  };

  // The System view has a sub-tab bar with 8 entries (Integrations …
  // Database) plus wide data sections (notably the DB-Admin table). At max-w-4xl
  // the tab bar wraps onto two rows — so System gets a wider max-width so all tabs
  // fit on one row. Personal stays narrow (it's just forms).
  const systemView = isAdmin && topTab === 'system';

  return (
    <div className={`space-y-4 mx-auto np-fade-up ${systemView ? 'max-w-7xl' : 'max-w-4xl'}`}>
      <header>
        <p className="text-sm text-on-surface-variant font-label max-w-3xl">{t('settings:subtitle')}</p>
      </header>
      {/* Top-level tab bar — Personal / System. The System tab is only rendered for
          Admins; non-admins see the page exactly as before (just the Personal content). */}
      {isAdmin && (
        <div className="np-tab-list">
          <button
            type="button"
            onClick={() => setTopTab('personal')}
            className={`np-tab ${topTab === 'personal' ? 'is-active' : ''}`}
          >
            <User size={14} />
            {t('adminSettings:tabPersonal')}
          </button>
          <button
            type="button"
            onClick={() => setTopTab('system')}
            className={`np-tab ${topTab === 'system' ? 'is-active' : ''}`}
          >
            <BareMetalServer size={14} />
            {t('adminSettings:tabSystem')}
          </button>
        </div>
      )}
      {systemView ? <SystemSettingsPage /> : <PersonalSettings />}
    </div>
  );
}

/**
 * The original Settings content (theme/language/credentials), extracted into its own
 * component so the new top-level tab bar can switch between Personal and the Admin
 * System sub-page without duplicating state or breaking the React component tree.
 */
function PersonalSettings() {
  const { t } = useTranslation(['settings', 'credentials', 'common']);
  const queryClient = useQueryClient();
  const { canWrite, canDelete } = useRole();
  const emptyForm = { name: '', username: '', password: '', domain: '', expiresAt: '' };
  const [showCreate, setShowCreate] = useState(false);
  // Non-null while the inline panel edits an existing credential (mutually exclusive with showCreate).
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState(emptyForm);
  // Original expiresAt of the credential being edited. The date input is day-granular,
  // so if the user leaves the date untouched we send this exact string back — a
  // CLI-set time-of-day (e.g. T18:00:00Z) must not be rewritten to midnight by a rename.
  const [originalExpiresAt, setOriginalExpiresAt] = useState<string | null>(null);

  /** `<input type="date">` value (yyyy-MM-dd) → ISO UTC midnight; empty stays null. */
  const toIsoExpiry = (d: string) => (d ? `${d}T00:00:00Z` : null);

  /** Preserve the original timestamp when the picked date still matches its date part. */
  const resolveExpiry = (d: string) =>
    originalExpiresAt && d === originalExpiresAt.slice(0, 10) ? originalExpiresAt : toIsoExpiry(d);

  const { theme, setTheme } = useThemeStore();
  const { lang, setLang } = useLangStore();

  const { data: credentials } = useQuery({
    queryKey: ['credentials'],
    queryFn: () => api.get<Credential[]>('/credentials'),
  });

  const createMutation = useMutation({
    mutationFn: () =>
      api.post('/credentials', {
        name: form.name,
        username: form.username,
        password: form.password,
        domain: form.domain || null,
        expiresAt: toIsoExpiry(form.expiresAt),
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['credentials'] });
      setShowCreate(false);
      setForm(emptyForm);
    },
  });

  const updateMutation = useMutation({
    mutationFn: (id: string) =>
      api.put(`/credentials/${id}`, {
        name: form.name,
        username: form.username,
        // Blank password = keep the stored one (backend treats null as "unchanged").
        password: form.password || null,
        domain: form.domain || null,
        expiresAt: resolveExpiry(form.expiresAt),
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['credentials'] });
      setEditingId(null);
      setForm(emptyForm);
      setOriginalExpiresAt(null);
      toast.success(t('credentials:saved'));
    },
    onError: (err: Error) => toast.error(t('common:saveFailed', { message: err.message })),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/credentials/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['credentials'] }),
  });

  // Derived from the THEMES registry (+ the OS-following `system`) so a new skin
  // shows up here automatically — no per-scheme wiring.
  const themeOptions: { value: Theme; key: string }[] = [
    ...THEMES.map((th) => ({ value: th.id as Theme, key: th.labelKey })),
    { value: 'system', key: 'themeSystem' },
  ];

  const langOptions: { value: AppLang; key: 'languageDe' | 'languageEn' }[] = [
    { value: 'de', key: 'languageDe' },
    { value: 'en', key: 'languageEn' },
  ];

  return (
    <div className="space-y-6">
      {/* Appearance section: theme + language */}
      <div className="np-card p-4">
        <h3 className="font-semibold text-on-surface flex items-center gap-2 mb-4">
          <ColorPalette size={18} /> {t('settings:appearance')}
        </h3>
        <div className="space-y-5">
          <div>
            <label className="block text-xs font-medium text-on-surface-variant mb-2">{t('settings:theme')}</label>
            <div className="flex flex-wrap gap-2">
              {themeOptions.map(({ value, key }) => (
                <button
                  key={value}
                  type="button"
                  onClick={() => setTheme(value)}
                  className={`np-btn np-btn-sm ${theme === value ? 'np-btn-selected' : 'np-btn-secondary'}`}
                >
                  {t(`settings:${key}`)}
                </button>
              ))}
            </div>
          </div>
          <div className="border-t border-outline-variant pt-4">
            <label className="text-xs font-medium text-on-surface-variant mb-2 flex items-center gap-1.5">
              <Translate size={12} /> {t('settings:language')}
            </label>
            <div className="flex flex-wrap gap-2">
              {langOptions.map(({ value, key }) => (
                <button
                  key={value}
                  type="button"
                  onClick={() => setLang(value)}
                  className={`np-btn np-btn-sm ${lang === value ? 'np-btn-selected' : 'np-btn-secondary'}`}
                >
                  {t(`settings:${key}`)}
                </button>
              ))}
            </div>
            <p className="text-[11px] text-outline mt-2">{t('settings:languageHint')}</p>
          </div>
        </div>
      </div>
      <div className="np-card p-4">
        <div className="flex items-center justify-between mb-4">
          <h3 className="font-semibold text-on-surface flex items-center gap-2">
            <Password size={18} /> {t('credentials:title')}
          </h3>
          {canWrite && (
            <button
              type="button"
              onClick={() => { setEditingId(null); setForm(emptyForm); setShowCreate(true); }}
              className="np-btn np-btn-sm np-btn-primary"
            >
              <Add size={14} /> {t('credentials:addCredential')}
            </button>
          )}
        </div>

        {(showCreate || editingId !== null) && (
          <div className="bg-surface-low rounded-md p-3 mb-4 space-y-3">
            <div className="grid grid-cols-2 gap-3">
              <input
                type="text"
                value={form.name}
                onChange={(e) => setForm({ ...form, name: e.target.value })}
                placeholder={t('credentials:credentialName')}
                className="px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-primary/40 focus:border-primary"
              />
              <input
                type="text"
                value={form.domain}
                onChange={(e) => setForm({ ...form, domain: e.target.value })}
                placeholder={t('credentials:domainOptional')}
                className="px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-primary/40 focus:border-primary"
              />
              <input
                type="text"
                value={form.username}
                onChange={(e) => setForm({ ...form, username: e.target.value })}
                placeholder={t('common:username')}
                className="px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-primary/40 focus:border-primary"
              />
              <div>
                <input
                  type="password"
                  value={form.password}
                  onChange={(e) => setForm({ ...form, password: e.target.value })}
                  placeholder={t('common:password')}
                  className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-primary/40 focus:border-primary"
                  autoComplete="new-password"
                />
                {editingId !== null && (
                  <p className="text-[11px] text-outline mt-1">{t('credentials:passwordKeepHint')}</p>
                )}
              </div>
              <div>
                <label htmlFor="credential-expires-at" className="block text-[11px] text-on-surface-variant mb-1">
                  {t('credentials:expiresAtOptional')}
                </label>
                <input
                  id="credential-expires-at"
                  type="date"
                  value={form.expiresAt}
                  onChange={(e) => setForm({ ...form, expiresAt: e.target.value })}
                  className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-primary/40 focus:border-primary"
                />
              </div>
            </div>
            <div className="flex gap-2">
              <button
                type="button"
                onClick={() => (editingId !== null ? updateMutation.mutate(editingId) : createMutation.mutate())}
                className="np-btn np-btn-primary"
              >
                {t('common:save')}
              </button>
              <button
                type="button"
                onClick={() => { setShowCreate(false); setEditingId(null); setForm(emptyForm); }}
                className="np-btn np-btn-secondary"
              >
                {t('common:cancel')}
              </button>
            </div>
          </div>
        )}

        {credentials?.length === 0 ? (
          <p className="text-outline text-sm">{t('credentials:noCredentials')}</p>
        ) : (
          <div className="space-y-2">
            {credentials?.map((c) => (
              <div key={c.id} className="np-row flex items-center justify-between py-2 px-3 border border-surface-variant/30 rounded-md">
                <div>
                  <div className="flex items-center gap-2">
                    <p className="font-medium text-sm">{c.name}</p>
                    <CredentialExpiryBadge expiresAt={c.expiresAt} />
                  </div>
                  <p className="text-xs text-outline">
                    {c.domain ? `${c.domain}\\` : ''}{c.username}
                  </p>
                </div>
                <div className="flex items-center gap-1">
                  {canWrite && (
                    <button
                      type="button"
                      onClick={() => {
                        setShowCreate(false);
                        setEditingId(c.id);
                        setOriginalExpiresAt(c.expiresAt);
                        setForm({
                          name: c.name,
                          username: c.username,
                          password: '',
                          domain: c.domain ?? '',
                          expiresAt: c.expiresAt ? c.expiresAt.slice(0, 10) : '',
                        });
                      }}
                      title={t('credentials:edit')}
                      aria-label={t('credentials:edit')}
                      className="np-btn np-btn-icon np-btn-ghost"
                    >
                      <Edit size={16} />
                    </button>
                  )}
                  {canDelete && (
                    <button
                      type="button"
                      onClick={async () => {
                        if (await confirmDialog({ message: t('credentials:deleteConfirm'), danger: true }))
                          deleteMutation.mutate(c.id);
                      }}
                      title={t('common:delete')}
                      aria-label={t('common:delete')}
                      className="np-btn np-btn-icon np-btn-danger"
                    >
                      <TrashCan size={16} />
                    </button>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
