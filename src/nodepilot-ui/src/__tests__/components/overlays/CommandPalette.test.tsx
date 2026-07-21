import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { CommandPalette, type PaletteCommand } from '../../../components/designer/overlays/CommandPalette';
import { filterCommandsForDesignerMode } from '../../../lib/editorCommandPalette';

/**
 * CommandPalette is the Cmd-K-style fuzzy command picker. We pin the user-visible
 * contracts:
 *   - Empty query lists everything, grouped.
 *   - Typing filters results by fuzzy match against title/subtitle/group.
 *   - ArrowUp/Down + Enter invoke the highlighted command and close the palette.
 *   - Escape closes without invoking.
 *   - Disabled commands are visible but never invoked.
 *   - Click on the backdrop closes; click on a row invokes.
 */

function makeCmd(id: string, title: string, extra: Partial<PaletteCommand> = {}): PaletteCommand {
  return { id, title, run: vi.fn(), ...extra };
}

describe('CommandPalette', () => {
  it('standardMode_filtersExpertAndStyleCommands', () => {
    const commands = [
      makeCmd('save', 'Save', { group: 'Run' }),
      makeCmd('debug', 'Debug', { group: 'Run', expertOnly: true }),
      makeCmd('heatmap', 'Heatmap', { group: 'Style' }),
    ];

    expect(filterCommandsForDesignerMode(commands, 'standard').map((command) => command.id)).toEqual(['save']);
    expect(filterCommandsForDesignerMode(commands, 'expert')).toEqual(commands);
  });

  it('rendersAllCommandsWithEmptyQuery', () => {
    const cmds = [
      makeCmd('a', 'Run workflow', { group: 'Run' }),
      makeCmd('b', 'Toggle heatmap', { group: 'View' }),
    ];
    render(<CommandPalette commands={cmds} onClose={vi.fn()} />);

    expect(screen.getByText('Run workflow')).toBeInTheDocument();
    expect(screen.getByText('Toggle heatmap')).toBeInTheDocument();
  });

  it('groupsResultsByGroupHeader', () => {
    const cmds = [
      makeCmd('a', 'Save', { group: 'File' }),
      makeCmd('b', 'Open', { group: 'File' }),
      makeCmd('c', 'Find', { group: 'Edit' }),
    ];
    render(<CommandPalette commands={cmds} onClose={vi.fn()} />);

    // Group headers are rendered uppercase
    expect(screen.getByText('File')).toBeInTheDocument();
    expect(screen.getByText('Edit')).toBeInTheDocument();
  });

  it('typingFiltersByTitle', () => {
    const cmds = [
      makeCmd('a', 'Toggle heatmap'),
      makeCmd('b', 'Export workflow'),
    ];
    render(<CommandPalette commands={cmds} onClose={vi.fn()} />);

    fireEvent.change(screen.getByPlaceholderText(/Type a command/i), { target: { value: 'heat' } });

    expect(screen.getByText('Toggle heatmap')).toBeInTheDocument();
    expect(screen.queryByText('Export workflow')).not.toBeInTheDocument();
  });

  it('emptyMatchShowsNoCommandsHint', () => {
    const cmds = [makeCmd('a', 'Save')];
    render(<CommandPalette commands={cmds} onClose={vi.fn()} />);

    fireEvent.change(screen.getByPlaceholderText(/Type a command/i), { target: { value: 'zzz' } });

    expect(screen.getByText(/No commands match/i)).toBeInTheDocument();
  });

  it('clickOnRowInvokesAndCloses', () => {
    const onClose = vi.fn();
    const run = vi.fn();
    const cmds = [makeCmd('a', 'Save', { run })];
    render(<CommandPalette commands={cmds} onClose={onClose} />);

    fireEvent.click(screen.getByText('Save'));

    expect(run).toHaveBeenCalledOnce();
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('disabledCommandIsNotInvoked', () => {
    const onClose = vi.fn();
    const run = vi.fn();
    const cmds = [makeCmd('a', 'Save', { run, disabled: true })];
    render(<CommandPalette commands={cmds} onClose={onClose} />);

    fireEvent.click(screen.getByText('Save'));

    expect(run).not.toHaveBeenCalled();
    // The button itself is disabled, so clicking shouldn't fire any handlers.
  });

  it('enterKeyInvokesHighlightedCommand', () => {
    const onClose = vi.fn();
    const run = vi.fn();
    const cmds = [makeCmd('a', 'Save', { run })];
    render(<CommandPalette commands={cmds} onClose={onClose} />);

    const input = screen.getByPlaceholderText(/Type a command/i);
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(run).toHaveBeenCalledOnce();
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('escapeKeyClosesWithoutInvoking', () => {
    const onClose = vi.fn();
    const run = vi.fn();
    const cmds = [makeCmd('a', 'Save', { run })];
    render(<CommandPalette commands={cmds} onClose={onClose} />);

    fireEvent.keyDown(screen.getByPlaceholderText(/Type a command/i), { key: 'Escape' });

    expect(onClose).toHaveBeenCalledOnce();
    expect(run).not.toHaveBeenCalled();
  });

  it('arrowDownMovesHighlightAndEnterInvokesNewSelection', () => {
    const runA = vi.fn();
    const runB = vi.fn();
    const cmds = [makeCmd('a', 'Aaa', { run: runA }), makeCmd('b', 'Bbb', { run: runB })];
    render(<CommandPalette commands={cmds} onClose={vi.fn()} />);

    const input = screen.getByPlaceholderText(/Type a command/i);
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(runB).toHaveBeenCalledOnce();
    expect(runA).not.toHaveBeenCalled();
  });

  it('arrowUpAtTopStaysAtFirst', () => {
    const runA = vi.fn();
    const cmds = [makeCmd('a', 'Aaa', { run: runA }), makeCmd('b', 'Bbb', { run: vi.fn() })];
    render(<CommandPalette commands={cmds} onClose={vi.fn()} />);

    const input = screen.getByPlaceholderText(/Type a command/i);
    fireEvent.keyDown(input, { key: 'ArrowUp' }); // already at 0
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(runA).toHaveBeenCalledOnce();
  });

  it('clickOnBackdropClosesPalette', () => {
    const onClose = vi.fn();
    render(<CommandPalette commands={[makeCmd('a', 'Save')]} onClose={onClose} />);

    // The outermost div (the backdrop) has onMouseDown={onClose}.
    fireEvent.mouseDown(screen.getByPlaceholderText(/Type a command/i).closest('.fixed')!);

    expect(onClose).toHaveBeenCalledOnce();
  });

  it('rendersShortcutHintWhenProvided', () => {
    const cmds = [makeCmd('a', 'Save', { shortcut: 'Ctrl+S' })];
    render(<CommandPalette commands={cmds} onClose={vi.fn()} />);

    expect(screen.getByText('Ctrl+S')).toBeInTheDocument();
  });

  it('rendersSubtitleWhenProvided', () => {
    const cmds = [makeCmd('a', 'Save', { subtitle: 'Save the current workflow' })];
    render(<CommandPalette commands={cmds} onClose={vi.fn()} />);

    expect(screen.getByText('Save the current workflow')).toBeInTheDocument();
  });

  it('substringMatchOutranksSubsequenceMatch', () => {
    // Pin the fuzzyScore contract: a substring "save" beats a subseq "s..a..v..e".
    const runDirect = vi.fn();
    const runFuzzy = vi.fn();
    const cmds = [
      makeCmd('1', 'Snake-eyes-vault-eject', { run: runFuzzy }),
      makeCmd('2', 'Save workflow',          { run: runDirect }),
    ];
    render(<CommandPalette commands={cmds} onClose={vi.fn()} />);

    fireEvent.change(screen.getByPlaceholderText(/Type a command/i), { target: { value: 'save' } });
    fireEvent.keyDown(screen.getByPlaceholderText(/Type a command/i), { key: 'Enter' });

    expect(runDirect).toHaveBeenCalled();
  });
});
