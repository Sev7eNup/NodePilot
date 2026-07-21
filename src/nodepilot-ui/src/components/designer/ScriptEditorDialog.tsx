import {
  Add,
  Checkbox,
  CheckmarkFilled,
  ChevronDown,
  ChevronUp,
  CircleDash,
  Close,
  Copy,
  DragHorizontal,
  ErrorFilled,
  Hashtag,
  MagicWandFilled,
  Maximize,
  Minimize,
  Play,
  Redo,
  Subtract,
  TextWrap,
  Undo,
} from '@carbon/icons-react';
import { useState, useCallback, useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import Editor, { type OnMount } from '@monaco-editor/react';
import { useTranslation } from 'react-i18next';
import { monaco } from '../../lib/monacoSetup';
import { useThemeStore, resolveTheme } from '../../stores/themeStore';
import { AiPromptDialog } from '../ai/AiPromptDialog';
import type { StepTestResult } from '../../types/api';

interface AvailableVar {
  name: string;
  label: string;
}

interface UpstreamRef {
  expression: string;
  label: string;
}

interface Props {
  value: string;
  onChange: (value: string) => void;
  onClose: () => void;
  onRun?: () => Promise<StepTestResult>;
  availableVars?: AvailableVar[];
  /** Full `{{step.param.X}}`-style expressions for autocomplete + validation. */
  upstreamRefs?: UpstreamRef[];
  /** Name of this step's outputVariable — shown as prefix in the Exposed panel (e.g. `collectInfo.param.hostName`). */
  outputVariableName?: string;
  title?: string;
  /**
   * When set: an AI-generate button is shown in the toolbar. The callback **streams** the
   * generated script: `onToken` is called for each token; `signal` aborts the stream
   * (cancel/stop). The prompt dialog closes immediately after "Generate"; generation happens
   * directly in the editor (a waiting indicator, then code typing in live). Errors (both
   * pre-token and mid-stream) appear as a banner in the editor. Default insert mode is "insert
   * at cursor"; the user can switch to "replace the whole editor content" in the prompt dialog.
   */
  onAiGenerate?: (prompt: string, currentScript: string, onToken: (text: string) => void, signal: AbortSignal) => Promise<void>;
}

const FONT_SIZE_KEY = 'nodepilot.scriptEditor.fontSize';
const MIN_FONT = 10;
const MAX_FONT = 22;

/**
 * Advances a 1-based {lineNumber, column} position (Monaco's convention) by the inserted text.
 * Used during AI streaming to move the next insert to the end of the chunk just written —
 * independent of `editor.getSelection()`, which is unreliable between fast programmatic edits
 * + readOnly toggling and would otherwise scramble the tokens.
 */
export function advanceStreamPosition(
  pos: { lineNumber: number; column: number },
  text: string,
): { lineNumber: number; column: number } {
  const lastNl = text.lastIndexOf('\n');
  if (lastNl < 0) return { lineNumber: pos.lineNumber, column: pos.column + text.length };
  const newlineCount = text.split('\n').length - 1;
  return { lineNumber: pos.lineNumber + newlineCount, column: text.length - lastNl }; // column = characters after the last \n, plus 1
}

const THEME_DARK = 'nodepilot-dark';
const THEME_LIGHT = 'nodepilot-light';

// --- Theme bridge --------------------------------------------------------------
// Monaco themes are static — they can't read CSS vars. We resolve the relevant
// vars from the live designer scope/root and feed them into defineTheme. Re-runs
// on theme switch pick up the new values.

function readVar(name: string, fallback: string): string {
  if (typeof document === 'undefined') return fallback;
  const scopes = [
    document.querySelector('.np-designer'),
    document.documentElement,
  ].filter((scope): scope is Element => scope !== null);
  for (const scope of scopes) {
    const raw = getComputedStyle(scope).getPropertyValue(name).trim();
    if (raw.startsWith('#')) return raw;
  }
  return fallback;
}

function defineNodePilotThemes() {
  const lightColors = {
    editorSurface: readVar('--color-surface-low', '#f3f4f6'),
    gutterSurface: readVar('--color-surface-container', '#edeef0'),
    onSurface: readVar('--color-on-surface', '#191c1e'),
    primary: readVar('--color-primary', '#004ac6'),
    outline: readVar('--color-outline', '#737686'),
  };
  // Dark reads the SAME runtime tokens as light (readVar resolves against the live
  // scope, which under html.dark carries the dark values) — the previous hardcoded
  // dark object ignored skins and token updates.
  const darkColors = {
    surfaceLowest: readVar('--color-surface-lowest', '#111214'),
    surfaceLow: readVar('--color-surface-low', '#1e2024'),
    onSurface: readVar('--color-on-surface', '#e2e2e6'),
    primary: readVar('--color-primary', '#aac7ff'),
    outline: readVar('--color-outline', '#8e9099'),
  };

  monaco.editor.defineTheme(THEME_LIGHT, {
    base: 'vs',
    inherit: true,
    rules: [
      { token: 'variable.predefined.powershell', foreground: '0451A5' },
      { token: 'variable.powershell', foreground: '0070C1' },
      { token: 'type.powershell', foreground: '267F99' },
      { token: 'string.powershell', foreground: 'A31515' },
      { token: 'comment.powershell', foreground: '008000', fontStyle: 'italic' },
      { token: 'keyword.powershell', foreground: 'AF00DB' },
      { token: 'operator.powershell', foreground: '6F6F6F' },
      { token: 'number.powershell', foreground: '098658' },
    ],
    colors: {
      'editor.background': lightColors.editorSurface,
      'editor.foreground': lightColors.onSurface,
      'editorGutter.background': lightColors.gutterSurface,
      'editorLineNumber.foreground': lightColors.outline,
      'editorLineNumber.activeForeground': lightColors.primary,
      'editor.selectionBackground': lightColors.primary + '33',
      'editor.lineHighlightBackground': lightColors.primary + '0d',
    },
  });

  monaco.editor.defineTheme(THEME_DARK, {
    base: 'vs-dark',
    inherit: true,
    rules: [
      { token: 'variable.predefined.powershell', foreground: '4FC1FF' },
      { token: 'variable.powershell', foreground: '9CDCFE' },
      { token: 'type.powershell', foreground: '4EC9B0' },
      { token: 'string.powershell', foreground: 'CE9178' },
      { token: 'comment.powershell', foreground: '6A9955', fontStyle: 'italic' },
      { token: 'keyword.powershell', foreground: 'C586C0' },
      { token: 'operator.powershell', foreground: 'D4D4D4' },
      { token: 'number.powershell', foreground: 'B5CEA8' },
    ],
    colors: {
      'editor.background': darkColors.surfaceLowest,
      'editor.foreground': darkColors.onSurface,
      'editorGutter.background': darkColors.surfaceLow,
      'editorLineNumber.foreground': darkColors.outline,
      'editorLineNumber.activeForeground': darkColors.primary,
      'editor.selectionBackground': darkColors.primary + '40',
      'editor.lineHighlightBackground': darkColors.primary + '14',
    },
  });
}

// --- Exposed-variable parser ---------------------------------------------------
// Matches `$foo = ...` on a line (ignores `$foo.bar = ...`, `$foo -eq ...`, etc.).
// PowerShell is case-preserving but case-insensitive; we keep the original casing.
const ASSIGN_RE = /^\s*\$([A-Za-z_]\w*)\s*=(?!=)/gm;

function parseExposedVars(code: string): string[] {
  const found = new Set<string>();
  ASSIGN_RE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = ASSIGN_RE.exec(code)) !== null) {
    found.add(m[1]);
  }
  return [...found];
}

