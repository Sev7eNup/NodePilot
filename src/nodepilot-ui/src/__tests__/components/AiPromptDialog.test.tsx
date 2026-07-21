import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { AiPromptDialog } from '../../components/ai/AiPromptDialog';

describe('AiPromptDialog', () => {
  it('renders title, subtitle, and the textarea with auto-focus', () => {
    render(
      <AiPromptDialog
        title="KI Test"
        subtitle="Tu bitte was Vernünftiges."
        onSubmit={async () => {}}
        onClose={() => {}}
      />,
    );

    expect(screen.getByText('KI Test')).toBeInTheDocument();
    expect(screen.getByText('Tu bitte was Vernünftiges.')).toBeInTheDocument();
    const textarea = screen.getByLabelText('AI prompt') as HTMLTextAreaElement;
    expect(textarea).toBeInTheDocument();
    expect(document.activeElement).toBe(textarea);
  });

  it('submit is disabled when prompt is empty / whitespace', () => {
    render(<AiPromptDialog title="x" onSubmit={async () => {}} onClose={() => {}} />);

    const submit = screen.getByRole('button', { name: /generate/i });
    expect(submit).toBeDisabled();

    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: '   ' } });
    expect(submit).toBeDisabled();
  });

  it('submit calls onSubmit with trimmed prompt and replaceAll=false by default', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(
      <AiPromptDialog
        title="x"
        showReplaceToggle
        onSubmit={onSubmit}
        onClose={() => {}}
      />,
    );

    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: '  do a thing  ' } });
    fireEvent.click(screen.getByRole('button', { name: /generate/i }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith('do a thing', false));
  });

  it('replaceAll toggle flips the flag passed to onSubmit', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(
      <AiPromptDialog
        title="x"
        showReplaceToggle
        onSubmit={onSubmit}
        onClose={() => {}}
      />,
    );

    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: 'hello' } });
    fireEvent.click(screen.getByRole('checkbox'));
    fireEvent.click(screen.getByRole('button', { name: /generate/i }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith('hello', true));
  });

  it('replace toggle is hidden when showReplaceToggle is false', () => {
    render(<AiPromptDialog title="x" onSubmit={async () => {}} onClose={() => {}} />);
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  });

  it('shows error message when onSubmit rejects, dialog stays open', async () => {
    const onSubmit = vi.fn().mockRejectedValue(new Error('endpoint unreachable'));
    const onClose = vi.fn();
    render(
      <AiPromptDialog title="x" onSubmit={onSubmit} onClose={onClose} />,
    );

    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: 'go' } });
    fireEvent.click(screen.getByRole('button', { name: /generate/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('endpoint unreachable');
    expect(onClose).not.toHaveBeenCalled();
  });

  it('cancel button calls onClose', () => {
    const onClose = vi.fn();
    render(<AiPromptDialog title="x" onSubmit={async () => {}} onClose={onClose} />);

    fireEvent.click(screen.getByRole('button', { name: /^cancel$/i }));
    expect(onClose).toHaveBeenCalled();
  });

  it('Escape key closes the dialog', () => {
    const onClose = vi.fn();
    render(<AiPromptDialog title="x" onSubmit={async () => {}} onClose={onClose} />);

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    expect(onClose).toHaveBeenCalled();
  });

  it('Ctrl+Enter inside textarea submits', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(<AiPromptDialog title="x" onSubmit={onSubmit} onClose={() => {}} />);

    const ta = screen.getByLabelText('AI prompt');
    fireEvent.change(ta, { target: { value: 'go' } });
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Enter', ctrlKey: true });

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith('go', false));
  });

  it('shows loading state while onSubmit is pending', async () => {
    let resolve!: () => void;
    const pending = new Promise<void>((r) => { resolve = r; });
    const onSubmit = vi.fn().mockReturnValue(pending);

    render(<AiPromptDialog title="x" onSubmit={onSubmit} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: 'go' } });
    fireEvent.click(screen.getByRole('button', { name: /generate/i }));

    // While the submit is pending: the button text changes and Cancel becomes disabled.
    expect(await screen.findByRole('button', { name: /generating/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /^cancel$/i })).toBeDisabled();

    resolve();
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /generate/i })).not.toBeDisabled(),
    );
  });

  it('clears previous error on next submit attempt', async () => {
    const onSubmit = vi
      .fn()
      .mockRejectedValueOnce(new Error('first fail'))
      .mockResolvedValueOnce(undefined);
    render(<AiPromptDialog title="x" onSubmit={onSubmit} onClose={() => {}} />);

    fireEvent.change(screen.getByLabelText('AI prompt'), { target: { value: 'go' } });
    fireEvent.click(screen.getByRole('button', { name: /generate/i }));
    expect(await screen.findByRole('alert')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /generate/i }));
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });
});
