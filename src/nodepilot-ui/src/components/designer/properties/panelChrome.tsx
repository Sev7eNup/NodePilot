import { ChevronDown, ChevronRight, Close, Maximize, View, ViewOff } from '@carbon/icons-react';
import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import CodeMirror from '@uiw/react-codemirror';
import { StreamLanguage } from '@codemirror/language';
import { powerShell } from '@codemirror/legacy-modes/mode/powershell';
import { standardSQL } from '@codemirror/legacy-modes/mode/sql';
import { javascript as legacyJavascript } from '@codemirror/legacy-modes/mode/javascript';
import { xml as legacyXml } from '@codemirror/legacy-modes/mode/xml';
import { EditorView, keymap } from '@codemirror/view';
import { autocompletion, type CompletionContext, type CompletionResult } from '@codemirror/autocomplete';
import { openSearchPanel } from '@codemirror/search';
import { useThemeStore, resolveTheme } from '../../../stores/themeStore';
import type { UpstreamVariable } from '../../../lib/upstreamVariables';
import { validateTemplateExpression } from '../../../lib/templateValidation';
import { hasDraggedVariableExpression, readDraggedVariableExpression } from '../../../lib/variableDragDrop';
import { ActivityIcon } from '../library/NodeLibrary';
import { getActivityLabel, VariableInsertField, VariablePicker, GlobalVariablePicker } from './shared';

/* ---- Section ----------------------------------------------------------- */

/** Visual section wrapper used throughout the PropertiesPanel. Inspector style:
 *  top-level sections separate via a full-bleed hairline and carry the same neutral
 *  grey micro-header as the palette category headers in EditorSidebar; collapse is
 *  opt-in. Accent color is reserved for interactive/active states elsewhere. */
export function Section({
  title, collapsible = false, defaultOpen = true, action, nested = false, children,
}: Readonly<{
  title: string;
  collapsible?: boolean;
  defaultOpen?: boolean;
  /** Optional action element rendered on the right side of the section header. */
  action?: ReactNode;
  /** Sub-section inside another section's content (e.g. RestApi "Proxy"): no
   *  full-bleed divider; the open content gets an indented structural rail instead. */
  nested?: boolean;
  children: ReactNode;
}>) {
  const [open, setOpen] = useState(defaultOpen);
  const isOpen = collapsible ? open : true;
  return (
    // -mx-6/px-6 stretches the hairline across the full panel width; assumes the
    // px-6 gutter of the PropertiesPanel scroll container (same coupling as PanelHeader).
    <section className={nested ? '' : 'border-t border-outline-variant/15 -mx-6 px-6'}>
      <header
        className={`flex items-center gap-1.5 py-2 ${collapsible ? 'cursor-pointer select-none rounded px-2 -mx-2 hover:bg-surface-highest/50 transition-colors' : ''}`}
        onClick={collapsible ? () => setOpen((o) => !o) : undefined}
        onKeyDown={collapsible ? (e) => (e.key === 'Enter' || e.key === ' ') && setOpen((o) => !o) : undefined}
        role={collapsible ? 'button' : undefined}
        tabIndex={collapsible ? 0 : undefined}
        aria-expanded={collapsible ? isOpen : undefined}
      >
        {collapsible && (
          isOpen
            ? <ChevronDown size={11} className="text-on-surface-variant shrink-0" />
            : <ChevronRight size={11} className="text-on-surface-variant shrink-0" />
        )}
        <h4 className="font-label text-[10px] font-bold uppercase tracking-widest text-on-surface-variant">
          {title}
        </h4>
        {action && (
          <div
            className="ml-auto"
            onClick={(e) => e.stopPropagation()}
            onKeyDown={(e) => e.stopPropagation()}
            role="presentation"
          >
            {action}
          </div>
        )}
      </header>
      {isOpen && (
        <div className={`pb-3 pt-0.5 space-y-3 ${nested ? 'pl-3 border-l border-outline-variant/30' : ''}`}>
          {children}
        </div>
      )}
    </section>
  );
}

/* ---- FieldGrid --------------------------------------------------------- */

/** 2-column grid that re-flows to 1 column when the panel narrows below ~340px,
 *  driven by container queries. The parent scroll container must declare
 *  `container-type: inline-size` (PropertiesPanel does this). */
