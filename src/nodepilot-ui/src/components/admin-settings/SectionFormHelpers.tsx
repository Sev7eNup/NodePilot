import { ChevronDown, ChevronRight, Chip, FlashFilled } from '@carbon/icons-react';
import { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  adminSettings,
  SettingsApiError,
  type SettingsSectionResponse,
} from '../../api/adminSettings';
import { EnvOverrideBadge } from './EnvOverrideBadge';
import { refreshAiCapabilities } from '../../hooks/useAiCapabilities';
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
  // What the Save button actually PUT. "Keep mine" after a 412 has to re-send exactly that
  // (the PascalCase DTO the card mapped), not the raw camelCase form state.
  const pendingPayloadRef = useRef<unknown>(null);

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
      pendingPayloadRef.current = payload;
      return adminSettings.putSection<T>(section, payload, data.etag);
    },
    onSuccess: (fresh) => {
      pendingPayloadRef.current = null;
      queryClient.setQueryData(['admin-settings', section], fresh);
      queryClient.invalidateQueries({ queryKey: ['admin-settings', 'status'] });
      // These sections drive the visibility of the AI entry points (buttons + AI-Chat nav) —
      // refresh so a save takes effect without a reload. ('Llm' saves through its own mutation
      // in IntegrationsSection, listed here so a future move onto this helper keeps the refresh.)
      if (section === 'AiKnowledge' || section === 'Llm') refreshAiCapabilities(queryClient);
    },
    onError: (err: unknown) => {
      if (err instanceof SettingsApiError && err.status === 412 && err.body?.current) {
        setConflict(err.body.current as SettingsSectionResponse<T>);
        return;
      }
      pendingPayloadRef.current = null;
      if (err instanceof SettingsApiError && err.status === 400 && err.body?.errors) {
        setErrors(err.body.errors.map((e) => {
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
        const retryPayload = pendingPayloadRef.current ?? form;
        queryClient.setQueryData(['admin-settings', section], conflict);
        setConflict(null);
        adminSettings.putSection<T>(section, retryPayload, conflict.etag)
          .then((fresh) => {
            queryClient.setQueryData(['admin-settings', section], fresh);
            if (section === 'AiKnowledge' || section === 'Llm') refreshAiCapabilities(queryClient);
          })
          .catch((e: unknown) => setErrors([e instanceof Error ? e.message : String(e)]))
          .finally(() => { pendingPayloadRef.current = null; });
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
    // Rule + generous gap: the save action belongs to the card, not to whatever field
    // happens to sit above it — without the separation it reads as part of the last group.
    <div className="mt-7 space-y-3 border-t border-outline-variant/15 pt-5">
      {errors && errors.length > 0 && (
        <div className="bg-error-container/30 border border-error/30 rounded-md p-3 text-on-error-container text-sm">
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
    <div className="np-card p-5 sm:p-6">
      <h3 className="font-semibold text-on-surface flex items-center gap-2.5 border-b border-outline-variant/15 pb-3 mb-4">
        <span className="shrink-0 text-on-surface-variant"><Icon size={18} /></span> {title}
      </h3>
      {children}
    </div>
  );
}

/**
 * The tighter card five sections had each declared for themselves: `p-4`, no rule under the
 * heading, bare icon. It is NOT the same shape as {@link Card} above — that one is roomier and
 * separates its heading — so the two are kept apart rather than merged, which would silently
 * restyle those five pages.
 *
 * `headingMargin` and `bodyClassName` exist only to preserve what each call site renders today
 * (three used `mb-3`, two `mb-4`; SystemInfo wraps its rows in `space-y-1.5`). Pass complete
 * class literals — Tailwind's scanner has to see them in the source.
 */
export function CompactCard({
  icon: Icon, title, headingMargin = 'mb-3', bodyClassName, children,
}: Readonly<{
  icon: React.ComponentType<{ size?: number }>;
  title: string;
  headingMargin?: string;
  bodyClassName?: string;
  children: React.ReactNode;
}>) {
  return (
    <div className="np-card p-4">
      <h3 className={`font-semibold text-on-surface flex items-center gap-2 ${headingMargin}`}>
        <Icon size={18} /> {title}
      </h3>
      {bodyClassName ? <div className={bodyClassName}>{children}</div> : children}
    </div>
  );
}

/**
 * Sub-group heading inside a settings card.
 *
 * Long cards (AI knowledge, logging, performance) are really three or four unrelated blocks
 * stacked on top of each other, and previously nothing but a 16 px margin said so. The
 * heading therefore carries its own separation — a rule above plus real breathing room — and
 * a quiet micro-label look that can't be confused with the card title one level up.
 * `first:` drops both for a group that opens a card, so no card starts with a stray rule.
 */
/**
 * Collapsible sub-block inside a settings card. Wears the same shell as the editors it sits next
 * to (bordered, one step up from the card surface) so it reads as a peer block rather than as
 * something appended after the form ended — a naked heading plus one control on the bare card
 * surface reads as a leftover.
 *
 * `summary` is what the header shows while collapsed and is the whole point of collapsing: the
 * setting stays legible without being unfolded. Open/closed is owned by the caller, because the
 * useful default is usually "open iff this block is doing something".
 */
export function DisclosurePanel({
  title, summary, open, onToggle, bodyId, children,
}: Readonly<{
  title: string;
  summary?: string;
  open: boolean;
  onToggle: () => void;
  /** Ties the header button to the body for `aria-controls`; must be unique on the page. */
  bodyId: string;
  children: React.ReactNode;
}>) {
  return (
    <div className="mt-4 rounded-md border border-outline-variant bg-surface-low/40">
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={open}
        aria-controls={bodyId}
        className="flex w-full items-center gap-2 rounded-md px-3 py-2 text-left transition-colors hover:bg-surface-low/60"
      >
        {open
          ? <ChevronDown size={12} className="shrink-0 text-on-surface-variant" />
          : <ChevronRight size={12} className="shrink-0 text-on-surface-variant" />}
        <span className="text-[11px] font-semibold uppercase tracking-wider text-on-surface-variant">
          {title}
        </span>
        {summary && <span className="ml-auto truncate text-xs text-on-surface-variant">{summary}</span>}
      </button>
      {open && (
        <div id={bodyId} className="border-t border-outline-variant/60 p-3">
          {children}
        </div>
      )}
    </div>
  );
}

export function GroupHeading({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <h4 className="mt-7 mb-3 border-t border-outline-variant/15 pt-5 text-[11px] font-semibold uppercase tracking-wider text-on-surface-variant first:mt-0 first:border-t-0 first:pt-0">
      {children}
    </h4>
  );
}

/**
 * Inline confidentiality/caution note. Token-driven (`warning`), not a palette literal, so it
 * follows every skin — see the status-token rule for the designer.
 */
export function WarningNote({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <p className="rounded-md border border-warning/40 bg-warning-container/25 px-3 py-2.5 text-xs leading-relaxed text-on-warning-container">
      {children}
    </p>
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

/**
 * Checkbox row. `hint` and `children` (e.g. a {@link WarningNote} that only shows while the
 * toggle is on) render *indented under the label*, aligned past the checkbox — so the text
 * visibly belongs to this switch instead of floating between two of them, which is what the
 * old `-mt-1` hints at the call sites did.
 */
export function Toggle({
  label, checked, onChange, configKey, effectiveSource, isEnvLocked, hint, children,
}: Readonly<{ label: string; checked: boolean; onChange: (v: boolean) => void; configKey: string; effectiveSource: Record<string, string>; isEnvLocked: (k: string) => boolean; hint?: string; children?: React.ReactNode }>) {
  return (
    <div className="py-1.5">
      <label className="flex items-center gap-2.5 text-sm cursor-pointer w-fit">
        <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)}
          disabled={isEnvLocked(configKey)} className="h-4 w-4 shrink-0 rounded disabled:opacity-50" />
        {label}
        <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
      </label>
      {(hint || children) && (
        <div className="mt-2 ml-[1.625rem] space-y-2">
          {hint && <p className="text-xs leading-relaxed text-on-surface-variant">{hint}</p>}
          {children}
        </div>
      )}
    </div>
  );
}

export function TextInput({
  label, value, onChange, configKey, effectiveSource, isEnvLocked, placeholder,
}: Readonly<{ label: string; value: string; onChange: (v: string) => void; configKey: string; effectiveSource: Record<string, string>; isEnvLocked: (k: string) => boolean; placeholder?: string }>) {
  const locked = isEnvLocked(configKey);
  return (
    <div>
      <label className="text-xs font-medium text-on-surface-variant mb-1.5 flex items-center gap-2">
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
      <label className="text-xs font-medium text-on-surface-variant mb-1.5 flex items-center gap-2">
        {label}
        <EnvOverrideBadge source={effectiveSource[configKey] ?? ''} configKey={configKey} />
      </label>
      <input type="number" value={value} min={min} max={max} disabled={locked}
        onChange={(e) => onChange(Number.parseInt(e.target.value, 10) || 0)}
        className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-surface-low disabled:text-on-surface-variant" />
      {hint && <p className="text-[11px] text-on-surface-variant/80 mt-1.5 leading-relaxed">{hint}</p>}
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
