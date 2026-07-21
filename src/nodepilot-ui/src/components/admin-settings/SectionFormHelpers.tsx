import { Chip, FlashFilled } from '@carbon/icons-react';
import { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  adminSettings,
  SettingsApiError,
  type SettingsSectionResponse,
} from '../../api/adminSettings';
import { EnvOverrideBadge } from './EnvOverrideBadge';
import { EtagConflictDialog } from './EtagConflictDialog';

// Shared form helpers used by SecuritySection and PerformanceSection — both consist
// of many small flat cards, all with the same GET + PUT + ETag + 412-conflict flow.
// Pulled out of the section files so the cards stay readable and the per-section
// component is just composition + form-state.

export type FormUi<T> = {
  loading: boolean;
  data: SettingsSectionResponse<T>;
  form: T;
  set: (next: T) => void;
  isEnvLocked: (k: string) => boolean;
  save: (payload: unknown) => void;
  errors: string[] | null;
  dialog: React.ReactNode;
};

export function useSectionForm<T>(section: string, fallback: T): FormUi<T> | { loading: true } & Partial<FormUi<T>> {
  const queryClient = useQueryClient();
  const [conflict, setConflict] = useState<SettingsSectionResponse<T> | null>(null);
  const [errors, setErrors] = useState<string[] | null>(null);

  const { data, isLoading } = useQuery({
    queryKey: ['admin-settings', section],
    queryFn: () => adminSettings.getSection<T>(section),
  });

  const [form, setForm] = useState<T>(fallback);
  useEffect(() => { if (data) setForm(data.payload); }, [data]);

  const isEnvLocked = (key: string) => {
    const src = data?.effectiveSource[key];
    return src === 'env' || src === 'cli';
  };

  const saveMutation = useMutation({
    mutationFn: async (payload: unknown) => {
      setErrors(null);
      if (!data) throw new Error('No section snapshot loaded yet.');
      return adminSettings.putSection<T>(section, payload, data.etag);
    },
    onSuccess: (fresh) => {
      queryClient.setQueryData(['admin-settings', section], fresh);
      queryClient.invalidateQueries({ queryKey: ['admin-settings', 'status'] });
    },
    onError: (err: unknown) => {
      if (err instanceof SettingsApiError && err.status === 412 && err.body?.current) {
        setConflict(err.body.current as SettingsSectionResponse<T>);
        return;
      }
      if (err instanceof SettingsApiError && err.status === 400 && err.body?.errors) {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        setErrors(err.body.errors.map((e: any) => {
          const fields = e.fields?.length ? `${e.fields.join(', ')}: ` : '';
          return `${fields}${e.message ?? JSON.stringify(e)}`;
        }));
        return;
      }
      setErrors([err instanceof Error ? err.message : String(err)]);
    },
  });

  if (isLoading || !data) {
    return { loading: true };
  }

  const dialog = (
    <EtagConflictDialog
      open={!!conflict}
      serverSnapshot={conflict}
      localDraft={form}
      onKeepMine={() => {
        if (!conflict) return;
        queryClient.setQueryData(['admin-settings', section], conflict);
        setConflict(null);
        adminSettings.putSection<T>(section, form, conflict.etag)
          .then((fresh) => queryClient.setQueryData(['admin-settings', section], fresh))
          .catch((e: unknown) => setErrors([e instanceof Error ? e.message : String(e)]));
      }}
      onTakeTheirs={() => {
        if (!conflict) return;
        queryClient.setQueryData(['admin-settings', section], conflict);
        setConflict(null);
      }}
      onCancel={() => setConflict(null)}
    />
  );

  return {
    loading: false,
    data,
    form,
    set: setForm,
    isEnvLocked,
    save: (payload: unknown) => saveMutation.mutate(payload),
    errors,
    dialog,
  };
}

export function ErrorsAndSave({ errors, onSave }: Readonly<{ errors: string[] | null; onSave: () => void }>) {
  const { t } = useTranslation(['adminSettings']);
  return (
    <div className="mt-4 space-y-3">
      {errors && errors.length > 0 && (
        <div className="bg-red-50 border border-red-200 rounded-md p-3 text-red-900 text-sm">
          <p className="font-semibold mb-1">{t('adminSettings:validationErrorsTitle')}</p>
          <ul className="list-disc list-inside space-y-0.5">{errors.map((e, i) => <li key={i}>{e}</li>)}</ul>
        </div>
      )}
      <div className="flex justify-end">
        <button type="button" onClick={onSave}
          className="flex items-center gap-2 px-4 py-2 text-sm bg-blue-600 text-white hover:bg-blue-700 rounded-md">
          <Chip size={14} /> {t('adminSettings:saveButton')}
        </button>
      </div>
    </div>
  );
}

