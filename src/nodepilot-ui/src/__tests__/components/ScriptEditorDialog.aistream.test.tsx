import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';

// Tests the AI streaming write path against a fake editor supplied through onMount. The global
// setup.ts mock has no onMount handler, so editorRef stays null and the setCode fallback runs
// instead of the streaming path. The fake below tracks line/column positions: executeEdits is a
// no-op while the editor is read-only and otherwise resolves its range to a string offset, and
// only setPosition moves the cursor. Both match real Monaco.
const h = vi.hoisted(() => {
  let value = '';
  let readOnly = false;
  let cursor = { lineNumber: 1, column: 1 };
  let onChange: ((v: string) => void) | undefined;

  const endOf = (v: string) => {
    const lines = v.split('\n');
    return { lineNumber: lines.length, column: lines[lines.length - 1].length + 1 };
  };
  const posToOffset = (v: string, line: number, col: number) => {
    const lines = v.split('\n');
    let off = 0;
    for (let i = 0; i < line - 1; i++) off += (lines[i]?.length ?? 0) + 1;
    return off + (col - 1);
  };

  const editor = {
    updateOptions: (o: { readOnly?: boolean }) => { if (o.readOnly !== undefined) readOnly = o.readOnly; },
    getSelection: () => ({ endLineNumber: cursor.lineNumber, endColumn: cursor.column }),
    setPosition: (p: { lineNumber: number; column: number }) => { cursor = { lineNumber: p.lineNumber, column: p.column }; },
    revealPositionInCenterIfOutsideViewport: () => {},
    getModel: () => ({
      getValue: () => value,
      getFullModelRange: () => {
        const e = endOf(value);
        return { startLineNumber: 1, startColumn: 1, endLineNumber: e.lineNumber, endColumn: e.column };
      },
    }),
    executeEdits: (
      _src: string,
      edits: Array<{ range: { startLineNumber: number; startColumn: number; endLineNumber: number; endColumn: number }; text: string }>,
    ) => {
      if (readOnly) return false; // real Monaco refuses executeEdits while the editor is read-only
      for (const e of edits) {
        const start = posToOffset(value, e.range.startLineNumber, e.range.startColumn);
        const end = posToOffset(value, e.range.endLineNumber, e.range.endColumn);
        value = value.slice(0, start) + e.text + value.slice(end);
      }
      onChange?.(value);
      return true;
    },
    getValue: () => value,
    focus: () => {},
    pushUndoStop: () => {},
    addCommand: () => {},
    trigger: () => {},
  };
  return {
    editor,
    init: (v: string) => { value = v; cursor = endOf(v); readOnly = false; },
    setOnChange: (fn: ((v: string) => void) | undefined) => { onChange = fn; },
    reset: () => { value = ''; cursor = { lineNumber: 1, column: 1 }; readOnly = false; onChange = undefined; },
  };
});

vi.mock('@monaco-editor/react', async () => {
  const React = await import('react');
  const MockEditor = (props: { value?: string; onChange?: (v: string | undefined) => void; onMount?: (e: unknown, m: unknown) => void }) => {
    const { value, onChange, onMount } = props;
    h.setOnChange((v: string) => onChange?.(v));
    const mountedRef = React.useRef(false);
    React.useEffect(() => {
      if (mountedRef.current) return;
      mountedRef.current = true;
      h.init(value ?? '');
      onMount?.(h.editor, {});
    }, [value, onMount]);
    return React.createElement('textarea', {
      'data-testid': 'monaco-editor-mock',
      value: value ?? '',
      readOnly: true,
      onChange: (e: { target: { value: string } }) => onChange?.(e.target.value),
    });
  };
  return { default: MockEditor, loader: { config: () => {}, init: () => Promise.resolve({}) } };
});

