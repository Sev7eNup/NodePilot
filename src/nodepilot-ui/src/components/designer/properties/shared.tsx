import {
  FlashFilled,
  FlashOff,
  Locked,
  Password,
  Search,
  TaskComplete,
  ValueVariable,
  WarningAltFilled,
} from '@carbon/icons-react';
import { useRef, useState, useCallback, useEffect, useMemo, type DragEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import i18n from '../../../i18n';
import { api } from '../../../api/client';
import type { UpstreamVariable } from '../../../lib/upstreamVariables';
import { validateTemplateExpression } from '../../../lib/templateValidation';
import { hasDraggedVariableExpression, readDraggedVariableExpression, setVariableDragData } from '../../../lib/variableDragDrop';
import {
  ACTIVITY_ICONS,
  ACTIVITY_LABEL_KEYS,
  REMOTE_ACTIVITY_TYPES,
  TIMEOUT_ACTIVITY_TYPES
} from '../../../lib/activityCatalog.generated';
import { isCustomActivityType, getCustomActivityFacts } from '../../../lib/customActivities';
import { useVariableAutocomplete } from './useVariableAutocomplete';
import { VariableSuggestionsDropdown } from './VariableSuggestionsDropdown';
import { AnchoredPickerPopover } from './AnchoredPickerPopover';

/** Preference store for the autocomplete toggle. Wrapped in its own module so every
 *  VariableInsertField instance reads the same value (and a change instantly propagates
 *  to all open fields, since they all share the same storage key + read path). */
const AUTOCOMPLETE_STORAGE_KEY = 'nodepilot.designer.inlineAutocomplete';
function readAutocompletePref(): boolean {
  if (typeof window === 'undefined') return true;
  const v = globalThis.localStorage.getItem(AUTOCOMPLETE_STORAGE_KEY);
  return v === null ? true : v === 'true';
}

/** Minimal shape returned by GET /api/global-variables — admin lists + picker share this. */
type GlobalVariableRow = {
  id: string;
  name: string;
  value: string | null;
  isSecret: boolean;
  description: string | null;
};

export { ACTIVITY_ICONS, REMOTE_ACTIVITY_TYPES, TIMEOUT_ACTIVITY_TYPES };

/** Quiet picker/toggle chip shared by all field-level pickers (Vars/Globals/Liste/`{{`).
 *  Active = the app-wide accent state (`bg-primary/15 text-primary`, like toolbar toggles);
 *  everything else stays neutral so accent color is reserved for interactive states. */
export const pickerChipClass = (active = false) =>
  `inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-label font-semibold transition-colors cursor-pointer disabled:opacity-40 disabled:cursor-default ${
    active
      ? 'bg-primary/15 text-primary hover:bg-primary/25'
      : 'bg-surface-high text-on-surface-variant hover:bg-surface-highest'
  }`;

/** Locale-aware label for an activity type. Custom activities use their author-set name (never i18n);
 *  built-ins fall back to the raw type if unmapped. */
export function getActivityLabel(type: string): string {
  if (isCustomActivityType(type)) return getCustomActivityFacts(type)?.name ?? type;
  const key = ACTIVITY_LABEL_KEYS[type];
  if (!key) return type;
  return i18n.t(`activities:labels.${key}`);
}

export interface ConfigProps {
  config: Record<string, unknown>;
  onUpdate: (patch: Record<string, unknown>) => void;
  upstreamVars?: UpstreamVariable[];
  // Only relevant for StartWorkflow / ForEach — ignored by other configs.
  onOpenWorkflowPicker?: () => void;
  // Identity of the step being edited. Used by configs that offer an inline step-test
  // (currently only RunScriptConfig's "Run" button inside the script editor).
  workflowId?: string;
  stepId?: string;
  outputVariableName?: string;
  lastStepsByStepId?: Map<string, import('../../../types/api').StepExecution>;
  /**
   * True when the step targets local execution (no remote machine). Defaults to true at the call
   * site so a config rendered standalone (e.g. in tests) isn't wrongly treated as remote. Used by
   * RunScriptConfig to gate the process-isolation toggle, which is local-only.
   */
  isLocalTarget?: boolean;
}

export function TimeoutField({ value, onChange }: Readonly<{ value: number | undefined; onChange: (v: number | undefined) => void }>) {
  const { t } = useTranslation(['properties']);
  const display = value ?? 0;
  return (
    <Field label={t('properties:timeout')}>
      <input
        type="number"
        value={display}
        onChange={(e) => {
          const raw = e.target.value;
          if (raw === '') { onChange(0); return; }
          const parsed = parseInt(raw, 10);
          onChange(Number.isFinite(parsed) && parsed > 0 ? parsed : 0);
        }}
        className="input-field"
        min={0}
      />
      <p className="text-[10px] text-on-surface-variant">
        {t('properties:timeoutHint')}
      </p>
    </Field>
  );
}

export function Field({ label, children }: Readonly<{ label: string; children: React.ReactNode }>) {
  return (
    <div className="space-y-1.5">
      {label && <label className="block font-label text-xs font-semibold text-on-surface-variant">{label}</label>}
      {children}
    </div>
  );
}

/**
 * Boolean toggle rendered as a modern switch pill. Semantically still an
 * `<input type="checkbox">` (checkbox role, aria-label, keyboard behavior),
 * so existing tests keep working — only the visual is a switch (`.np-switch`
 * in index.css hides the native box and paints track + knob). Replaces the
 * old "checkbox stuffed into an .input-field" pattern in the config panes.
 */
export function SwitchField({ label, stateText, checked, onChange, disabled = false, ariaLabel }: Readonly<{
  /** Optional inline text right of the switch (e.g. current-state wording). */
  stateText?: string;
  label?: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  ariaLabel: string;
}>) {
  const body = (
    <label className={`flex items-center gap-2 select-none py-1 ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'}`}>
      <input
        type="checkbox"
        className="np-switch"
        aria-label={ariaLabel}
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
      />
      {stateText && <span className="text-sm text-on-surface">{stateText}</span>}
    </label>
  );
  return label !== undefined ? <Field label={label}>{body}</Field> : body;
}

export function VariableInsertField({
  label, value, onChange, upstreamVars, multiline = false, rows = 3, placeholder, mono = false, compact = false, extraPickers,
}: Readonly<{
  label: string;
  value: string;
  onChange: (val: string) => void;
  upstreamVars: UpstreamVariable[];
  multiline?: boolean;
  rows?: number;
  placeholder?: string;
  mono?: boolean;
  /** Compact mode: hide picker toolbar, keep inline `{{` autocomplete. For dense rows like ParameterTable. */
  compact?: boolean;
  /** Optional additional picker chips rendered next to Variable/Global pickers (e.g. options-list picker). */
  extraPickers?: React.ReactNode;
}>) {
  const { t } = useTranslation(['properties']);
  const inputRef = useRef<HTMLTextAreaElement | HTMLInputElement>(null);
  const [autocompleteEnabled, setAutocompleteEnabled] = useState(readAutocompletePref);
  const [dragActive, setDragActive] = useState(false);

  const autocomplete = useVariableAutocomplete({
    inputRef,
    value,
    onChange,
    upstreamVars,
    enabled: autocompleteEnabled,
  });

  const insertVariable = useCallback(
    (expression: string) => {
      const el = inputRef.current;
      if (!el) {
        onChange(value + expression);
        return;
      }
      const start = el.selectionStart ?? value.length;
      const end = el.selectionEnd ?? value.length;
      const newValue = value.slice(0, start) + expression + value.slice(end);
      onChange(newValue);
      requestAnimationFrame(() => {
        el.focus();
        const newPos = start + expression.length;
        el.setSelectionRange(newPos, newPos);
      });
    },
    [value, onChange],
  );

  const toggleAutocomplete = () => {
    setAutocompleteEnabled((prev) => {
      const next = !prev;
      try { globalThis.localStorage.setItem(AUTOCOMPLETE_STORAGE_KEY, String(next)); } catch { /* quota, private-mode */ }
      if (!next) autocomplete.close();
      return next;
    });
  };

  const validation = useMemo(
    () => validateTemplateExpression(value, upstreamVars),
    [value, upstreamVars],
  );
  const sqlTemplateWarning = /sql query/i.test(label) && value.includes('{{')
    ? t('properties:panel.sqlTemplateWarning')
    : null;

  const handleDragOver = useCallback((e: DragEvent) => {
    if (!hasDraggedVariableExpression(e)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
    setDragActive(true);
  }, []);
  const handleDrop = useCallback((e: DragEvent) => {
    const expression = readDraggedVariableExpression(e);
    if (!expression) return;
    e.preventDefault();
    setDragActive(false);
    insertVariable(expression);
  }, [insertVariable]);

  return (
    <div className={compact ? 'flex-1' : 'space-y-1'}>
      {!compact && label && <label className="font-label text-xs font-semibold text-on-surface-variant">{label}</label>}
      <div
        className={`relative rounded-md ${dragActive ? 'ring-2 ring-primary/50' : ''}`}
        onDragOver={handleDragOver}
        onDragEnter={handleDragOver}
        onDragLeave={() => setDragActive(false)}
        onDrop={handleDrop}
      >
        {multiline ? (
          <textarea
            ref={inputRef as React.Ref<HTMLTextAreaElement>}
            value={value}
            onChange={(e) => { onChange(e.target.value); }}
            onSelect={autocomplete.refresh}
            onKeyUp={autocomplete.refresh}
            onKeyDown={autocomplete.handleKeyDown}
            onBlur={() => setTimeout(autocomplete.close, 150) /* small delay so onMouseDown on the dropdown item still fires first */}
            className={`input-field ${mono ? 'font-mono text-xs' : ''}`}
            rows={rows}
            placeholder={placeholder}
          />
        ) : (
          <input
            ref={inputRef as React.Ref<HTMLInputElement>}
            type="text"
            value={value}
            onChange={(e) => { onChange(e.target.value); }}
            onSelect={autocomplete.refresh}
            onKeyUp={autocomplete.refresh}
            onKeyDown={autocomplete.handleKeyDown}
            onBlur={() => setTimeout(autocomplete.close, 150)}
            className={`input-field ${mono ? 'font-mono text-xs' : ''}`}
            placeholder={placeholder}
          />
        )}

        <VariableSuggestionsDropdown
          open={autocomplete.open}
          suggestions={autocomplete.filtered}
          selectedIdx={autocomplete.selectedIdx}
          onPick={autocomplete.pick}
          anchorRef={inputRef}
        />
      </div>
      {!compact && validation.issues.length > 0 && (
        <div className={`flex items-start gap-1.5 text-[10px] font-label leading-snug ${
          validation.status === 'error' ? 'text-error' : 'text-amber-700 dark:text-amber-300'
        }`}>
          <WarningAltFilled size={11} className="mt-0.5 shrink-0" />
          <span>
            {validation.issues.slice(0, 2).map((issue) => issue.token ? `${issue.token}: ${issue.message}` : issue.message).join(' ')}
          </span>
        </div>
      )}
      {!compact && sqlTemplateWarning && (
        <div className="flex items-start gap-1.5 text-[10px] font-label leading-snug text-amber-700 dark:text-amber-300">
          <WarningAltFilled size={11} className="mt-0.5 shrink-0" />
          <span>{sqlTemplateWarning}</span>
        </div>
      )}
      {/* Picker tray BELOW the input — the label → input → tray order makes sure that
          in a FieldGrid all inputs align at the top edge, no matter how many picker
          chips a field has (e.g. Operation: Field/Select has none; Path: VariableInsertField has 3+). */}
      {!compact && (
        <div className="flex flex-wrap items-center gap-1 pt-0.5">
          {upstreamVars.length > 0 && (
            <VariablePicker upstreamVars={upstreamVars} onPick={insertVariable} />
          )}
          <GlobalVariablePicker onPick={insertVariable} />
          {extraPickers}
          {/* Toggle for inline autocomplete. When on, typing `{{...` pops open a
              dropdown below the input. */}
          <button
            type="button"
            onClick={toggleAutocomplete}
            className={pickerChipClass(autocompleteEnabled)}
            title={autocompleteEnabled ? t('properties:autocompleteOn') : t('properties:autocompleteOff')}
          >
            {autocompleteEnabled ? <FlashFilled size={10} /> : <FlashOff size={10} />}
            {'{{'}
          </button>
        </div>
      )}
    </div>
  );
}

/**
 * Compact variable picker — a single button that opens a searchable popover
 * instead of rendering every upstream variable as a chip inline. Keeps per-field
 * UI small even when many upstream variables exist.
 */
export function VariablePicker({
  upstreamVars, onPick,
}: Readonly<{
  upstreamVars: UpstreamVariable[];
  onPick: (expression: string) => void;
}>) {
  const { t } = useTranslation(['properties', 'common']);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const containerRef = useRef<HTMLDivElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!open) return;
    const onClickOutside = (e: MouseEvent) => {
      const target = e.target as Node;
      if (containerRef.current && !containerRef.current.contains(target) && !popoverRef.current?.contains(target)) {
        setOpen(false);
        setQuery('');
      }
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { setOpen(false); setQuery(''); }
    };
    document.addEventListener('mousedown', onClickOutside);
    document.addEventListener('keydown', onKey);
    // Autofocus search input after mount
    requestAnimationFrame(() => searchRef.current?.focus());
    return () => {
      document.removeEventListener('mousedown', onClickOutside);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  // Group by step for readability; filter by query (matches expression or label)
  const { groups, total } = useMemo(() => {
    const q = query.trim().toLowerCase();
    const filtered = q
      ? upstreamVars.filter((v) => v.expression.toLowerCase().includes(q) || v.label.toLowerCase().includes(q))
      : upstreamVars;
    const byStep = new Map<string, UpstreamVariable[]>();
    for (const v of filtered) {
      const baseLabel = v.label.split(' → ')[0];
      if (!byStep.has(baseLabel)) byStep.set(baseLabel, []);
      byStep.get(baseLabel)!.push(v);
    }
    return { groups: [...byStep.entries()], total: filtered.length };
  }, [upstreamVars, query]);

  return (
    <div ref={containerRef} className="relative inline-block">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className={pickerChipClass()}
        title={t('properties:varsTooltip', { count: upstreamVars.length })}
      >
        <ValueVariable size={10} />
        {t('properties:vars')}
        <span className="opacity-60 tabular-nums">{upstreamVars.length}</span>
      </button>
      <AnchoredPickerPopover open={open} anchorRef={containerRef} popoverRef={popoverRef}>
          <div className="p-2 border-b border-outline-variant/30">
            <div className="relative">
              <Search size={12} className="absolute left-2 top-1/2 -translate-y-1/2 text-on-surface-variant" />
              <input
                ref={searchRef}
                type="text"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder={t('properties:searchVariable')}
                className="w-full bg-surface-high rounded pl-7 pr-2 py-1 text-xs font-label focus:outline-none focus:ring-1 focus:ring-primary/40"
              />
            </div>
          </div>
          <div className="min-h-0 max-h-[26rem] overflow-y-auto py-1">
            {total === 0 && (
              <div className="text-[11px] font-label text-on-surface-variant px-3 py-2">{t('common:noResults')}</div>
            )}
            {groups.map(([stepLabel, items]) => (
              <div key={stepLabel} className="pb-1">
                <div className="text-[9px] font-label font-bold text-outline uppercase tracking-widest px-3 pt-1.5 pb-0.5">
                  {stepLabel}
                </div>
                {items.map((v) => {
                  const suffix = v.label.includes(' → ') ? v.label.split(' → ')[1] : '';
                  return (
                    <button
                      key={v.expression}
                      draggable
                      onDragStart={(e) => setVariableDragData(e, v.expression)}
                      type="button"
                      onClick={() => { onPick(v.expression); setOpen(false); setQuery(''); }}
                      className="w-full flex items-center justify-between gap-2 px-3 py-1 text-left hover:bg-surface-high transition-colors"
                      title={t('properties:insertVariable', { expression: v.expression })}
                    >
                      <code className="text-[10px] font-mono text-primary truncate">{v.variable}</code>
                      {suffix && <span className="text-[10px] font-label text-on-surface-variant truncate">{suffix}</span>}
                    </button>
                  );
                })}
              </div>
            ))}
          </div>
      </AnchoredPickerPopover>
    </div>
  );
}

/**
 * Picker for admin-managed global variables — inserts <c>{{globals.NAME}}</c>. Renders the
 * button even when no globals exist (the popover then shows an empty-state hint with a link
 * explaining where to create them) so users discover the feature. Data is cached across the
 * session via React Query so opening the popover on many fields doesn't fan out to the API.
 */
export function GlobalVariablePicker({ onPick }: Readonly<{ onPick: (expression: string) => void }>) {
  const { t } = useTranslation(['properties', 'common']);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const containerRef = useRef<HTMLDivElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);

  const { data: globals = [], isLoading } = useQuery({
    queryKey: ['global-variables'],
    queryFn: () => api.get<GlobalVariableRow[]>('/global-variables'),
    // Long staleTime — globals rarely change, and the picker is opened often.
    staleTime: 60_000,
  });

  useEffect(() => {
    if (!open) return;
    const onClickOutside = (e: MouseEvent) => {
      const target = e.target as Node;
      if (containerRef.current && !containerRef.current.contains(target) && !popoverRef.current?.contains(target)) {
        setOpen(false); setQuery('');
      }
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { setOpen(false); setQuery(''); }
    };
    document.addEventListener('mousedown', onClickOutside);
    document.addEventListener('keydown', onKey);
    requestAnimationFrame(() => searchRef.current?.focus());
    return () => {
      document.removeEventListener('mousedown', onClickOutside);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return globals;
    return globals.filter((g) =>
      g.name.toLowerCase().includes(q) || (g.description?.toLowerCase().includes(q) ?? false));
  }, [globals, query]);

  return (
    <div ref={containerRef} className="relative inline-block">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className={pickerChipClass()}
        title={t('properties:globalsTooltip', { count: globals.length })}
      >
        <Password size={10} />
        {t('properties:globals')}
        <span className="opacity-60 tabular-nums">{globals.length}</span>
      </button>
      <AnchoredPickerPopover open={open} anchorRef={containerRef} popoverRef={popoverRef}>
          <div className="p-2 border-b border-outline-variant/30">
            <div className="relative">
              <Search size={12} className="absolute left-2 top-1/2 -translate-y-1/2 text-on-surface-variant" />
              <input
                ref={searchRef}
                type="text"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder={t('properties:searchGlobalVariable')}
                className="w-full bg-surface-high rounded pl-7 pr-2 py-1 text-xs font-label focus:outline-none focus:ring-1 focus:ring-primary/40"
              />
            </div>
          </div>
          <div className="min-h-0 max-h-[26rem] overflow-y-auto py-1">
            {isLoading && (
              <div className="text-[11px] font-label text-on-surface-variant px-3 py-2">{t('common:loading')}</div>
            )}
            {!isLoading && globals.length === 0 && (
              <div className="text-[11px] font-label text-on-surface-variant px-3 py-2">
                {t('properties:noGlobalsHint')}
              </div>
            )}
            {!isLoading && globals.length > 0 && filtered.length === 0 && (
              <div className="text-[11px] font-label text-on-surface-variant px-3 py-2">{t('common:noResults')}</div>
            )}
            {filtered.map((g) => {
              const expression = `{{globals.${g.name}}}`;
              return (
                <button
                  key={g.id}
                  draggable
                  onDragStart={(e) => setVariableDragData(e, expression)}
                  type="button"
                  onClick={() => { onPick(expression); setOpen(false); setQuery(''); }}
                  className="w-full flex items-center justify-between gap-2 px-3 py-1 text-left hover:bg-surface-high transition-colors"
                  title={g.description ? `${expression} — ${g.description}` : t('properties:insertVariable', { expression })}
                >
                  <span className="flex items-center gap-1.5 min-w-0">
                    {g.isSecret && <Locked size={9} className="text-on-surface-variant shrink-0" />}
                    <code className="text-[10px] font-mono text-primary truncate">{g.name}</code>
                  </span>
                  {!g.isSecret && g.value && (
                    <span className="text-[10px] font-label text-on-surface-variant truncate max-w-[7rem]" title={g.value}>
                      = {g.value}
                    </span>
                  )}
                </button>
              );
            })}
          </div>
      </AnchoredPickerPopover>
    </div>
  );
}

/**
 * Unified target/credential field. Always renders a single text input plus the standard
 * Variable / Global / Options pickers above it — no Select/Variable mode toggle. The user
 * can type a literal ID, paste a `{{var}}` expression, pick from upstream/global pickers,
 * or click "Liste" to choose from `options` (which inserts the option's `id`).
 *
 * If the current value matches one of the options' IDs, the option's friendly label is
 * shown as a small caption below — so the user immediately sees which machine/credential
 * the GUID resolves to.
 *
 * Replaces the previous Select/Variable two-mode design that surfaced two visually
 * different layouts depending on whether the value happened to be a known GUID or a
 * literal/variable expression.
 */
export function DynamicTargetField({
  label, value, onChange, options, placeholder, upstreamVars, emptyLabel, optionPickerLabel = 'Liste',
}: Readonly<{
  label: string;
  value: string;
  onChange: (val: string) => void;
  options: { id: string; label: string }[];
  placeholder: string;
  upstreamVars: UpstreamVariable[];
  /** Caption shown below the input when value is empty (no preselected option). */
  emptyLabel: string;
  /** Picker-button label, e.g. "Maschine wählen" or "Credential wählen". */
  optionPickerLabel?: string;
}>) {
  const matchedOption = useMemo(() => options.find((o) => o.id === value), [options, value]);

  return (
    <div className="space-y-1.5">
      {label && <label className="font-label text-xs font-semibold text-on-surface-variant">{label}</label>}
      <VariableInsertField
        label=""
        value={value}
        onChange={onChange}
        upstreamVars={upstreamVars}
        placeholder={placeholder}
        mono
        extraPickers={
          options.length > 0 ? (
            <OptionsPicker options={options} onPick={onChange} label={optionPickerLabel} />
          ) : null
        }
      />
      {matchedOption ? (
        <p className="text-[10px] font-label text-emerald-600 dark:text-emerald-400 truncate" title={matchedOption.label}>
          ✓ {matchedOption.label}
        </p>
      ) : value === '' ? (
        <p className="text-[10px] font-label text-on-surface-variant italic">{emptyLabel}</p>
      ) : null}
    </div>
  );
}

/**
 * Picker chip for choosing one of `options` from a searchable popover. Mirrors the
 * VariablePicker / GlobalVariablePicker visual style so the row of pickers above an
 * input stays coherent.
 */
export function OptionsPicker({
  options, onPick, label,
}: Readonly<{
  options: { id: string; label: string }[];
  onPick: (id: string) => void;
  label: string;
}>) {
  const { t } = useTranslation(['properties', 'common']);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const containerRef = useRef<HTMLDivElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!open) return;
    const onClickOutside = (e: MouseEvent) => {
      const target = e.target as Node;
      if (containerRef.current && !containerRef.current.contains(target) && !popoverRef.current?.contains(target)) {
        setOpen(false); setQuery('');
      }
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { setOpen(false); setQuery(''); }
    };
    document.addEventListener('mousedown', onClickOutside);
    document.addEventListener('keydown', onKey);
    requestAnimationFrame(() => searchRef.current?.focus());
    return () => {
      document.removeEventListener('mousedown', onClickOutside);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return options;
    return options.filter((o) => o.label.toLowerCase().includes(q) || o.id.toLowerCase().includes(q));
  }, [options, query]);

  return (
    <div ref={containerRef} className="relative inline-block">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className={pickerChipClass()}
        title={t('properties:listTooltip', { label, count: options.length })}
      >
        <TaskComplete size={10} />
        {t('properties:list')}
        <span className="opacity-60 tabular-nums">{options.length}</span>
      </button>
      <AnchoredPickerPopover
        open={open}
        anchorRef={containerRef}
        popoverRef={popoverRef}
        surfaceClass="bg-surface-container border-outline-variant/30"
      >
          <div className="p-2 border-b border-outline-variant/30">
            <div className="relative">
              <Search size={12} className="absolute left-2 top-1/2 -translate-y-1/2 text-on-surface-variant" />
              <input
                ref={searchRef}
                type="text"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder={t('common:searchEllipsis')}
                className="w-full bg-surface-high rounded pl-7 pr-2 py-1 text-xs font-label focus:outline-none focus:ring-1 focus:ring-primary/40"
              />
            </div>
          </div>
          <div className="min-h-0 max-h-[26rem] overflow-y-auto py-1">
            {filtered.length === 0 && (
              <div className="text-[11px] font-label text-on-surface-variant px-3 py-2">{t('common:noResults')}</div>
            )}
            {filtered.map((o) => (
              <button
                key={o.id}
                type="button"
                onClick={() => { onPick(o.id); setOpen(false); setQuery(''); }}
                className="w-full flex items-center justify-between gap-2 px-3 py-1 text-left hover:bg-surface-high transition-colors"
                title={t('properties:insertVariable', { expression: o.label })}
              >
                <span className="text-xs font-label text-on-surface truncate">{o.label}</span>
                <code className="text-[9px] font-mono text-on-surface-variant truncate max-w-[6rem]" title={o.id}>{o.id.slice(0, 8)}</code>
              </button>
            ))}
          </div>
      </AnchoredPickerPopover>
    </div>
  );
}