export function Card({ icon: Icon, title, children }: Readonly<{ icon: React.ComponentType<{ size?: number }>; title: string; children: React.ReactNode }>) {
  return (
    <div className="np-card p-4">
      <h3 className="font-semibold text-on-surface flex items-center gap-2 mb-3">
        <Icon size={18} /> {title}
      </h3>
      {children}
    </div>
  );
}

/**
 * Data-driven hot-reload hint. The GET response carries `isHotReloadable` per section; when true
 * the section's consumers read the live config (IOptionsMonitor.CurrentValue / IConfiguration
 * per use), so a save takes effect immediately — no service restart. Renders an emerald inline
 * hint; returns null for restart-required sections so the card layout is untouched.
 */
export function HotReloadHint({ isHotReloadable }: Readonly<{ isHotReloadable: boolean | undefined }>) {
  const { t } = useTranslation(['adminSettings']);
  if (!isHotReloadable) return null;
  return (
    <p className="flex items-center gap-1.5 text-[11px] font-medium text-emerald-700 dark:text-emerald-400 mb-3 leading-snug">
      <FlashFilled size={12} className="shrink-0" />
      {t('adminSettings:hotReloadHint')}
    </p>
  );
}

export function Toggle({
  label, checked, onChange, configKey, effectiveSource, isEnvLocked,
}: Readonly<{ label: string; checked: boolean; onChange: (v: boolean) => void; configKey: string; effectiveSource: Record<string, string>; isEnvLocked: (k: string) => boolean }>) {
  return (
    <label className="flex items-center gap-2 text-sm cursor-pointer my-1">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)}
        disabled={isEnvLocked(configKey)} className="rounded disabled:opacity-50" />
      {label}
      <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
    </label>
  );
}

export function TextInput({
  label, value, onChange, configKey, effectiveSource, isEnvLocked, placeholder,
}: Readonly<{ label: string; value: string; onChange: (v: string) => void; configKey: string; effectiveSource: Record<string, string>; isEnvLocked: (k: string) => boolean; placeholder?: string }>) {
  const locked = isEnvLocked(configKey);
  return (
    <div>
      <label className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
        {label}
        <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
      </label>
      <input type="text" value={value} onChange={(e) => onChange(e.target.value)} disabled={locked}
        placeholder={placeholder}
        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant" />
    </div>
  );
}

export function NumberInput({
  label, value, onChange, min, max, configKey, effectiveSource, isEnvLocked, hint,
}: Readonly<{ label: string; value: number; onChange: (v: number) => void; min: number; max: number; configKey: string; effectiveSource: Record<string, string>; isEnvLocked: (k: string) => boolean; hint?: string }>) {
  const locked = isEnvLocked(configKey);
  return (
    <div>
      <label className="text-xs font-medium text-on-surface-variant mb-1 flex items-center gap-2">
        {label}
        <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
      </label>
      <input type="number" value={value} min={min} max={max} disabled={locked}
        onChange={(e) => onChange(Number.parseInt(e.target.value, 10) || 0)}
        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant" />
      {hint && <p className="text-[11px] text-on-surface-variant/80 mt-1 leading-snug">{hint}</p>}
    </div>
  );
}

export function StringListEditor({
  label, value, onChange, placeholder,
}: Readonly<{ label: string; value: string[]; onChange: (next: string[]) => void; placeholder?: string }>) {
  const { t } = useTranslation('common');
  return (
    <div>
      <label className="block text-xs font-medium text-on-surface-variant mb-1">{label}</label>
      <div className="space-y-1">
        {value.map((v, idx) => (
          <div key={idx} className="flex items-center gap-2">
            <input
              type="text"
              value={v}
              placeholder={placeholder}
              onChange={(e) => {
                const next = [...value];
                next[idx] = e.target.value;
                onChange(next);
              }}
              className="flex-1 px-3 py-1.5 border border-outline-variant rounded-md text-sm font-mono focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
            <button
              type="button"
              onClick={() => onChange(value.filter((_, i) => i !== idx))}
              className="px-2 py-1 text-xs text-red-600 hover:bg-red-50 rounded"
            >
              ×
            </button>
          </div>
        ))}
        <button
          type="button"
          onClick={() => onChange([...value, ''])}
          className="text-xs text-blue-600 hover:bg-blue-50 px-2 py-1 rounded"
        >
          + {t('add')}
        </button>
      </div>
    </div>
  );
}