// This Range mock stores its coordinates, unlike the global setup.ts mock, because the
// position-tracking fake model above resolves the insert location from them.
vi.mock('../../lib/monacoSetup', () => ({
  // Must stay in sync with the real module: ScriptEditorDialog imports this constant, and a
  // missing export in the mock is a hard ESM error, not `undefined`.
  MONO_FONT_STACK:
    "'IBM Plex Mono', ui-monospace, 'Cascadia Code', Consolas, 'SFMono-Regular', Menlo, monospace",
  monaco: {
    editor: { defineTheme: () => {}, setTheme: () => {}, setModelMarkers: () => {} },
    languages: {
      registerCompletionItemProvider: () => ({ dispose: () => {} }),
      CompletionItemKind: { Variable: 4 },
    },
    Range: class {
      startLineNumber: number;
      startColumn: number;
      endLineNumber: number;
      endColumn: number;
      constructor(sl: number, sc: number, el: number, ec: number) {
        this.startLineNumber = sl;
        this.startColumn = sc;
        this.endLineNumber = el;
        this.endColumn = ec;
      }
    },
    KeyMod: { CtrlCmd: 0 },
    KeyCode: { KeyS: 0 },
    MarkerSeverity: { Warning: 4 },
  },
}));

import { ScriptEditorDialog, advanceStreamPosition } from '../../components/designer/ScriptEditorDialog';

beforeEach(() => h.reset());

// Wait longer than one animation frame (~16 ms) so the requestAnimationFrame-batched flush runs
// between tokens, which exercises the multi-flush path.
const tick = () => new Promise<void>((r) => setTimeout(r, 30));

function startGenerate(replaceAll: boolean) {
  fireEvent.click(screen.getByRole('button', { name: /generate script with ai/i }));
  fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: 'go' } });
  if (replaceAll) fireEvent.click(screen.getByRole('checkbox', { name: /replace entire script/i }));
  fireEvent.click(screen.getAllByRole('button', { name: /^generate$/i })[0]);
}

describe('ScriptEditorDialog — AI streaming order into a real (read-only) editor', () => {
  it('replace-all streams multi-line chunks in order across flushes (no scramble)', async () => {
    const onAiGenerate = vi.fn(async (_p: string, _cur: string | null, onToken: (t: string) => void) => {
      onToken('$now = Get-Date\n'); await tick();
      onToken('Write-Host '); await tick();
      onToken('"Zeit: $now"');
    });
    render(<ScriptEditorDialog value={'$old = 1\n# weg'} onChange={() => {}} onClose={() => {}} onAiGenerate={onAiGenerate} />);

    startGenerate(true);

    await waitFor(() => {
      const ed = screen.getByTestId('monaco-editor-mock') as HTMLTextAreaElement;
      expect(ed.value).toBe('$now = Get-Date\nWrite-Host "Zeit: $now"'); // in order, old content gone
    });
  });

  it('insert mode streams chunks in order at the cursor (no scramble)', async () => {
    const onAiGenerate = vi.fn(async (_p: string, _cur: string | null, onToken: (t: string) => void) => {
      onToken('$a = 1\n'); await tick();
      onToken('$b = 2\n'); await tick();
      onToken('$c = 3');
    });
    render(<ScriptEditorDialog value={'# header\n'} onChange={() => {}} onClose={() => {}} onAiGenerate={onAiGenerate} />);

    startGenerate(false);

    await waitFor(() => {
      const ed = screen.getByTestId('monaco-editor-mock') as HTMLTextAreaElement;
      expect(ed.value).toBe('# header\n$a = 1\n$b = 2\n$c = 3');
    });
  });
});

describe('advanceStreamPosition', () => {
  it('advances the column within a single line', () => {
    expect(advanceStreamPosition({ lineNumber: 2, column: 3 }, 'abc')).toEqual({ lineNumber: 2, column: 6 });
  });
  it('advances the line and resets the column after a newline', () => {
    expect(advanceStreamPosition({ lineNumber: 1, column: 1 }, 'ab\ncd')).toEqual({ lineNumber: 2, column: 3 });
  });
  it('handles multiple newlines in one chunk', () => {
    expect(advanceStreamPosition({ lineNumber: 2, column: 3 }, 'x\ny\nz')).toEqual({ lineNumber: 4, column: 2 });
  });
  it('handles a trailing newline (column resets to 1)', () => {
    expect(advanceStreamPosition({ lineNumber: 1, column: 1 }, 'abc\n')).toEqual({ lineNumber: 2, column: 1 });
  });
});
