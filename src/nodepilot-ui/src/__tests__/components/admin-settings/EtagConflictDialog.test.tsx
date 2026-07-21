import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { EtagConflictDialog } from '../../../components/admin-settings/EtagConflictDialog';

describe('EtagConflictDialog', () => {
  const serverSnapshot = {
    sectionPath: 'Smtp',
    payload: { Host: 'server-host', Port: 25 },
    etag: '"abc"',
    isHotReloadable: false,
    effectiveSource: {},
  };

  it('renders nothing when closed', () => {
    const { container } = render(
      <EtagConflictDialog
        open={false}
        serverSnapshot={serverSnapshot}
        localDraft={{ Host: 'mine' }}
        onKeepMine={() => {}} onTakeTheirs={() => {}} onCancel={() => {}}
      />,
    );
    expect(container.firstChild).toBeNull();
  });

  it('renders both sides and wires the three resolution buttons', () => {
    const onKeepMine = vi.fn();
    const onTakeTheirs = vi.fn();
    const onCancel = vi.fn();
    render(
      <EtagConflictDialog
        open
        serverSnapshot={serverSnapshot}
        localDraft={{ Host: 'mine', Port: 587 }}
        onKeepMine={onKeepMine} onTakeTheirs={onTakeTheirs} onCancel={onCancel}
      />,
    );

    expect(screen.getByText(/server-host/)).toBeInTheDocument();
    expect(screen.getByText(/mine/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /meine.*überschreiben|overwrite.*my/i }));
    expect(onKeepMine).toHaveBeenCalledOnce();

    fireEvent.click(screen.getByRole('button', { name: /server-stand|server state/i }));
    expect(onTakeTheirs).toHaveBeenCalledOnce();

    fireEvent.click(screen.getByRole('button', { name: /abbrechen|manuell|manually/i }));
    expect(onCancel).toHaveBeenCalledOnce();
  });
});
