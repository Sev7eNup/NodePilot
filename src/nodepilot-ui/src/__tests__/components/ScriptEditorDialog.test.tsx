import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';

import { ScriptEditorDialog } from '../../components/designer/ScriptEditorDialog';

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

  it('streamed tokens append to the editor buffer (default insert mode)', async () => {
    const onAiGenerate = vi.fn((_p: string, _cur: string, onToken: (t: string) => void) => { onToken('Get-Service'); return Promise.resolve(); });
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
    expect(onAiGenerate.mock.calls[0][1]).toBe('$existing = 1'); // current editor content, passed in as the basis for refactoring
  });

  it('replace-all clears the buffer on first token then streams in', async () => {
    const onAiGenerate = vi.fn((_p: string, _cur: string, onToken: (t: string) => void) => { onToken('Get-Service'); return Promise.resolve(); });
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
    fireEvent.click(screen.getByRole('checkbox')); // toggle "replace all"
    fireEvent.click(screen.getAllByRole('button', { name: /^generate$/i })[0]);

    await waitFor(() => {
      const editor = screen.getByTestId('monaco-editor-mock') as HTMLTextAreaElement;
      expect(editor.value).toBe('Get-Service');
      expect(editor.value).not.toContain('$existing');
    });
  });

  it('closes the dialog immediately on Generate and shows the waiting indicator until the first token', async () => {
    let resolve!: () => void;
    const onAiGenerate = vi.fn(() => new Promise<void>((r) => { resolve = r; })); // never tokens → stays waiting
    render(
      <ScriptEditorDialog value="" onChange={() => {}} onClose={() => {}} onAiGenerate={onAiGenerate} />,
    );

    fireEvent.click(screen.getByRole('button', { name: /generate script with ai/i }));
    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: 'go' } });
    fireEvent.click(screen.getAllByRole('button', { name: /^generate$/i })[0]);

    // The dialog closes immediately (title gone); the editor instead shows a waiting indicator with a Cancel button.
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
    // The dialog is already closed — the error shows up in the editor's banner, not inside the (closed) dialog.
    expect(screen.queryByText(/^generate script with ai$/i)).not.toBeInTheDocument();
  });
});
