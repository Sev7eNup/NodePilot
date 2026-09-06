import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';

import { ScriptEditorDialog } from '../../components/designer/ScriptEditorDialog';
import { monaco } from '../../lib/monacoSetup';

describe('ScriptEditorDialog', () => {
  it('renders title bar, PS badge and the (mocked) Monaco editor', () => {
    render(<ScriptEditorDialog value="$foo = 1" onChange={() => {}} onClose={() => {}} />);
    expect(screen.getByText('PowerShell Script Editor')).toBeInTheDocument();
    expect(screen.getByText('PS')).toBeInTheDocument();
    expect(screen.getByTestId('monaco-editor-mock')).toBeInTheDocument();
  });

  it('Save & Close calls onChange with current code and onClose', () => {
    const onChange = vi.fn();
    const onClose = vi.fn();
    render(<ScriptEditorDialog value="$x = 1" onChange={onChange} onClose={onClose} />);

    fireEvent.click(screen.getByRole('button', { name: /save & close/i }));

    expect(onChange).toHaveBeenCalledWith('$x = 1');
    expect(onClose).toHaveBeenCalled();
  });

  it('typing in the editor updates the buffer used by Save', () => {
    const onChange = vi.fn();
    const onClose = vi.fn();
    render(<ScriptEditorDialog value="" onChange={onChange} onClose={onClose} />);

    const ed = screen.getByTestId('monaco-editor-mock') as HTMLTextAreaElement;
    fireEvent.change(ed, { target: { value: 'Get-Process' } });
    fireEvent.click(screen.getByRole('button', { name: /save & close/i }));

    expect(onChange).toHaveBeenLastCalledWith('Get-Process');
    expect(onClose).toHaveBeenCalled();
  });

  it('Esc on the dialog closes it', () => {
    const onClose = vi.fn();
    render(<ScriptEditorDialog value="" onChange={() => {}} onClose={onClose} />);

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    expect(onClose).toHaveBeenCalled();
  });

  it('Run button is shown only when onRun is provided and triggers it', async () => {
    const onRun = vi.fn().mockResolvedValue({
      success: true, output: 'ok', errorOutput: null, outputParameters: {}, durationMs: 12,
    });
    render(<ScriptEditorDialog value="$x" onChange={() => {}} onClose={() => {}} onRun={onRun} />);

    const btn = screen.getByRole('button', { name: /^run$/i });
    fireEvent.click(btn);
    expect(onRun).toHaveBeenCalled();
  });

  it('renders the variables sidebar when availableVars are passed', () => {
    render(
      <ScriptEditorDialog
        value=""
        onChange={() => {}}
        onClose={() => {}}
        availableVars={[{ name: '$prevHost', label: 'previous step host' }]}
      />,
    );
    expect(screen.getByText('Variables (Upstream)')).toBeInTheDocument();
    expect(screen.getByTitle(/Insert \$prevHost/)).toBeInTheDocument();
  });

  it('parses $foo = ... assignments and lists them as exposed downstream', () => {
    render(
      <ScriptEditorDialog
        value={'$hostName = $env:COMPUTERNAME\n$count = 5'}
        onChange={() => {}}
        onClose={() => {}}
        outputVariableName="collectInfo"
      />,
    );
    expect(screen.getByText('Exposed Downstream')).toBeInTheDocument();
    expect(screen.getByText('$hostName')).toBeInTheDocument();
    expect(screen.getByText('$count')).toBeInTheDocument();
  });

  // ---- AI-assisted script generation -----------------------------------------------------

  it('AI button is hidden when onAiGenerate is not provided', () => {
    render(<ScriptEditorDialog value="" onChange={() => {}} onClose={() => {}} />);
    expect(screen.queryByRole('button', { name: /generate script with ai/i })).not.toBeInTheDocument();
  });

  it('AI button is shown when onAiGenerate is provided', () => {
    render(
      <ScriptEditorDialog
        value=""
        onChange={() => {}}
        onClose={() => {}}
        onAiGenerate={async () => {}}
      />,
    );
    expect(screen.getByRole('button', { name: /generate script with ai/i })).toBeInTheDocument();
  });

  it('clicking AI button opens the prompt dialog', () => {
    render(
      <ScriptEditorDialog
        value=""
        onChange={() => {}}
        onClose={() => {}}
        onAiGenerate={async () => {}}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: /generate script with ai/i }));
    expect(screen.getByText(/Generate script with AI/i)).toBeInTheDocument();
  });

  it('does not expose the current script unless the user explicitly consents', async () => {
    const onAiGenerate = vi.fn((_p: string, _cur: string | null, onToken: (t: string) => void) => { onToken('Get-Service'); return Promise.resolve(); });
    render(
      <ScriptEditorDialog
        value="$existing = 1"
        onChange={() => {}}
        onClose={() => {}}
        onAiGenerate={onAiGenerate}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /generate script with ai/i }));
    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: 'list services' } });
    fireEvent.click(screen.getAllByRole('button', { name: /^generate$/i })[0]);

    await waitFor(() => {
      const editor = screen.getByTestId('monaco-editor-mock') as HTMLTextAreaElement;
      expect(editor.value).toContain('$existing = 1');
      expect(editor.value).toContain('Get-Service');
    });
    expect(onAiGenerate.mock.calls[0][0]).toBe('list services');
    expect(onAiGenerate.mock.calls[0][1]).toBeNull();
  });

  it('names the LLM target and sends the current script only after consent', async () => {
    const onAiGenerate = vi.fn((_p: string, _cur: string | null, onToken: (t: string) => void) => { onToken('Get-Service'); return Promise.resolve(); });
    render(
      <ScriptEditorDialog
        value="$password = 'possibly-secret'"
        onChange={() => {}}
        onClose={() => {}}
        onAiGenerate={onAiGenerate}
        aiTargetHost="llm.example.test"
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /generate script with ai/i }));
    expect(screen.getByText(/llm\.example\.test/i)).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: 'refactor' } });
    fireEvent.click(screen.getByRole('checkbox', { name: /send current script/i }));
    fireEvent.click(screen.getAllByRole('button', { name: /^generate$/i })[0]);

    await waitFor(() => expect(onAiGenerate).toHaveBeenCalled());
    expect(onAiGenerate.mock.calls[0][1]).toBe("$password = 'possibly-secret'");
  });

  it('uses an external-LLM fallback warning and forgets consent when reopened', () => {
    render(
      <ScriptEditorDialog
        value="$possiblySecret = 1"
        onChange={() => {}}
        onClose={() => {}}
        onAiGenerate={async () => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /generate script with ai/i }));
    expect(screen.getByText(/configured LLM endpoint.*may be external/i)).toBeInTheDocument();
    const consent = screen.getByRole('checkbox', { name: /send current script/i });
    expect(consent).not.toBeChecked();
    fireEvent.click(consent);
    expect(consent).toBeChecked();

    const dialogs = screen.getAllByRole('dialog');
    fireEvent.keyDown(dialogs[dialogs.length - 1], { key: 'Escape' });
    fireEvent.click(screen.getByRole('button', { name: /generate script with ai/i }));
    expect(screen.getByRole('checkbox', { name: /send current script/i })).not.toBeChecked();
  });

  it('replace-all clears the buffer on first token then streams in', async () => {
    const onAiGenerate = vi.fn((_p: string, _cur: string | null, onToken: (t: string) => void) => { onToken('Get-Service'); return Promise.resolve(); });
    render(
      <ScriptEditorDialog
        value="$existing = 1"
        onChange={() => {}}
        onClose={() => {}}
        onAiGenerate={onAiGenerate}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /generate script with ai/i }));
    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: 'list services' } });
    fireEvent.click(screen.getByRole('checkbox', { name: /replace entire script/i }));
    fireEvent.click(screen.getAllByRole('button', { name: /^generate$/i })[0]);

    await waitFor(() => {
      const editor = screen.getByTestId('monaco-editor-mock') as HTMLTextAreaElement;
      expect(editor.value).toBe('Get-Service');
      expect(editor.value).not.toContain('$existing');
    });
  });

  it('closes the dialog immediately on Generate and shows the waiting indicator until the first token', async () => {
    let resolve!: () => void;
    const onAiGenerate = vi.fn(() => new Promise<void>((r) => { resolve = r; })); // emits no tokens
    render(
      <ScriptEditorDialog value="" onChange={() => {}} onClose={() => {}} onAiGenerate={onAiGenerate} />,
    );

    fireEvent.click(screen.getByRole('button', { name: /generate script with ai/i }));
    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: 'go' } });
    fireEvent.click(screen.getAllByRole('button', { name: /^generate$/i })[0]);

    // The dialog closes immediately, so its title is gone and the editor shows a waiting
    // indicator with a Cancel button instead.
    await waitFor(() => expect(screen.queryByText(/^generate script with ai$/i)).not.toBeInTheDocument());
    expect(screen.getByRole('button', { name: /^cancel$/i })).toBeInTheDocument();

    resolve();
    await waitFor(() => expect(screen.queryByRole('button', { name: /^cancel$/i })).not.toBeInTheDocument());
  });

  it('disables KI, Run and Save while a generation is in flight', async () => {
    let resolve!: () => void;
    const onAiGenerate = vi.fn(() => new Promise<void>((r) => { resolve = r; }));
    render(
      <ScriptEditorDialog value="" onChange={() => {}} onClose={() => {}} onAiGenerate={onAiGenerate} onRun={async () => ({
        success: true, output: 'ok', errorOutput: null, errorMessage: null, outputParameters: {}, durationMs: 1,
      })} />,
    );

    fireEvent.click(screen.getByRole('button', { name: /generate script with ai/i }));
    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: 'go' } });
    fireEvent.click(screen.getAllByRole('button', { name: /^generate$/i })[0]);

    await waitFor(() => expect(screen.getByRole('button', { name: /generate script with ai/i })).toBeDisabled());
    expect(screen.getByRole('button', { name: /^run$/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /save & close/i })).toBeDisabled();

    resolve();
    await waitFor(() => expect(screen.getByRole('button', { name: /save & close/i })).not.toBeDisabled());
  });

  it('AI generate failure (before first token) shows the error in the editor and closes the dialog', async () => {
    const onAiGenerate = vi.fn().mockRejectedValue(new Error('LLM unreachable'));
    render(
      <ScriptEditorDialog
        value=""
        onChange={() => {}}
        onClose={() => {}}
        onAiGenerate={onAiGenerate}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /generate script with ai/i }));
    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: 'go' } });
    fireEvent.click(screen.getAllByRole('button', { name: /^generate$/i })[0]);

    expect(await screen.findByRole('alert')).toHaveTextContent('LLM unreachable');
    // The dialog is already closed, so the error appears in the editor's banner rather than
    // in the dialog.
    expect(screen.queryByText(/^generate script with ai$/i)).not.toBeInTheDocument();
  });
});

