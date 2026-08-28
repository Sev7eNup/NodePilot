import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { Card, GroupHeading, Toggle, WarningNote } from '../../../components/admin-settings/SectionFormHelpers';
import { Chat } from '@carbon/icons-react';

/**
 * Layout primitives of the admin settings page. They are the only place that defines the
 * spacing rhythm, so the section files never repeat those class literals. These tests pin
 * two things: the primitives render the shared structure, and `Toggle` carries its hints and
 * warnings in its own slots instead of leaving them as loose siblings at the call site.
 */

const toggleProps = {
  configKey: 'X:Y',
  effectiveSource: {} as Record<string, string>,
  isEnvLocked: () => false,
};

describe('Card', () => {
  it('separates the title from the body with a rule', () => {
    render(<Card icon={Chat} title="Titel"><p>Body</p></Card>);
    const heading = screen.getByRole('heading', { name: 'Titel' });
    expect(heading.className).toContain('border-b');
    expect(screen.getByText('Body')).toBeInTheDocument();
  });
});

describe('GroupHeading', () => {
  it('carries its own separation instead of relying on the call site', () => {
    render(<GroupHeading>Wissensquellen</GroupHeading>);
    const heading = screen.getByRole('heading', { name: 'Wissensquellen' });
    // The separator rule and the space above it are the reason this component exists.
    expect(heading.className).toContain('border-t');
    expect(heading.className).toMatch(/\bmt-\d/);
    expect(heading.className).toMatch(/\bpt-\d/);
    // The first group in a card gets no rule above it.
    expect(heading.className).toContain('first:border-t-0');
  });
});

describe('Toggle', () => {
  it('keeps the checkbox reachable by its label', () => {
    const onChange = vi.fn();
    render(<Toggle label="KI-Chat aktiviert" checked={false} onChange={onChange} {...toggleProps} />);
    fireEvent.click(screen.getByLabelText('KI-Chat aktiviert'));
    expect(onChange).toHaveBeenCalledWith(true);
  });

  it('renders a hint indented under its own label', () => {
    render(<Toggle label="Betrieb" checked hint="Nur Definition und Analyse." onChange={vi.fn()} {...toggleProps} />);
    const hint = screen.getByText('Nur Definition und Analyse.');
    // Indented past the checkbox so the text reads as an explanation of this one switch
    // rather than as loose text between two switches.
    expect(hint.parentElement!.className).toContain('ml-[1.625rem]');
  });

  it('hosts conditional children in the same indented slot', () => {
    render(
      <Toggle label="Quellcode" checked onChange={vi.fn()} {...toggleProps}>
        <WarningNote>Achtung</WarningNote>
      </Toggle>,
    );
    expect(screen.getByText('Achtung').parentElement!.className).toContain('ml-[1.625rem]');
  });

  it('renders no slot wrapper when there is neither hint nor children', () => {
    const { container } = render(<Toggle label="Doku" checked onChange={vi.fn()} {...toggleProps} />);
    expect(container.querySelector('[class*="ml-[1.625rem]"]')).toBeNull();
  });
});

describe('WarningNote', () => {
  it('uses the warning status tokens, not palette literals', () => {
    render(<WarningNote>Achtung</WarningNote>);
    const note = screen.getByText('Achtung');
    // Status tokens follow the active skin, while a palette literal such as `amber-500`
    // stays pinned to the Tailwind palette.
    expect(note.className).toContain('border-warning/40');
    expect(note.className).toContain('bg-warning-container/25');
    expect(note.className).toContain('text-on-warning-container');
    expect(note.className).not.toMatch(/\bamber-/);
  });
});
