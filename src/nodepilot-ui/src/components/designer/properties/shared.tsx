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

/** Preference store for the autocomplete toggle. Every VariableInsertField instance shares this
 *  storage key and read path, so a change applies to all open fields at once. */
const AUTOCOMPLETE_STORAGE_KEY = 'nodepilot.designer.inlineAutocomplete';
function readAutocompletePref(): boolean {
  if (typeof window === 'undefined') return true;
  const v = globalThis.localStorage.getItem(AUTOCOMPLETE_STORAGE_KEY);
  return v === null ? true : v === 'true';
}

/** Minimal global-variable shape shared by admin lists and the picker. */
type GlobalVariableRow = {
  id: string;
  name: string;
  value: string | null;
  isSecret: boolean;
  description: string | null;
};

export { ACTIVITY_ICONS, REMOTE_ACTIVITY_TYPES, TIMEOUT_ACTIVITY_TYPES };

/** Chip class shared by all field-level pickers (variables, globals, options, `{{`).
 *  The active state uses the app-wide accent color; every other state stays neutral so the
 *  accent is reserved for interactive states. */
export const pickerChipClass = (active = false) =>
  `inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-label font-semibold transition-colors cursor-pointer disabled:opacity-40 disabled:cursor-default ${
    active
      ? 'bg-primary/15 text-primary hover:bg-primary/25'
      : 'bg-surface-high text-on-surface-variant hover:bg-surface-highest'
  }`;