/**
 * The production CSS minifier shortens colors inside custom properties, so design tokens reach
 * the theme bridge in notations the stylesheet never contained. Monaco accepts only 6- or
 * 8-digit hex for token colors and throws on anything else.
 */
describe('ScriptEditorDialog Monaco theme bridge', () => {
  const MINIFIED: Record<string, string> = {
    '--color-surface-low': '#222',
    '--color-surface-lowest': '#fff',
    '--color-surface-container': '#fff8',
    '--color-on-surface': 'red',
    '--color-primary': '#e00',
    '--color-outline': 'oklch(0.7 0.1 200)',
  };

  afterEach(() => {
    for (const name of Object.keys(MINIFIED)) document.documentElement.style.removeProperty(name);
    vi.restoreAllMocks();
  });

  function setMinifiedTokens() {
    for (const [name, value] of Object.entries(MINIFIED)) {
      document.documentElement.style.setProperty(name, value);
    }
  }

  it('hands Monaco only hex, whatever notation the tokens arrive in', () => {
    setMinifiedTokens();
    const defineTheme = vi.spyOn(monaco.editor, 'defineTheme');
    render(<ScriptEditorDialog value="" onChange={() => {}} onClose={() => {}} />);

    expect(defineTheme).toHaveBeenCalled();
    const colors = defineTheme.mock.calls.flatMap(([, data]) => Object.values(data.colors ?? {}));
    expect(colors.length).toBeGreaterThan(0);
    // The optional trailing pair covers the alpha the caller concatenates onto `primary`.
    for (const color of colors) expect(color).toMatch(/^#[0-9a-f]{6}([0-9a-f]{2})?$/i);
  });

  it('falls back to the built-in theme when defineTheme rejects a value', () => {
    vi.spyOn(monaco.editor, 'defineTheme').mockImplementation(() => {
      throw new Error('Illegal value for token color: #fff');
    });
    render(<ScriptEditorDialog value="" onChange={() => {}} onClose={() => {}} />);

    expect(screen.getByText('PowerShell Script Editor')).toBeInTheDocument();
    expect(screen.getByTestId('monaco-editor-mock')).toHaveAttribute('data-theme', 'vs');
  });

  // Monaco parses a theme's colors on activation, not on definition, so this is the edge a
  // defineTheme-only test misses: the definition succeeds and setTheme throws.
  it('falls back when the definition succeeds but activation throws', () => {
    vi.spyOn(monaco.editor, 'setTheme').mockImplementation((name: string) => {
      if (name.startsWith('nodepilot-')) throw new Error('Illegal value for token color: #fff');
    });
    render(<ScriptEditorDialog value="" onChange={() => {}} onClose={() => {}} />);

    expect(screen.getByText('PowerShell Script Editor')).toBeInTheDocument();
    expect(screen.getByTestId('monaco-editor-mock')).toHaveAttribute('data-theme', 'vs');
  });
});
