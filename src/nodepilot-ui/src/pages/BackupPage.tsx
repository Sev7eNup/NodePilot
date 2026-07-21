import { Archive, Certificate, Download, SecurityServices, Upload, WarningAltFilled } from '@carbon/icons-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import {
  backupApi, type BackupPreviewResult, type BackupRestoreResult, type RestorePolicy,
} from '../api/backup';
import { confirmDialog } from '../stores/confirmStore';

const MIN_PASSPHRASE = 12;
const POLICIES: RestorePolicy[] = ['skip', 'rename', 'overwrite'];

export function BackupPage() {
  const { t } = useTranslation(['backup', 'common']);
  const [searchParams, setSearchParams] = useSearchParams();
  const tab: 'backup' | 'restore' = searchParams.get('tab') === 'restore' ? 'restore' : 'backup';
  const setTab = (next: 'backup' | 'restore') => {
    const params = new URLSearchParams(searchParams);
    params.set('tab', next);
    setSearchParams(params);
  };

  return (
    <div className="space-y-4 max-w-4xl mx-auto np-fade-up">
      <header>
        <p className="text-sm text-on-surface-variant font-label">{t('backup:subtitle')}</p>
      </header>

      <div className="np-tab-list">
        {(['backup', 'restore'] as const).map((key) => {
          const Icon = key === 'backup' ? Download : Upload;
          return (
            <button
              key={key}
              type="button"
              onClick={() => setTab(key)}
              className={`np-tab ${tab === key ? 'is-active' : ''}`}
            >
              <Icon size={14} />
              {t(`backup:tabs.${key}`)}
            </button>
          );
        })}
      </div>

      <div className="np-card p-5">
        {tab === 'backup' ? <BackupTab /> : <RestoreTab />}
      </div>
    </div>
  );
}

function BackupTab() {
  const { t } = useTranslation(['backup', 'common']);
  const { data: manifest } = useQuery({ queryKey: ['backup', 'manifest'], queryFn: backupApi.getManifest });

  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [passphrase, setPassphrase] = useState('');
  const [confirm, setConfirm] = useState('');
  // Client-side validation errors (empty selection / short or mismatched passphrase) live
  // outside the mutation — they never reach the server.
  const [validationError, setValidationError] = useState<string | null>(null);

  const createMutation = useMutation({
    mutationFn: (args: { sections: string[]; passphrase: string }) =>
      backupApi.download(args.sections, args.passphrase),
    onSuccess: () => { setPassphrase(''); setConfirm(''); },
  });

  // Default to all sections selected once the manifest loads.
  useEffect(() => {
    if (manifest) setSelected(new Set(manifest.sections.map((s) => s.section)));
  }, [manifest]);

  const toggle = (section: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(section)) next.delete(section); else next.add(section);
      return next;
    });

  const create = () => {
    setValidationError(null);
    createMutation.reset();
    if (selected.size === 0) { setValidationError(t('backup:backup.noSections')); return; }
    if (passphrase.length < MIN_PASSPHRASE) { setValidationError(t('backup:backup.tooShort')); return; }
    if (passphrase !== confirm) { setValidationError(t('backup:backup.mismatch')); return; }
    createMutation.mutate({ sections: [...selected], passphrase });
  };

  const busy = createMutation.isPending;
  const success = createMutation.isSuccess;
  const error = validationError ?? (createMutation.error as Error | null)?.message ?? null;

  return (
    <div className="space-y-5">
      <section>
        <h2 className="font-label text-sm font-semibold text-on-surface mb-2">{t('backup:backup.selectSections')}</h2>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
          {(manifest?.sections ?? []).map((s) => (
            <label key={s.section} className="flex items-center gap-2.5 p-2.5 rounded-lg border border-outline-variant/40 bg-surface-high/40 hover:bg-surface-high cursor-pointer transition-colors">
              <input type="checkbox" checked={selected.has(s.section)} onChange={() => toggle(s.section)} className="rounded" />
              <span className="text-sm font-label text-on-surface flex-1">{t(`backup:sections.${s.section}`)}</span>
              <span className="text-xs text-on-surface-variant">{s.count}</span>
            </label>
          ))}
        </div>
        <p className="text-xs text-on-surface-variant font-label mt-2">{t('backup:backup.dependencyNote')}</p>
      </section>

      <section className="space-y-3 max-w-sm">
        <div>
          <label className="font-label text-xs font-semibold text-on-surface-variant">{t('backup:backup.passphrase')}</label>
          <input type="password" value={passphrase} onChange={(e) => setPassphrase(e.target.value)} className="input-field mt-1" autoComplete="new-password" />
        </div>
        <div>
          <label className="font-label text-xs font-semibold text-on-surface-variant">{t('backup:backup.passphraseConfirm')}</label>
          <input type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} className="input-field mt-1" autoComplete="new-password" />
        </div>
        <p className="text-xs text-on-surface-variant font-label">{t('backup:backup.passphraseHint')}</p>
      </section>

      {error && <p className="text-sm text-error font-label">{error}</p>}
      {success && <p className="text-sm text-green-600 font-label">{t('backup:backup.success')}</p>}

      <button
        onClick={create}
        disabled={busy}
        className="flex items-center gap-2 px-5 py-2.5 rounded-md text-sm font-label font-medium bg-primary text-on-primary shadow-sm hover:shadow-md transition-all disabled:opacity-50"
      >
        <Download size={16} /> {busy ? t('backup:backup.creating') : t('backup:backup.create')}
      </button>
    </div>
  );
}

