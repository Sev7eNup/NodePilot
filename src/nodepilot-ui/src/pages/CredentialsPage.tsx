import { Add, Edit, TrashCan } from '@carbon/icons-react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useState } from 'react';
import { api } from '../api/client';
import type { Credential } from '../types/api';
import { DependencyLink } from '../components/dependencies/DependencyLink';
import { useRole } from '../lib/rbac';
import { credentialExpiryState } from '../lib/credentialExpiry';
import { confirmDialog } from '../stores/confirmStore';
import { toast } from '../stores/toastStore';
import { formatDateOnly } from '../lib/format';

/**
 * Expiry state chip for a credential row: red once the expiry timestamp lies in the
 * past, amber inside the warn window, otherwise just the date as muted subtle text.
 */
function CredentialExpiryBadge({ expiresAt }: { expiresAt: string | null }) {
  const { t } = useTranslation(['credentials']);
  // Day-granular badge -> a mount-time "now" suffices (same pattern as EditorStatusBanners,
  // sans interval) and keeps Date.now() out of render for react-hooks/purity.
  const [nowMs] = useState(() => Date.now());
  if (!expiresAt) return null;
  const expiry = credentialExpiryState(expiresAt, nowMs);
  if (expiry.kind === 'none') return null;
  if (expiry.kind === 'expired') {
    return (
      <span className="text-[11px] px-1.5 py-0.5 rounded-md bg-red-500/15 text-red-600 font-medium">
        {t('credentials:expired')}
      </span>
    );
  }
  if (expiry.kind === 'expiring') {
    return (
      <span className="text-[11px] px-1.5 py-0.5 rounded-md bg-amber-500/15 text-amber-600 font-medium">
        {t('credentials:expiresInDays', { days: expiry.daysLeft })}
      </span>
    );
  }
  return <span className="text-[11px] text-outline">{formatDateOnly(expiresAt)}</span>;
}

/** Credential management. The route is limited to Admin and Operator, like the API. */
export function CredentialsPage() {
  const { t } = useTranslation(['credentials', 'common']);
  const queryClient = useQueryClient();
  const { canDelete } = useRole();
  const emptyForm = { name: '', username: '', password: '', domain: '', expiresAt: '' };
  const [showCreate, setShowCreate] = useState(false);
  // Non-null while the inline panel edits an existing credential (mutually exclusive with
  // showCreate).
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState(emptyForm);
  // Original expiresAt of the credential being edited. The date input is day-granular,
  // so if the user leaves the date untouched we send this exact string back — a
  // CLI-set time-of-day (e.g. T18:00:00Z) must not be rewritten to midnight by a rename.
  const [originalExpiresAt, setOriginalExpiresAt] = useState<string | null>(null);

  /** `<input type="date">` value (yyyy-MM-dd) -> ISO UTC midnight; empty stays null. */
  const toIsoExpiry = (d: string) => (d ? `${d}T00:00:00Z` : null);

  /** Preserve the original timestamp when the picked date still matches its date part. */
  const resolveExpiry = (d: string) =>
    originalExpiresAt && d === originalExpiresAt.slice(0, 10) ? originalExpiresAt : toIsoExpiry(d);

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
    onError: (err: Error) => toast.error(t('common:createFailed', { message: err.message })),
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
    onError: (err: Error) => toast.error(t('common:deleteFailed', { message: err.message })),
  });

  return (
    <div className="max-w-4xl mx-auto np-fade-up">
      <div className="flex items-center justify-between gap-4 mb-6">
        <div>
          <p className="text-sm text-on-surface-variant mt-1 max-w-3xl">{t('credentials:subtitle')}</p>
        </div>
        <button
          type="button"
          onClick={() => { setEditingId(null); setForm(emptyForm); setShowCreate(true); }}
          title={t('credentials:addCredential')}
          className="flex items-center gap-2 px-3 py-2 sm:px-4 bg-blue-600 text-white rounded-md hover:bg-blue-700 text-sm whitespace-nowrap"
        >
          <Add size={16} /> <span className="hidden sm:inline">{t('credentials:addCredential')}</span>
        </button>
      </div>

      <div className="np-card p-4">
        {(showCreate || editingId !== null) && (
          <div
            role="group"
            aria-label={t(editingId !== null ? 'credentials:edit' : 'credentials:addCredential')}
            className="bg-surface-low rounded-md p-3 mb-4 space-y-3"
          >
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
                    <DependencyLink kind="credential" id={c.id} />
                    <CredentialExpiryBadge expiresAt={c.expiresAt} />
                  </div>
                  <p className="text-xs text-outline">
                    {c.domain ? `${c.domain}\\` : ''}{c.username}
                  </p>
                </div>
                <div className="flex items-center gap-1">
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