export function ScriptEditorDialog({
  value, onChange, onClose, onRun, availableVars = [], upstreamRefs = [], outputVariableName, title = 'PowerShell Script Editor', onAiGenerate,
}: Readonly<Props>) {
  const { t } = useTranslation(['ai', 'editor']);
  const aiDialogTitle = t('ai:scriptDialog.title');
  const aiDialogSubtitle = t('ai:scriptDialog.promptLabel');
  const aiDialogPlaceholder = t('ai:scriptDialog.promptPlaceholder');
  const aiDialogSubmit = t('ai:scriptDialog.generate');
  const [code, setCode] = useState(value);
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [wordWrap, setWordWrap] = useState(true);
  const [fontSize, setFontSize] = useState<number>(() => {
    const raw = typeof window !== 'undefined' ? globalThis.localStorage.getItem(FONT_SIZE_KEY) : null;
    const n = raw ? parseInt(raw, 10) : Number.NaN;
    return Number.isFinite(n) && n >= MIN_FONT && n <= MAX_FONT ? n : 13;
  });
  const [testResult, setTestResult] = useState<StepTestResult | null>(null);
  const [testing, setTesting] = useState(false);
  const [resultCollapsed, setResultCollapsed] = useState(false);
  const [aiDialogOpen, setAiDialogOpen] = useState(false);
  const [aiError, setAiError] = useState<string | null>(null);
  const [aiPhase, setAiPhase] = useState<'idle' | 'waiting' | 'streaming'>('idle');
  const aiAbortRef = useRef<AbortController | null>(null);
  const aiBusy = aiPhase !== 'idle';

  // Window geometry — centered horizontally + a bit above vertical center, then the
  // user can drag/resize freely.
  const DEFAULT_W = 1280;
  const DEFAULT_H = 800;
  const MIN_W = 640;
  const MIN_H = 420;
  const [pos, setPos] = useState<{ x: number; y: number }>(() => ({
    x: typeof window !== 'undefined' ? Math.max(0, (globalThis.innerWidth - DEFAULT_W) / 2) : 0,
    // Sit at ~32% of the free vertical gap (not 0.5) so the window reads a touch higher.
    y: typeof window !== 'undefined' ? Math.max(8, (globalThis.innerHeight - DEFAULT_H) * 0.32) : 8,
  }));
  const [size, setSize] = useState<{ w: number; h: number }>(() => ({
    w: typeof window !== 'undefined' ? Math.min(DEFAULT_W, globalThis.innerWidth * 0.95) : DEFAULT_W,
    h: typeof window !== 'undefined' ? Math.min(DEFAULT_H, globalThis.innerHeight * 0.92) : DEFAULT_H,
  }));
  const dragState = useRef<{ startX: number; startY: number; posX: number; posY: number } | null>(null);
  const resizeState = useRef<{ startX: number; startY: number; startW: number; startH: number } | null>(null);
  const editorRef = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);

  useEffect(() => {
    try { globalThis.localStorage.setItem(FONT_SIZE_KEY, String(fontSize)); } catch { /* quota / private */ }
  }, [fontSize]);

  // Abort the AI stream on unmount (dialog closing / switching workflow).
  useEffect(() => () => aiAbortRef.current?.abort(), []);

  // --- Drag (title bar) ---
  const handleTitleMouseDown = useCallback((e: React.MouseEvent) => {
    if (isFullscreen) return;
    if ((e.target as HTMLElement).closest('button')) return;
    e.preventDefault();
    dragState.current = { startX: e.clientX, startY: e.clientY, posX: pos.x, posY: pos.y };
    const onMove = (m: MouseEvent) => {
      const d = dragState.current;
      if (!d) return;
      const nextX = d.posX + (m.clientX - d.startX);
      const nextY = d.posY + (m.clientY - d.startY);
      const clampedX = Math.max(-size.w + 120, Math.min(globalThis.innerWidth - 80, nextX));
      const clampedY = Math.max(0, Math.min(globalThis.innerHeight - 40, nextY));
      setPos({ x: clampedX, y: clampedY });
    };
    const onUp = () => {
      dragState.current = null;
      globalThis.removeEventListener('mousemove', onMove);
      globalThis.removeEventListener('mouseup', onUp);
    };
    globalThis.addEventListener('mousemove', onMove);
    globalThis.addEventListener('mouseup', onUp);
  }, [pos.x, pos.y, size.w, isFullscreen]);

  // --- Resize (bottom-right handle) ---
  const handleResizeMouseDown = useCallback((e: React.MouseEvent) => {
    if (isFullscreen) return;
    e.preventDefault();
    e.stopPropagation();
    resizeState.current = { startX: e.clientX, startY: e.clientY, startW: size.w, startH: size.h };
    const onMove = (m: MouseEvent) => {
      const r = resizeState.current;
      if (!r) return;
      const nextW = Math.max(MIN_W, Math.min(globalThis.innerWidth - pos.x, r.startW + (m.clientX - r.startX)));
      const nextH = Math.max(MIN_H, Math.min(globalThis.innerHeight - pos.y, r.startH + (m.clientY - r.startY)));
      setSize({ w: nextW, h: nextH });
    };
    const onUp = () => {
      resizeState.current = null;
      globalThis.removeEventListener('mousemove', onMove);
      globalThis.removeEventListener('mouseup', onUp);
    };
    globalThis.addEventListener('mousemove', onMove);
    globalThis.addEventListener('mouseup', onUp);
  }, [size.w, size.h, pos.x, pos.y, isFullscreen]);

  const handleToggleComment = useCallback(() => {
    const editor = editorRef.current;
    if (!editor) return;
    editor.getAction('editor.action.commentLine')?.run();
    editor.focus();
  }, []);

  const handleSave = useCallback(() => {
    onChange(code);
    onClose();
  }, [code, onChange, onClose]);

  const handleRun = useCallback(async () => {
    if (!onRun || testing) return;
    onChange(code);
    setTesting(true);
    setTestResult(null);
    setResultCollapsed(false);
    try {
      const res = await onRun();
      setTestResult(res);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setTestResult({ success: false, output: null, errorOutput: msg, outputParameters: {}, durationMs: 0, errorMessage: msg });
    } finally {
      setTesting(false);
    }
  }, [onRun, code, onChange, testing]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (e.key === 's' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      onChange(code);
    }
    if (e.key === 'Escape') onClose();
  }, [code, onChange, onClose]);

  const insertText = useCallback((text: string) => {
    const editor = editorRef.current;
    if (!editor) {
      setCode((prev) => prev + text);
      return;
    }
    const selection = editor.getSelection();
    const range = selection ?? new monaco.Range(1, 1, 1, 1);
    editor.executeEdits('var-insert', [{ range, text, forceMoveMarkers: true }]);
    editor.focus();
  }, []);

  /**
   * AI submit (streaming): closes the prompt dialog on the first token (so the editor becomes
   * visible) and types the script live into Monaco. Tokens are batched per
   * `requestAnimationFrame` (one `executeEdits` per frame), and the entire generation is ONE
   * undo group (`pushUndoStop` before/after). The editor is read-only during the stream, so the
   * user typing in parallel doesn't interleave with the AI output. ReplaceAll only clears the
   * editor on the first token (so a pre-token error doesn't lose the old content). Cancelled via
   * `signal` (Stop/Close).
   */
  const handleAiSubmit = useCallback(async (prompt: string, replaceAll: boolean) => {
    if (!onAiGenerate) return;
    if (aiAbortRef.current) return; // a generation is already running → no second stream
    const editor = editorRef.current;
    const ac = new AbortController();
    aiAbortRef.current = ac;
    setAiError(null);
    // Immediately: close the dialog, switch focus to the editor, show the waiting indicator.
    setAiDialogOpen(false);
    setAiPhase('waiting');

    let firstToken = true;
    let pending = '';
    let rafScheduled = false;
    // An explicit, monotonically advancing insert position. Do NOT use
    // `editor.getSelection()`: between the fast stream edits, the selection is unreliable after
    // readOnly toggling/executeEdits/focus — otherwise the tokens would land scrambled.
    let insertPos = { lineNumber: 1, column: 1 };

    // executeEdits is a no-op on a read-only editor → lift readOnly only for the
    // synchronous, programmatic write, and restore it via try/finally so it's guaranteed.
    const withWritable = (fn: () => void) => {
      if (!editor) return;
      editor.updateOptions({ readOnly: false });
      try { fn(); } finally { editor.updateOptions({ readOnly: true }); }
    };

    const flush = () => {
      rafScheduled = false;
      if (!pending) return;
      const text = pending;
      pending = '';
      if (!editor || !editor.getModel()) { setCode((prev) => prev + text); return; } // Fallback (no Monaco / in tests)
      const range = new monaco.Range(insertPos.lineNumber, insertPos.column, insertPos.lineNumber, insertPos.column);
      withWritable(() => editor.executeEdits('ai-stream', [{ range, text, forceMoveMarkers: true }]));
      insertPos = advanceStreamPosition(insertPos, text);
      editor.setPosition(insertPos);                              // Cursor follows → auto-scroll
      editor.revealPositionInCenterIfOutsideViewport(insertPos);
    };
    const schedule = () => {
      if (rafScheduled) return;
      rafScheduled = true;
      (globalThis.requestAnimationFrame ?? ((cb: FrameRequestCallback) => globalThis.setTimeout(() => cb(0), 16)))(flush);
    };

    const onToken = (text: string) => {
      if (firstToken) {
        firstToken = false;
        setAiPhase('streaming');
        if (editor) {
          editor.updateOptions({ readOnly: true });
          editor.pushUndoStop(); // Start of the undo block
          if (replaceAll) {
            // Clear via executeEdits over the full range → stays in the SAME undo
            // block as the inserts (setValue is model-level and would break the editor's undo
            // grouping). Starts at {1,1}.
            withWritable(() => {
              const model = editor.getModel();
              if (model) editor.executeEdits('ai-clear', [{ range: model.getFullModelRange(), text: '' }]);
            });
            setCode('');
            insertPos = { lineNumber: 1, column: 1 };
          } else {
            // Insert mode: start once at the current cursor end-position, then only `advance`.
            const sel = editor.getSelection();
            insertPos = sel
              ? { lineNumber: sel.endLineNumber, column: sel.endColumn }
              : { lineNumber: 1, column: 1 };
          }
        } else if (replaceAll) {
          setCode('');
        }
      }
      pending += text;
      schedule();
    };

    const cleanup = () => {
      flush();
      if (editor) {
        editor.updateOptions({ readOnly: false });
        editor.pushUndoStop(); // End of the undo block → the whole generation is a single undo step
        editor.focus();
      }
      aiAbortRef.current = null;
      setAiPhase('idle');
    };

    try {
      // Send the current editor content along, so "refactor/fix the script" has a starting point.
      const currentScript = editor?.getValue() ?? code;
      await onAiGenerate(prompt, currentScript, onToken, ac.signal);
      cleanup();
    } catch (err: unknown) {
      const aborted = (err instanceof DOMException || err instanceof Error) && err.name === 'AbortError';
      cleanup();
      // Dialog is already closed → both pre-token and mid-stream errors show up as an
      // editor banner. A cancel (Stop/Cancel) is not an error — the partial content stays.
      if (!aborted) setAiError(err instanceof Error ? err.message : String(err));
    }
  }, [onAiGenerate, code]);

  const handleAiStop = useCallback(() => aiAbortRef.current?.abort(), []);

  const theme = useThemeStore((s) => s.theme);
  const isDark = resolveTheme(theme) === 'dark';
  const monacoTheme = isDark ? THEME_DARK : THEME_LIGHT;

  // Re-define themes when the app theme switches so `editor.background` / `selectionBackground`
  // pick up the freshly resolved CSS-var values (handles user-customised tokens).
  useEffect(() => {
    defineNodePilotThemes();
  }, [isDark]);

  // Variable-completion provider: triggers on `{{` and offers all upstream refs.
  // Registered per-mount so each dialog session gets a fresh provider scoped to its own upstreamRefs closure.
  useEffect(() => {
    const disposable = monaco.languages.registerCompletionItemProvider('powershell', {
      triggerCharacters: ['{'],
      provideCompletionItems: (model, position) => {
        const line = model.getLineContent(position.lineNumber);
        const prefix = line.slice(0, position.column - 1);
        const m = prefix.match(/\{\{[\w.-]*$/);
        if (!m || upstreamRefs.length === 0) return { suggestions: [] };
        const startCol = position.column - m[0].length;
        const word = model.getWordUntilPosition(position);
        return {
          suggestions: upstreamRefs.map((v) => ({
            label: v.expression,
            kind: monaco.languages.CompletionItemKind.Variable,
            insertText: v.expression,
            detail: v.label,
            range: new monaco.Range(position.lineNumber, startCol, position.lineNumber, word.endColumn),
          })),
        };
      },
    });
    return () => disposable.dispose();
  }, [upstreamRefs]);

  // Linter: warn on `{{...}}` that doesn't match any known upstream expression or `{{globals.*}}`.
  useEffect(() => {
    const editor = editorRef.current;
    if (!editor) return;
    const model = editor.getModel();
    if (!model) return;
    const known = new Set(upstreamRefs.map((v) => v.expression));
    const text = model.getValue();
    const markers: monaco.editor.IMarkerData[] = [];
    const re = /\{\{([^{}]+?)\}\}/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(text)) !== null) {
      const inner = m[1].trim();
      if (inner.startsWith('globals.')) continue;
      if (known.has(m[0])) continue;
      const start = model.getPositionAt(m.index);
      const end = model.getPositionAt(m.index + m[0].length);
      markers.push({
        severity: monaco.MarkerSeverity.Warning,
        message: `"${inner}" ist in diesem Step nicht als Upstream-Variable verfügbar. Prüfe Schreibweise und ob der vorherige Step die Variable wirklich exponiert.`,
        startLineNumber: start.lineNumber, startColumn: start.column,
        endLineNumber: end.lineNumber, endColumn: end.column,
      });
    }
    monaco.editor.setModelMarkers(model, 'nodepilot-vars', markers);
  }, [code, upstreamRefs]);

  const handleEditorMount: OnMount = useCallback((editor) => {
    editorRef.current = editor;
    defineNodePilotThemes();
    // Ctrl+S → flush current buffer to onChange (no close — same semantics as the previous handler).
    // We use editor.getValue() so the latest text is captured even if React state is mid-flush.
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
      onChange(editor.getValue());
    });
  }, [onChange]);

  const exposedVars = parseExposedVars(code);
  const exposedPrefix = outputVariableName?.trim() ? outputVariableName.trim() : '<step>';

  // Portal auf document.body: der Dialog rendert sonst im Stacking-Kontext des
  // Properties-Panels (Flex-Item mit z-10) — Header (z-45), Sidebar und die
  // Canvas-Float-Pills (z-30/40) zeichnen dann ÜBER dem Backdrop, und ein
  // transformierter/contain-ender Vorfahre könnte das fixed-Overlay einsperren.
  // `.np-tooltip-portal` re-assertet die Skin-Tokens außerhalb von .np-designer
  // (gleiches Muster wie der Node-Tooltip).
  return createPortal(
    <div
      className="np-tooltip-portal fixed inset-0 z-50 bg-black/20 backdrop-blur-sm"
      onKeyDown={handleKeyDown}
      role="dialog"
      aria-modal="true"
      aria-labelledby="script-editor-title"
      data-nodepilot-script-editor-dialog="true"
    >
      <div
        className="absolute bg-surface-lowest rounded-xl shadow-2xl ring-1 ring-outline-variant/20 flex flex-col overflow-hidden"
        style={isFullscreen
          ? { left: 0, top: 0, width: '100%', height: '100%', borderRadius: 0 }
          : { left: pos.x, top: pos.y, width: size.w, height: size.h }}
      >

        {/* Title bar (drag handle) */}
        <div
          className={`flex items-center justify-between px-4 py-2.5 bg-surface-low border-b border-outline-variant/20 shrink-0 select-none ${isFullscreen ? '' : 'cursor-move'}`}
          onMouseDown={handleTitleMouseDown}
        >
          <div className="flex items-center gap-3">
            <span className="px-1.5 py-0.5 bg-primary-fixed text-primary text-[10px] font-mono font-bold rounded">PS</span>
            <span id="script-editor-title" className="text-on-surface text-sm font-headline font-bold">{title}</span>
          </div>
          <div className="flex items-center gap-1">
            <button
              onClick={() => setIsFullscreen(!isFullscreen)}
              className="p-1.5 text-on-surface-variant hover:text-on-surface hover:bg-surface-highest rounded transition-colors focus-visible:outline-2 focus-visible:outline-primary"
              title={t('editor:scriptEditor.toggleFullscreen')}
              aria-label={t('editor:scriptEditor.toggleFullscreen')}
            >
              {isFullscreen ? <Minimize size={14} /> : <Maximize size={14} />}
            </button>
            <button
              onClick={onClose}
              className="p-1.5 text-on-surface-variant hover:text-error hover:bg-error-container/30 rounded transition-colors focus-visible:outline-2 focus-visible:outline-primary"
              aria-label={t('editor:scriptEditor.close')}
            >
              <Close size={14} />
            </button>
          </div>
        </div>

        {/* Toolbar */}
        <div className="flex items-center justify-between px-3 py-1.5 bg-surface border-b border-outline-variant/15 shrink-0">
          <div className="flex items-center gap-0.5">
            <ToolbarButton icon={Undo} label={t('editor:scriptEditor.undo')} onClick={() => editorRef.current?.trigger('toolbar', 'undo', null)} />
            <ToolbarButton icon={Redo} label={t('editor:scriptEditor.redo')} onClick={() => editorRef.current?.trigger('toolbar', 'redo', null)} />
            <div className="w-px h-4 bg-outline-variant/30 mx-1.5" />
            <ToolbarButton icon={TextWrap} label={t('editor:scriptEditor.wordWrap')} onClick={() => setWordWrap(!wordWrap)} active={wordWrap} />
            <ToolbarButton icon={Hashtag} label="Zeile kommentieren / entkommentieren (Ctrl+/)" onClick={handleToggleComment} />
            <ToolbarButton icon={Copy} label={t('editor:scriptEditor.copyAll')} onClick={() => navigator.clipboard.writeText(code)} />
            <div className="w-px h-4 bg-outline-variant/30 mx-1.5" />
            <ToolbarButton
              icon={Subtract}
              label={t('editor:scriptEditor.smaller', { size: fontSize })}
              onClick={() => setFontSize((s) => Math.max(MIN_FONT, s - 1))}
              disabled={fontSize <= MIN_FONT}
            />
            <span className="px-1 text-[10px] font-mono text-on-surface-variant tabular-nums select-none" title={t('editor:scriptEditor.fontSize')}>{fontSize}</span>
            <ToolbarButton
              icon={Add}
              label={t('editor:scriptEditor.larger', { size: fontSize })}
              onClick={() => setFontSize((s) => Math.min(MAX_FONT, s + 1))}
              disabled={fontSize >= MAX_FONT}
            />
          </div>
          <div className="flex items-center gap-2">
            {onAiGenerate && (
              <button
                onClick={() => setAiDialogOpen(true)}
                disabled={aiBusy}
                className="flex items-center gap-1.5 px-3 py-1 bg-gradient-to-br from-primary to-primary-container text-on-primary text-xs font-label font-medium rounded-md shadow-sm hover:shadow-lg hover:brightness-110 hover:-translate-y-px active:translate-y-0 active:brightness-95 transition-all cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed disabled:hover:brightness-100 disabled:hover:translate-y-0 disabled:hover:shadow-sm"
                title={aiDialogTitle}
                aria-label={t('editor:scriptEditor.generateWithAi')}
              >
                <MagicWandFilled size={12} />
                KI
              </button>
            )}
            {onRun && (
              <button
                onClick={handleRun}
                disabled={testing || aiBusy}
                className="flex items-center gap-1.5 px-3 py-1 bg-green-600 text-white text-xs font-label font-medium rounded-md shadow-sm hover:shadow-lg hover:brightness-110 hover:-translate-y-px active:translate-y-0 active:brightness-95 transition-all cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed disabled:hover:brightness-100 disabled:hover:translate-y-0 disabled:hover:shadow-sm"
                title="Step testen (nutzt den zuletzt gespeicherten Stand aus der DB)"
              >
                {testing ? <CircleDash size={12} className="animate-spin" /> : <Play size={12} />}
                {testing ? t('editor:scriptEditor.running') : t('editor:scriptEditor.run')}
              </button>
            )}
            <button onClick={handleSave} disabled={aiBusy} className="flex items-center gap-1.5 px-4 py-1.5 bg-gradient-to-br from-primary to-primary-container text-on-primary text-xs font-label font-semibold rounded-md shadow-sm hover:shadow-lg hover:brightness-110 hover:-translate-y-px active:translate-y-0 active:brightness-95 transition-all cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed disabled:hover:brightness-100 disabled:hover:translate-y-0 disabled:hover:shadow-sm">
              {t('editor:scriptEditor.saveClose')}
            </button>
          </div>
        </div>

        {aiError && (
          <div role="alert" className="flex items-center justify-between gap-2 bg-error-container/30 border-b border-error/30 px-3 py-1.5 text-xs text-on-error-container">
            <span className="truncate">{t('ai:scriptDialog.errorPrefix', { message: aiError })}</span>
            <button onClick={() => setAiError(null)} className="shrink-0 hover:text-error" aria-label={t('common:close')}>
              <Close size={13} />
            </button>
          </div>
        )}

        <div className="flex flex-1 overflow-hidden">
          {/* Variables sidebar */}
          {(availableVars.length > 0 || exposedVars.length > 0) && (
            <div className="w-52 bg-surface-low border-r border-outline-variant/15 flex flex-col shrink-0 overflow-hidden">
              {availableVars.length > 0 && (
                <>
                  <div className="px-3 pt-2.5 pb-1.5 text-[10px] font-label font-bold text-on-surface-variant uppercase tracking-widest border-b border-outline-variant/10">
                    {t('editor:scriptEditor.variablesUpstream')}
                  </div>
                  <div className="flex-1 overflow-y-auto px-2 pt-1 pb-2 space-y-0.5 min-h-0">
                    {availableVars.map((v) => (
                      <button
                        key={v.name}
                        onClick={() => insertText(v.name)}
                        disabled={aiBusy}
                        className="w-full text-left px-2.5 py-2 rounded-md text-xs font-mono text-primary hover:bg-primary-fixed transition-colors disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:bg-transparent"
                        title={t('editor:scriptEditor.insertVar', { name: v.name, label: v.label })}
                      >
                        {v.name}
                        <span className="block text-[9px] text-on-surface-variant font-label truncate mt-0.5">{v.label}</span>
                      </button>
                    ))}
                  </div>
                </>
              )}

              {exposedVars.length > 0 && (
                <>
                  <div className="px-3 pt-1.5 pb-1.5 mt-3 text-[10px] font-label font-bold text-on-surface-variant uppercase tracking-widest border-y border-outline-variant/10 bg-surface-container/40">
                    {t('editor:scriptEditor.exposedDownstream')}
                  </div>
                  <div className="overflow-y-auto px-2 pt-1 pb-2 space-y-0.5 max-h-40 shrink-0">
                    {exposedVars.map((name) => (
                      <div
                        key={name}
                        className="px-2.5 py-1.5 rounded-md"
                        title={`Downstream-Steps können dies als {{${exposedPrefix}.param.${name}}} referenzieren.`}
                      >
                        <div className="text-xs font-mono text-green-700 truncate">${name}</div>
                        <div className="text-[9px] text-on-surface-variant font-mono truncate">→ {`{{${exposedPrefix}.param.${name}}}`}</div>
                      </div>
                    ))}
                  </div>
                </>
              )}

              <div className="border-t border-outline-variant/10 px-2.5 py-2 text-[9px] leading-snug text-on-surface-variant font-label mt-auto">
                <div className="mb-1"><strong className="text-amber-700">Auto-Quoting:</strong> <code className="text-[9px]">{'{{var}}'}</code> wird zur Laufzeit als <code className="text-[9px]">'wert'</code> eingesetzt — keine Quotes drumherum schreiben.</div>
                <div className="opacity-80">{t('editor:scriptEditor.shortcutsLabel')} Ctrl+F Suchen · Ctrl+H Ersetzen · Ctrl+G Zeile · Ctrl+/ Kommentar · Ctrl+Space Autocomplete</div>
              </div>
            </div>
          )}

          {/* Code editor + result panel */}
          <div className="flex-1 flex flex-col overflow-hidden">
            <div className="relative flex-1 overflow-hidden min-h-0">
              {/* Waiting indicator: from clicking Generate until the first token arrives. Covers the old content. */}
              {aiPhase === 'waiting' && (
                <div className="absolute inset-0 z-20 flex flex-col items-center justify-center gap-3 bg-surface/70 backdrop-blur-sm">
                  <div className="rounded-2xl bg-primary-fixed p-3 text-primary shadow-sm">
                    <MagicWandFilled size={24} className="animate-pulse" />
                  </div>
                  <div className="flex items-center text-sm font-label font-medium text-on-surface">
                    {t('ai:scriptDialog.generatingInEditor')}
                    <span className="ml-1 inline-flex">
                      <span className="animate-bounce [animation-delay:-0.2s]">.</span>
                      <span className="animate-bounce [animation-delay:-0.1s]">.</span>
                      <span className="animate-bounce">.</span>
                    </span>
                  </div>
                  <button
                    onClick={handleAiStop}
                    className="rounded-md px-3 py-1 text-xs font-label font-semibold text-on-surface-variant transition-colors hover:bg-surface-high hover:text-on-surface"
                  >
                    {t('ai:scriptDialog.cancel')}
                  </button>
                </div>
              )}
              {/* Streaming: a subtle pill in the top-right so the incoming code stays readable. */}
              {aiPhase === 'streaming' && (
                <div className="absolute right-3 top-3 z-20 flex items-center gap-1.5 rounded-full bg-primary px-2.5 py-1 text-[11px] font-label font-semibold text-on-primary shadow-lg">
                  <CircleDash size={12} className="animate-spin" />
                  {t('ai:scriptDialog.streamingBadge')}
                  <button
                    onClick={handleAiStop}
                    className="ml-1 rounded-full p-0.5 transition-colors hover:bg-white/20"
                    aria-label={t('ai:scriptDialog.stop')}
                    title={t('ai:scriptDialog.stop')}
                  >
                    <Checkbox size={10} className="fill-current" />
                  </button>
                </div>
              )}
              <Editor
                language="powershell"
                theme={monacoTheme}
                value={code}
                onChange={(v) => setCode(v ?? '')}
                onMount={handleEditorMount}
                height="100%"
                options={{
                  fontSize,
                  wordWrap: wordWrap ? 'on' : 'off',
                  lineNumbers: 'on',
                  folding: true,
                  bracketPairColorization: { enabled: true },
                  automaticLayout: true,
                  minimap: { enabled: false },
                  scrollBeyondLastLine: false,
                  renderWhitespace: 'selection',
                  smoothScrolling: true,
                  fixedOverflowWidgets: true,
                }}
              />
            </div>

            {(testResult || testing) && (
              <div className="border-t border-outline-variant/20 bg-surface-low shrink-0 flex flex-col" style={{ maxHeight: '40%' }}>
                <div className="flex items-center justify-between px-3 py-1.5 border-b border-outline-variant/15 shrink-0">
                  <div className="flex items-center gap-2">
                    {testing && <CircleDash size={12} className="animate-spin text-primary" />}
                    {!testing && testResult?.success && <CheckmarkFilled size={12} className="text-green-600" />}
                    {!testing && testResult && !testResult.success && <ErrorFilled size={12} className="text-error" />}
                    <span className="text-xs font-label font-semibold text-on-surface">
                      {testing ? t('editor:scriptEditor.runningStep') : testResult?.success ? t('editor:scriptEditor.stepSucceeded') : t('editor:scriptEditor.stepFailed')}
                    </span>
                    {testResult && !testing && (
                      <span className="text-[10px] font-mono text-on-surface-variant tabular-nums">
                        {testResult.durationMs.toFixed(0)} ms
                      </span>
                    )}
                  </div>
                  <div className="flex items-center gap-1">
                    <button
                      onClick={() => setResultCollapsed((c) => !c)}
                      className="p-1 text-on-surface-variant hover:text-on-surface hover:bg-surface-high rounded transition-colors"
                      title={resultCollapsed ? t('editor:scriptEditor.expand') : t('editor:scriptEditor.collapse')}
                    >
                      {resultCollapsed ? <ChevronUp size={12} /> : <ChevronDown size={12} />}
                    </button>
                    <button
                      onClick={() => { setTestResult(null); setResultCollapsed(false); }}
                      className="p-1 text-on-surface-variant hover:text-error hover:bg-error-container/30 rounded transition-colors"
                      title={t('editor:scriptEditor.closeResultPanel')}
                      aria-label={t('editor:scriptEditor.closeResultPanel')}
                    >
                      <Close size={12} />
                    </button>
                  </div>
                </div>

                {!resultCollapsed && testResult && (
                  <div className="overflow-y-auto px-3 py-2 space-y-2 text-[11px] font-mono">
                    {testResult.errorMessage && (
                      <div className="bg-error-container/20 border border-error/30 rounded px-2 py-1.5 text-on-error-container font-label whitespace-pre-wrap">
                        <span className="font-semibold">{t('editor:scriptEditor.errorPrefix')}</span>{testResult.errorMessage}
                      </div>
                    )}
                    {testResult.output && (
                      <div>
                        <div className="text-[9px] font-label font-bold text-on-surface-variant uppercase tracking-widest mb-0.5">stdout</div>
                        <pre className="bg-surface-container rounded px-2 py-1.5 whitespace-pre-wrap text-on-surface max-h-40 overflow-y-auto">{testResult.output}</pre>
                      </div>
                    )}
                    {testResult.errorOutput && (
                      <div>
                        <div className="text-[9px] font-label font-bold text-error uppercase tracking-widest mb-0.5">stderr</div>
                        <pre className="bg-error-container/10 rounded px-2 py-1.5 whitespace-pre-wrap text-error max-h-32 overflow-y-auto">{testResult.errorOutput}</pre>
                      </div>
                    )}
                    {Object.keys(testResult.outputParameters).length > 0 && (
                      <div>
                        <div className="text-[9px] font-label font-bold text-on-surface-variant uppercase tracking-widest mb-0.5">{t('editor:scriptEditor.exposedParams', { count: Object.keys(testResult.outputParameters).length })}</div>
                        <div className="bg-surface-container rounded px-2 py-1.5 space-y-0.5 max-h-32 overflow-y-auto">
                          {Object.entries(testResult.outputParameters).map(([k, v]) => (
                            <div key={k} className="flex gap-2">
                              <span className="text-primary shrink-0">{k}</span>
                              <span className="text-on-surface-variant">=</span>
                              <span className="text-on-surface truncate" title={v}>{v}</span>
                            </div>
                          ))}
                        </div>
                      </div>
                    )}
                    {!testResult.output && !testResult.errorOutput && !testResult.errorMessage && Object.keys(testResult.outputParameters).length === 0 && (
                      <div className="text-on-surface-variant font-label italic">Kein Output.</div>
                    )}
                  </div>
                )}
              </div>
            )}
          </div>
        </div>

        {/* Status bar */}
        <div className="flex items-center justify-between px-4 py-1 bg-primary text-on-primary text-[10px] font-label shrink-0">
          <div className="flex items-center gap-4">
            <span>PowerShell</span>
            <span>UTF-8</span>
          </div>
          <div className="flex items-center gap-4">
            <span>{t('editor:scriptEditor.lines', { count: code.split('\n').length })}</span>
            <span>{fontSize}px</span>
            <span className="tabular-nums">{Math.round(size.w)}×{Math.round(size.h)}</span>
            <span>{t('editor:scriptEditor.ctrlSToSave')}</span>
          </div>
        </div>

        {/* Resize handle — bottom-right corner. Hidden in fullscreen. */}
        {!isFullscreen && (
          <div
            onMouseDown={handleResizeMouseDown}
            className="absolute bottom-0 right-0 w-4 h-4 cursor-nwse-resize flex items-end justify-end pr-0.5 pb-0.5 z-10 group"
            title={t('editor:scriptEditor.resize')}
            aria-label={t('editor:scriptEditor.resize')}
          >
            <DragHorizontal size={12} className="rotate-45 text-on-primary/60 group-hover:text-on-primary transition-colors" />
          </div>
        )}
      </div>

      {aiDialogOpen && onAiGenerate && (
        <AiPromptDialog
          title={aiDialogTitle}
          subtitle={aiDialogSubtitle}
          placeholder={aiDialogPlaceholder}
          submitLabel={aiDialogSubmit}
          showReplaceToggle
          defaultReplaceAll={false}
          onSubmit={handleAiSubmit}
          onClose={() => setAiDialogOpen(false)}
        />
      )}
    </div>,
    document.body
  );
}

export default ScriptEditorDialog;

function ToolbarButton({
  icon: Icon, label, onClick, active, disabled,
}: Readonly<{ icon: React.ElementType; label: string; onClick: () => void; active?: boolean; disabled?: boolean }>) {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      className={`p-1.5 rounded-md transition-colors focus-visible:outline-2 focus-visible:outline-primary disabled:opacity-40 disabled:cursor-not-allowed ${
        active ? 'text-primary bg-primary-fixed' : 'text-on-surface-variant hover:text-on-surface hover:bg-surface-high'
      }`}
      title={label}
      aria-label={label}
      aria-pressed={active}
    >
      <Icon size={14} />
    </button>
  );
}