export function FieldGrid({ children }: Readonly<{ children: ReactNode }>) {
  return (
    <div className="grid grid-cols-1 @[340px]:grid-cols-2 gap-3">
      {children}
    </div>
  );
}

/* ---- InlineEditable ---------------------------------------------------- */

/** Click-to-edit text. Looks like a heading until clicked, then swaps to an input.
 *  Blur or Enter commits, Escape reverts. */
export function InlineEditable({
  value, onChange, placeholder, className, inputClassName, ariaLabel, disabled = false,
}: Readonly<{
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  className?: string;
  inputClassName?: string;
  ariaLabel?: string;
  disabled?: boolean;
}>) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(value);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => { if (!editing) setDraft(value); }, [value, editing]);
  useEffect(() => { if (editing) inputRef.current?.select(); }, [editing]);
  // If read-only mode gets turned on while editing is open, close the input
  // field without committing the change.
  useEffect(() => { if (disabled && editing) setEditing(false); }, [disabled, editing]);

  const commit = () => {
    if (draft !== value) onChange(draft);
    setEditing(false);
  };
  const revert = () => { setDraft(value); setEditing(false); };

  if (editing && !disabled) {
    return (
      <input
        ref={inputRef}
        type="text"
        value={draft}
        aria-label={ariaLabel}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') { e.preventDefault(); commit(); }
          if (e.key === 'Escape') { e.preventDefault(); revert(); }
        }}
        className={inputClassName ?? `${className ?? ''} bg-surface-highest border border-primary/40 rounded px-1 -mx-1 outline-none focus:ring-1 focus:ring-primary/40`}
      />
    );
  }

  if (disabled) {
    return (
      <span
        className={`${className ?? ''} block px-1 -mx-1`}
        aria-label={ariaLabel}
        title="Workflow ist nicht in Bearbeitung"
      >
        {value || <span className="text-on-surface-variant italic font-normal">{placeholder ?? '—'}</span>}
      </span>
    );
  }

  return (
    <button
      type="button"
      onClick={() => setEditing(true)}
      className={`${className ?? ''} text-left hover:bg-surface-high/60 rounded px-1 -mx-1 transition-colors cursor-text`}
      title="Klicken zum Bearbeiten"
      aria-label={ariaLabel}
    >
      {value || <span className="text-on-surface-variant italic font-normal">{placeholder ?? '—'}</span>}
    </button>
  );
}

/* ---- StatusPillRow ----------------------------------------------------- */

interface StatusPillRowProps {
  showExpertControls?: boolean;
  isDisabled: boolean;
  hasBreakpoint: boolean;
  breakpointCondition: string;
  outputVariable: string;
  outputVariablePlaceholder: string;
  upstreamVars: UpstreamVariable[];
  onToggleDisabled: () => void;
  onToggleBreakpoint: () => void;
  onChangeBreakpointCondition: (v: string) => void;
  onChangeOutputVariable: (v: string) => void;
}

/** Compact horizontal row of three clickable pills replacing the three full-width
 *  Active/Breakpoint/Output-Variable sections at the bottom of the legacy panel. */