/** Locale-aware label for an activity type. Custom activities keep their author-set name and are
 *  never translated; built-ins fall back to the raw type when unmapped. */
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
  // Only relevant for StartWorkflow and ForEach; other configs ignore it.
  onOpenWorkflowPicker?: () => void;
  // Identity of the step being edited. Used by configs that offer an inline step test,
  // currently only the Run button in RunScriptConfig's script editor.
  workflowId?: string;
  stepId?: string;
  outputVariableName?: string;
  lastStepsByStepId?: Map<string, import('../../../types/api').StepExecution>;
  /**
   * True when the step runs locally (no remote machine). Call sites default it to true so a
   * config rendered standalone is not treated as remote. RunScriptConfig uses it to gate the
   * process-isolation toggle, which is local-only.
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
 * Boolean toggle rendered as a switch pill. It stays an `<input type="checkbox">`
 * (checkbox role, aria-label, keyboard behavior); only the visual is a switch, since
 * `.np-switch` in index.css hides the native box and paints track and knob.
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
  /** Compact mode: hide picker toolbar, keep inline `{{` autocomplete. For dense rows like
   * ParameterTable. */
  compact?: boolean;
  /** Optional additional picker chips rendered next to Variable/Global pickers (e.g. options-list
   * picker). */
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
      {/* Picker tray sits below the input. Ordering label, then input, then tray keeps all
          inputs in a FieldGrid aligned at the top edge, no matter how many picker chips a
          field has. */}
      {!compact && (
        <div className="flex flex-wrap items-center gap-1 pt-0.5">
          {upstreamVars.length > 0 && (
            <VariablePicker upstreamVars={upstreamVars} onPick={insertVariable} />
          )}
          <GlobalVariablePicker onPick={insertVariable} />
          {extraPickers}
          {/* Toggle for inline autocomplete. When on, typing `{{...` opens a dropdown
              below the input. */}
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
 * Open/close and search state shared by the field pickers below. An outside click or Escape
 * closes the popover and clears the query; the search box autofocuses once the popover mounts.
 * The query lives here rather than inside {@link PickerPopover} so each picker can filter with
 * a plain top-level `useMemo`.
 */
function useSearchablePicker() {
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

  const toggle = useCallback(() => setOpen((o) => !o), []);
  const close = useCallback(() => { setOpen(false); setQuery(''); }, []);

  return { open, toggle, close, query, setQuery, containerRef, popoverRef, searchRef };
}

/** Chip trigger, anchored popover and search box. `children` renders the already filtered rows. */
function PickerPopover({
  picker, icon, chipLabel, count, title, placeholder, surfaceClass, children,
}: Readonly<{
  picker: ReturnType<typeof useSearchablePicker>;
  icon: React.ReactNode;
  chipLabel: string;
  count: number;
  title: string;
  placeholder: string;
  surfaceClass?: string;
  children: React.ReactNode;
}>) {
  const { open, toggle, query, setQuery, containerRef, popoverRef, searchRef } = picker;
  return (
    <div ref={containerRef} className="relative inline-block">
      <button
        type="button"
        onClick={toggle}
        className={pickerChipClass()}
        title={title}
      >
        {icon}
        {chipLabel}
        <span className="opacity-60 tabular-nums">{count}</span>
      </button>
      <AnchoredPickerPopover
        open={open}
        anchorRef={containerRef}
        popoverRef={popoverRef}
        surfaceClass={surfaceClass}
      >
          <div className="p-2 border-b border-outline-variant/30">
            <div className="relative">
              <Search size={12} className="absolute left-2 top-1/2 -translate-y-1/2 text-on-surface-variant" />
              <input
                ref={searchRef}
                type="text"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder={placeholder}
                className="w-full bg-surface-high rounded pl-7 pr-2 py-1 text-xs font-label focus:outline-none focus:ring-1 focus:ring-primary/40"
              />
            </div>
          </div>
          <div className="min-h-0 max-h-[26rem] overflow-y-auto py-1">
            {children}
          </div>
      </AnchoredPickerPopover>
    </div>
  );
}

/**
 * Compact variable picker: a single button that opens a searchable popover instead of
 * rendering every upstream variable as an inline chip. Keeps the per-field UI small even
 * when many upstream variables exist.
 */
export function VariablePicker({
  upstreamVars, onPick,
}: Readonly<{
  upstreamVars: UpstreamVariable[];
  onPick: (expression: string) => void;
}>) {
  const { t } = useTranslation(['properties', 'common']);
  const picker = useSearchablePicker();

  // Group by step for readability and filter by query, matching expression or label.
  const { groups, total } = useMemo(() => {
    const q = picker.query.trim().toLowerCase();
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
  }, [upstreamVars, picker.query]);

  return (
    <PickerPopover
      picker={picker}
      icon={<ValueVariable size={10} />}
      chipLabel={t('properties:vars')}
      count={upstreamVars.length}
      title={t('properties:varsTooltip', { count: upstreamVars.length })}
      placeholder={t('properties:searchVariable')}
    >
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
                onClick={() => { onPick(v.expression); picker.close(); }}
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
    </PickerPopover>
  );
}

/**
 * Picker for admin-managed global variables. Inserts `{{globals.NAME}}`. The button renders
 * even when no globals exist, in which case the popover shows a hint linking to where they
 * are created. React Query caches the data for the session so opening the popover on many
 * fields does not fan out to the API.
 */
export function GlobalVariablePicker({ onPick }: Readonly<{ onPick: (expression: string) => void }>) {
  const { t } = useTranslation(['properties', 'common']);
  const picker = useSearchablePicker();

  const { data: globals = [], isLoading } = useQuery({
    queryKey: ['global-variables'],
    queryFn: () => api.get<GlobalVariableRow[]>('/global-variables'),
    // Long staleTime: globals rarely change and the picker is opened often.
    staleTime: 60_000,
  });

  const filtered = useMemo(() => {
    const q = picker.query.trim().toLowerCase();
    if (!q) return globals;
    return globals.filter((g) =>
      g.name.toLowerCase().includes(q) || (g.description?.toLowerCase().includes(q) ?? false));
  }, [globals, picker.query]);

  return (
    <PickerPopover
      picker={picker}
      icon={<Password size={10} />}
      chipLabel={t('properties:globals')}
      count={globals.length}
      title={t('properties:globalsTooltip', { count: globals.length })}
      placeholder={t('properties:searchGlobalVariable')}
    >
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
            onClick={() => { onPick(expression); picker.close(); }}
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
    </PickerPopover>
  );
}

/**
 * Unified target/credential field. Renders a single text input plus the standard variable,
 * global and options pickers above it. The user can type a literal ID, paste a `{{var}}`
 * expression, pick from the upstream or global pickers, or use the options picker, which
 * inserts the option's `id`.
 *
 * When the current value matches one of the options' IDs, that option's label is shown as a
 * small caption below, so the user sees which machine or credential the GUID resolves to.
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
  /** Label for the options picker button, for example "choose machine" or "choose credential". */
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
 * VariablePicker and GlobalVariablePicker styling so the row of pickers above an input
 * stays consistent.
 */
export function OptionsPicker({
  options, onPick, label,
}: Readonly<{
  options: { id: string; label: string }[];
  onPick: (id: string) => void;
  label: string;
}>) {
  const { t } = useTranslation(['properties', 'common']);
  const picker = useSearchablePicker();

  const filtered = useMemo(() => {
    const q = picker.query.trim().toLowerCase();
    if (!q) return options;
    return options.filter((o) => o.label.toLowerCase().includes(q) || o.id.toLowerCase().includes(q));
  }, [options, picker.query]);

  return (
    <PickerPopover
      picker={picker}
      icon={<TaskComplete size={10} />}
      chipLabel={t('properties:list')}
      count={options.length}
      title={t('properties:listTooltip', { label, count: options.length })}
      placeholder={t('common:searchEllipsis')}
      surfaceClass="bg-surface-container border-outline-variant/30"
    >
      {filtered.length === 0 && (
        <div className="text-[11px] font-label text-on-surface-variant px-3 py-2">{t('common:noResults')}</div>
      )}
      {filtered.map((o) => (
        <button
          key={o.id}
          type="button"
          onClick={() => { onPick(o.id); picker.close(); }}
          className="w-full flex items-center justify-between gap-2 px-3 py-1 text-left hover:bg-surface-high transition-colors"
          title={t('properties:insertVariable', { expression: o.label })}
        >
          <span className="text-xs font-label text-on-surface truncate">{o.label}</span>
          <code className="text-[9px] font-mono text-on-surface-variant truncate max-w-[6rem]" title={o.id}>{o.id.slice(0, 8)}</code>
        </button>
      ))}
    </PickerPopover>
  );
}