function RestoreTab() {
  const { t } = useTranslation(['backup', 'common']);
  const fileRef = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [passphrase, setPassphrase] = useState('');
  const [policies, setPolicies] = useState<Record<string, RestorePolicy>>({});

  const previewMutation = useMutation({
    mutationFn: (args: { file: File; passphrase: string }) =>
      backupApi.preview(args.file, args.passphrase),
    // Initialise per-section conflict policy to "skip".
    onSuccess: (r: BackupPreviewResult) =>
      setPolicies(Object.fromEntries(r.sections.map((s) => [s.section, 'skip' as RestorePolicy]))),
  });
  const restoreMutation = useMutation({
    mutationFn: (args: { file: File; passphrase: string; policyString: string }) =>
      backupApi.restore(args.file, args.passphrase, args.policyString),
  });

  const policyString = useMemo(
    () => Object.entries(policies).map(([s, p]) => `${s}=${p}`).join(','),
    [policies],
  );

  const runPreview = () => {
    // Reset both mutations so a re-preview clears the old table, error, and restore result.
    previewMutation.reset();
    restoreMutation.reset();
    if (!file) return;
    previewMutation.mutate({ file, passphrase });
  };

  const runRestore = async () => {
    restoreMutation.reset();
    if (!file || !passphrase) return;
    if (!(await confirmDialog({ message: t('backup:restore.confirm'), danger: true }))) return;
    restoreMutation.mutate({ file, passphrase, policyString });
  };

  const preview = previewMutation.data ?? null;
  const result: BackupRestoreResult | null = restoreMutation.data ?? null;
  const busy = previewMutation.isPending || restoreMutation.isPending;
  const error = (previewMutation.error as Error | null)?.message
    ?? (restoreMutation.error as Error | null)?.message ?? null;

  return (
    <div className="space-y-5">
      <section className="space-y-3 max-w-md">
        <div>
          <label className="font-label text-xs font-semibold text-on-surface-variant">{t('backup:restore.selectFile')}</label>
          <input
            ref={fileRef}
            type="file"
            accept=".npbackup,application/json"
            onChange={(e) => { setFile(e.target.files?.[0] ?? null); previewMutation.reset(); restoreMutation.reset(); }}
            className="block mt-1 text-sm text-on-surface-variant file:mr-3 file:py-1.5 file:px-3 file:rounded-md file:border-0 file:bg-surface-high file:text-on-surface file:text-sm file:cursor-pointer"
          />
        </div>
        <div>
          <label className="font-label text-xs font-semibold text-on-surface-variant">{t('backup:restore.passphrase')}</label>
          <input type="password" value={passphrase} onChange={(e) => setPassphrase(e.target.value)} className="input-field mt-1" autoComplete="off" />
          <p className="text-xs text-on-surface-variant font-label mt-1">{t('backup:restore.passphraseHint')}</p>
        </div>
        <button
          onClick={runPreview}
          disabled={busy || !file}
          className="flex items-center gap-2 px-4 py-2 rounded-md text-sm font-label font-medium bg-surface-high text-on-surface hover:bg-surface-highest transition-colors disabled:opacity-50"
        >
          <Upload size={15} /> {busy && !result ? t('backup:restore.previewing') : t('backup:restore.preview')}
        </button>
      </section>
      {error && <p className="text-sm text-error font-label whitespace-pre-wrap">{error}</p>}
      {preview && (
        <section className="space-y-3">
          <div className={`flex items-center gap-2 text-sm font-label ${preview.integrityVerified ? 'text-green-600' : 'text-amber-600'}`}>
            {preview.integrityVerified ? <Certificate size={16} /> : <SecurityServices size={16} />}
            {preview.integrityVerified ? t('backup:restore.integrityVerified') : t('backup:restore.integrityUnverified')}
            {preview.appVersion && <span className="text-on-surface-variant">· {t('backup:restore.appVersion')}: {preview.appVersion}</span>}
          </div>

          <table className="w-full text-sm font-label">
            <thead className="text-xs text-on-surface-variant text-left border-b border-outline-variant/30">
              <tr>
                <th className="py-1.5">{t('backup:restore.col.section')}</th>
                <th className="py-1.5 text-right">{t('backup:restore.col.inBackup')}</th>
                <th className="py-1.5 text-right">{t('backup:restore.col.new')}</th>
                <th className="py-1.5 text-right">{t('backup:restore.col.conflicts')}</th>
                <th className="py-1.5 pl-4">{t('backup:restore.col.policy')}</th>
              </tr>
            </thead>
            <tbody>
              {preview.sections.map((s) => (
                <tr key={s.section} className="border-b border-outline-variant/10">
                  <td className="py-1.5 text-on-surface">{t(`backup:sections.${s.section}`)}</td>
                  <td className="py-1.5 text-right text-on-surface-variant">{s.inBackup}</td>
                  <td className="py-1.5 text-right text-on-surface-variant">{s.new}</td>
                  <td className={`py-1.5 text-right ${s.conflicts > 0 ? 'text-amber-600 font-semibold' : 'text-on-surface-variant'}`}>{s.conflicts}</td>
                  <td className="py-1.5 pl-4">
                    <select
                      value={policies[s.section] ?? 'skip'}
                      onChange={(e) => setPolicies((p) => ({ ...p, [s.section]: e.target.value as RestorePolicy }))}
                      className="input-field py-1 text-xs"
                    >
                      {POLICIES.map((p) => <option key={p} value={p}>{t(`backup:restore.policy.${p}`)}</option>)}
                    </select>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          {preview.warnings.length > 0 && (
            <div className="text-xs text-amber-600 font-label space-y-0.5">
              {preview.warnings.map((w, i) => <p key={i} className="flex items-start gap-1.5"><WarningAltFilled size={13} className="mt-0.5 shrink-0" />{w}</p>)}
            </div>
          )}

          <button
            onClick={runRestore}
            disabled={busy || !passphrase}
            className="flex items-center gap-2 px-5 py-2.5 rounded-md text-sm font-label font-medium bg-error text-white shadow-sm hover:shadow-md transition-all disabled:opacity-50"
          >
            <Archive size={16} /> {busy && !result ? t('backup:restore.running') : t('backup:restore.run')}
          </button>
        </section>
      )}
      {result && (
        <section className="space-y-2">
          <p className="text-sm text-green-600 font-label font-semibold">{t('backup:restore.success')}</p>
          <table className="w-full text-sm font-label">
            <thead className="text-xs text-on-surface-variant text-left border-b border-outline-variant/30">
              <tr>
                <th className="py-1.5">{t('backup:restore.col.section')}</th>
                <th className="py-1.5 text-right">{t('backup:restore.col.created')}</th>
                <th className="py-1.5 text-right">{t('backup:restore.col.overwritten')}</th>
                <th className="py-1.5 text-right">{t('backup:restore.col.skipped')}</th>
                <th className="py-1.5 text-right">{t('backup:restore.col.renamed')}</th>
              </tr>
            </thead>
            <tbody>
              {result.sections.map((s) => (
                <tr key={s.section} className="border-b border-outline-variant/10">
                  <td className="py-1.5 text-on-surface">{t(`backup:sections.${s.section}`)}</td>
                  <td className="py-1.5 text-right text-on-surface-variant">{s.created}</td>
                  <td className="py-1.5 text-right text-on-surface-variant">{s.overwritten}</td>
                  <td className="py-1.5 text-right text-on-surface-variant">{s.skipped}</td>
                  <td className="py-1.5 text-right text-on-surface-variant">{s.renamed}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {result.settings && (
            <p className="text-sm text-on-surface-variant font-label">
              {t('backup:restore.settings')}: {result.settings.applied ? t('backup:restore.settingsApplied') : t('backup:restore.settingsSkipped')}
            </p>
          )}
          {result.warnings.length > 0 && (
            <div className="text-xs text-amber-600 font-label space-y-0.5">
              {result.warnings.map((w, i) => <p key={i} className="flex items-start gap-1.5"><WarningAltFilled size={13} className="mt-0.5 shrink-0" />{w}</p>)}
            </div>
          )}
        </section>
      )}
    </div>
  );
}