export function StatusPillRow({
  showExpertControls = true,
  isDisabled, hasBreakpoint, breakpointCondition, outputVariable, outputVariablePlaceholder,
  upstreamVars, onToggleDisabled, onToggleBreakpoint, onChangeBreakpointCondition, onChangeOutputVariable,
}: Readonly<StatusPillRowProps>) {
  const { t } = useTranslation('properties');
  const [bpOpen, setBpOpen] = useState(false);
  const [outEditing, setOutEditing] = useState(false);
  const bpRef = useRef<HTMLDivElement>(null);
  const outRef = useRef<HTMLDivElement>(null);
  const outInputRef = useRef<HTMLInputElement>(null);

  // Click-outside / Escape to close popovers.
  useEffect(() => {
    if (!bpOpen && !outEditing) return;
    const onClickOutside = (e: MouseEvent) => {
      const t = e.target as Node;
      if (bpOpen && bpRef.current && !bpRef.current.contains(t)) setBpOpen(false);
      if (outEditing && outRef.current && !outRef.current.contains(t)) setOutEditing(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { setBpOpen(false); setOutEditing(false); }
    };
    document.addEventListener('mousedown', onClickOutside);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onClickOutside);
      document.removeEventListener('keydown', onKey);
    };
  }, [bpOpen, outEditing]);

  useEffect(() => { if (outEditing) outInputRef.current?.select(); }, [outEditing]);

  const hasOutput = outputVariable.trim() !== '';
  const displayedOutput = hasOutput ? outputVariable : outputVariablePlaceholder;

  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {/* Active / Disabled — muted chip with a status dot */}
      <button
        type="button"
        onClick={onToggleDisabled}
        className="inline-flex items-center gap-1.5 h-6 px-2 rounded-full text-[10px] font-label font-semibold bg-surface-high hover:bg-surface-highest text-on-surface-variant transition-colors cursor-pointer"
        title={isDisabled
          ? 'Step ist deaktiviert — wird übersprungen. Klick aktiviert ihn.'
          : 'Step ist aktiv. Klick deaktiviert ihn (wird dann übersprungen).'}
      >
        <span className={`inline-block w-2 h-2 rounded-full shadow-[0_0_6px] ${
          isDisabled ? 'bg-on-surface-variant/40 shadow-transparent' : 'bg-emerald-400 shadow-emerald-500/60'
        }`} />
        {isDisabled ? <ViewOff size={10} /> : <View size={10} />}
        {isDisabled ? t('panel.statusDisabled') : t('panel.statusActive')}
      </button>
      {/* Breakpoint */}
      {showExpertControls && <div ref={bpRef} className="relative">
        <div className="inline-flex items-stretch h-6 rounded-full overflow-hidden text-[10px] font-label font-semibold bg-surface-high text-on-surface-variant">
          <button
            type="button"
            onClick={onToggleBreakpoint}
            className="inline-flex items-center gap-1.5 pl-2 pr-1.5 hover:bg-surface-highest transition-colors"
            title={hasBreakpoint
              ? 'Breakpoint aktiv (nur bei Debug-Run wirksam). Klick entfernt ihn.'
              : 'Klick setzt Breakpoint — pausiert nur bei Debug-Run vor diesem Step.'}
          >
            <span className={`inline-block w-2 h-2 rounded-full shadow-[0_0_6px] ${
              hasBreakpoint ? 'bg-error shadow-error/70' : 'bg-on-surface-variant/30 shadow-transparent'
            }`} />
            {hasBreakpoint ? t('panel.break') : t('panel.noBreak')}
          </button>
          {hasBreakpoint && (
            <button
              type="button"
              onClick={() => setBpOpen((o) => !o)}
              className="px-1 hover:bg-surface-highest transition-colors border-l border-outline-variant/30"
              title={breakpointCondition ? `Bedingung: ${breakpointCondition}` : 'Optional: Bedingung setzen'}
              aria-expanded={bpOpen}
            >
              <ChevronDown size={10} />
            </button>
          )}
        </div>
        {bpOpen && hasBreakpoint && (
          <div className="absolute left-0 top-full mt-1 z-30 w-72 bg-surface-container border border-outline-variant/30 rounded-md shadow-xl p-3 space-y-2">
            <VariableInsertField
              label="Breakpoint-Bedingung"
              value={breakpointCondition}
              onChange={onChangeBreakpointCondition}
              upstreamVars={upstreamVars}
              placeholder="Leer = immer pausieren · z.B. {{result.output}}"
            />
            <p className="text-[10px] font-label text-on-surface-variant leading-snug">
              Wenn gesetzt, pausiert die Engine nur wenn der aufgelöste Wert <em>truthy</em> ist
              (nicht leer, nicht <code>"false"</code>/<code>"0"</code>/<code>"no"</code>).
            </p>
          </div>
        )}
      </div>}
      {/* Output Variable — primary accent color when explicitly set */}
      <div ref={outRef} className="relative inline-flex items-center">
        <button
          type="button"
          onClick={() => setOutEditing(true)}
          className={`inline-flex items-center gap-1.5 h-6 px-2 rounded-full text-[10px] font-label font-semibold transition-colors cursor-pointer max-w-[180px] ${
            hasOutput
              ? 'bg-primary/15 text-primary hover:bg-primary/25'
              : 'bg-surface-high text-on-surface-variant hover:bg-surface-highest'
          }`}
          title={hasOutput
            ? `Downstream-Steps referenzieren als {{${outputVariable}.output}}`
            : `Default ist die Step-ID — Downstream referenziert als {{${outputVariablePlaceholder}.output}}. Klick zum Anpassen.`}
        >
          <span className={`inline-block w-2 h-2 rounded-full shadow-[0_0_6px] ${
            hasOutput ? 'bg-primary shadow-primary/60' : 'bg-on-surface-variant/30 shadow-transparent'
          }`} />
          <span className="font-mono truncate">{displayedOutput}</span>
        </button>
        {outEditing && (
          <div className="absolute left-0 top-full mt-1 z-30 w-72 bg-surface-container border border-outline-variant/30 rounded-md shadow-xl p-3 space-y-2">
            <label className="block font-label text-xs font-semibold text-on-surface-variant">
              {t('panel.outputVariableName')}
            </label>
            <input
              ref={outInputRef}
              type="text"
              value={outputVariable}
              onChange={(e) => onChangeOutputVariable(e.target.value.replaceAll(/\W/g, ''))}
              onKeyDown={(e) => {
                if (e.key === 'Enter') { e.preventDefault(); setOutEditing(false); }
              }}
              placeholder={outputVariablePlaceholder}
              className="input-field font-mono text-sm"
              autoFocus
            />
            <p className="text-[10px] font-label text-on-surface-variant leading-snug">
              Downstream-Steps referenzieren das als{' '}
              <code className="font-mono text-primary">
                {'{{' + (outputVariable || outputVariablePlaceholder) + '.output}}'}
              </code>.
              Leer lassen → Step-ID wird verwendet.
            </p>
          </div>
        )}
      </div>
    </div>
  );
}

/* ---- PanelHeader ------------------------------------------------------- */

/** Sticky header at the top of the PropertiesPanel scroll container. Combines
 *  the activity icon, inline-editable name, activity-type label, close button
 *  and the StatusPillRow into a single visual unit so the user never loses
 *  context when scrolling through long activity configs.
 *
 *  Read-only mode (`canWrite=false`): InlineEditable renders as a plain span
 *  instead of an editable field, and StatusPillRow gets wrapped in its own
 *  `<fieldset disabled>`. The close button stays active, since the user should
 *  still be able to close the panel even in read-only mode. */
export function PanelHeader({
  activityType, name, onChangeName, onClose, statusRow, canWrite = true,
}: Readonly<{
  activityType: string;
  name: string;
  onChangeName: (v: string) => void;
  onClose: () => void;
  statusRow: ReactNode;
  canWrite?: boolean;
}>) {
  const { t } = useTranslation('properties');
  return (
    <div className="sticky top-0 z-10 bg-surface-low/95 backdrop-blur border-b border-outline-variant/30 shadow-[0_2px_4px_rgba(0,0,0,0.04)] -mx-6 mb-3 px-6 py-3 space-y-2">
      <div className="flex items-start gap-3">
        <div className="w-9 h-9 rounded-md bg-surface-highest flex items-center justify-center shrink-0">
          <ActivityIcon type={activityType} size={20} />
        </div>
        <div className="flex-1 min-w-0">
          <InlineEditable
            value={name}
            onChange={onChangeName}
            placeholder={t('panel.unnamed')}
            className="block font-headline text-sm font-bold text-on-surface truncate w-full"
            ariaLabel={t('panel.nodeName')}
            disabled={!canWrite}
          />
          <p className="font-label text-[11px] text-on-surface-variant truncate">
            {getActivityLabel(activityType)}
          </p>
        </div>
        <button
          onClick={onClose}
          className="text-on-surface-variant hover:text-on-surface transition-colors focus-visible:outline-2 focus-visible:outline-primary rounded shrink-0 mt-0.5"
          aria-label={t('panel.closePanel')}
        >
          <Close size={16} aria-hidden="true" />
        </button>
      </div>
      <fieldset disabled={!canWrite} className="contents">
        {statusRow}
      </fieldset>
    </div>
  );
}

/* ---- CodeField --------------------------------------------------------- */

export type CodeLanguage = 'powershell' | 'sql' | 'json' | 'xml' | 'plain';

const LANGUAGE_EXTENSIONS = {
  powershell: StreamLanguage.define(powerShell),
  sql: StreamLanguage.define(standardSQL),
  json: StreamLanguage.define(legacyJavascript),
  xml: StreamLanguage.define(legacyXml),
  plain: null,
} as const;

/** Compact theme variant of the ScriptEditorDialog theme — sized for the
 *  ~360px-wide panel. Reads the same CSS variables so it tracks the active theme. */
function compactEditorTheme(fontSize: number) {
  return EditorView.theme({
    '&': { backgroundColor: 'var(--color-surface-low)', color: 'var(--color-on-surface)', fontSize: `${fontSize}px` },
    '.cm-scroller': { backgroundColor: 'var(--color-surface-low)' },
    '.cm-content': { fontSize: `${fontSize}px`, padding: '6px 0' },
    '.cm-gutters': { backgroundColor: 'var(--color-surface-container)', color: 'var(--color-outline)', borderRight: '1px solid var(--color-outline-variant)', fontSize: `${fontSize}px` },
    '.cm-activeLineGutter': { backgroundColor: 'var(--color-surface-high)' },
    '.cm-activeLine': { backgroundColor: 'color-mix(in srgb, var(--color-primary) 5%, transparent)' },
    '.cm-cursor': { borderLeftColor: 'var(--color-primary)' },
    '.cm-tooltip': { backgroundColor: 'var(--color-surface-lowest)', color: 'var(--color-on-surface)', border: '1px solid var(--color-outline-variant)' },
    '.cm-tooltip-autocomplete > ul > li[aria-selected]': { backgroundColor: 'var(--color-primary-fixed)', color: 'var(--color-primary)' },
    '.cm-tooltip-autocomplete > ul > li': { padding: '3px 8px' },
    '.cm-completionDetail': { color: 'var(--color-on-surface-variant)', fontStyle: 'normal', marginLeft: '1em', fontSize: '10px' },
  });
}

/** Inline CodeMirror wrapper with variable autocomplete on `{{` and the same
 *  picker buttons as VariableInsertField. Keeps the existing fullscreen
 *  ScriptEditorDialog flow intact via `onOpenFullscreen`. */
export function CodeField({
  language, value, onChange, upstreamVars, upstreamRefs,
  minLines = 12, fontSize = 12, onOpenFullscreen, fullscreenLabel,
  placeholder,
}: Readonly<{
  language: CodeLanguage;
  value: string;
  onChange: (v: string) => void;
  upstreamVars: UpstreamVariable[];
  /** Full `{{step.x}}` expressions for the CodeMirror completion source.
   *  Defaults to upstreamVars mapped to their `expression`/`label`. */
  upstreamRefs?: { expression: string; label: string }[];
  minLines?: number;
  fontSize?: number;
  onOpenFullscreen?: () => void;
  fullscreenLabel?: string;
  placeholder?: string;
}>) {
  const { t } = useTranslation('properties');
  const fullscreenText = fullscreenLabel ?? t('panel.openEditor');
  const theme = useThemeStore((s) => s.theme);
  const [dragActive, setDragActive] = useState(false);
  const isDark = resolveTheme(theme) === 'dark';
  const langExt = LANGUAGE_EXTENSIONS[language];
  const refs = upstreamRefs ?? upstreamVars.map((v) => ({ expression: v.expression, label: v.label }));

  // Shape the completion source from the live refs list. Memoised so CodeMirror
  // doesn't tear down/rebuild the autocomplete plugin on every keystroke.
  const completionSource = useCallback((ctx: CompletionContext): CompletionResult | null => {
    const match = ctx.matchBefore(/\{\{[\w.-]*/);
    if (!match) return null;
    if (match.from === match.to && !ctx.explicit) return null;
    if (refs.length === 0) return null;
    return {
      from: match.from,
      options: refs.map((v) => ({ label: v.expression, detail: v.label, type: 'variable' })),
      validFor: /^\{\{[\w.-]*$/,
    };
  }, [refs]);

  const extensions = useMemo(() => {
    const ext: ReturnType<typeof EditorView.theme>[] = [
      compactEditorTheme(fontSize) as unknown as ReturnType<typeof EditorView.theme>,
      EditorView.lineWrapping as unknown as ReturnType<typeof EditorView.theme>,
      autocompletion({ override: [completionSource], activateOnTyping: true }) as unknown as ReturnType<typeof EditorView.theme>,
      keymap.of([{ key: 'Mod-h', run: (v) => { openSearchPanel(v); return true; } }]) as unknown as ReturnType<typeof EditorView.theme>,
    ];
    if (langExt) ext.unshift(langExt as unknown as ReturnType<typeof EditorView.theme>);
    return ext;
  }, [langExt, fontSize, completionSource]);

  const insertExpression = useCallback((expression: string) => {
    onChange(value + (value && !value.endsWith(' ') && !value.endsWith('\n') ? ' ' : '') + expression);
  }, [value, onChange]);
  const validation = useMemo(
    () => validateTemplateExpression(value, upstreamVars),
    [value, upstreamVars],
  );
  const sqlTemplateWarning = language === 'sql' && value.includes('{{')
    ? t('panel.sqlTemplateWarning')
    : null;
  const handleDragOver = useCallback((event: DragEvent) => {
    if (!hasDraggedVariableExpression(event)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
    setDragActive(true);
  }, []);
  const handleDrop = useCallback((event: DragEvent) => {
    const expression = readDraggedVariableExpression(event);
    if (!expression) return;
    event.preventDefault();
    setDragActive(false);
    insertExpression(expression);
  }, [insertExpression]);

  // Min-height in px from line-count × line-height. CodeMirror's default
  // line-height is roughly fontSize × 1.4.
  const minHeightPx = Math.round(minLines * fontSize * 1.4) + 12; // +12 for padding

  return (
    <div className="space-y-1.5">
      <div className="flex flex-wrap items-center gap-1.5">
        {upstreamVars.length > 0 && (
          <VariablePicker upstreamVars={upstreamVars} onPick={insertExpression} />
        )}
        <GlobalVariablePicker onPick={insertExpression} />
        {onOpenFullscreen && (
          <button
            type="button"
            onClick={onOpenFullscreen}
            className="ml-auto inline-flex items-center gap-1 px-2 py-0.5 rounded text-[10px] font-label font-semibold text-primary hover:bg-primary-fixed transition-colors cursor-pointer"
            title={fullscreenText}
          >
            <Maximize size={10} />
            {fullscreenText}
          </button>
        )}
      </div>
      <div
        className={`border border-outline-variant/40 rounded-md overflow-hidden bg-surface-low ${dragActive ? 'ring-2 ring-primary/50' : ''}`}
        style={{ resize: 'vertical', minHeight: minHeightPx, maxHeight: 600, overflow: 'auto' }}
        onDragOver={handleDragOver}
        onDragEnter={handleDragOver}
        onDragLeave={() => setDragActive(false)}
        onDrop={handleDrop}
      >
        <CodeMirror
          value={value}
          onChange={onChange}
          theme={isDark ? 'dark' : 'light'}
          extensions={extensions}
          placeholder={placeholder}
          basicSetup={{
            lineNumbers: true,
            highlightActiveLineGutter: false,
            highlightActiveLine: false,
            foldGutter: false,
            autocompletion: false, // we install our own with custom source
            bracketMatching: true,
            closeBrackets: true,
            indentOnInput: true,
          }}
        />
      </div>
      {validation.issues.length > 0 && (
        <p className={`text-[10px] font-label leading-snug ${
          validation.status === 'error' ? 'text-error' : 'text-amber-700 dark:text-amber-300'
        }`}>
          {validation.issues.slice(0, 2).map((issue) => issue.token ? `${issue.token}: ${issue.message}` : issue.message).join(' ')}
        </p>
      )}
      {sqlTemplateWarning && (
        <p className="text-[10px] font-label leading-snug text-amber-700 dark:text-amber-300">
          {sqlTemplateWarning}
        </p>
      )}
    </div>
  );
}
