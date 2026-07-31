import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { Card, GroupHeading, Toggle, WarningNote } from '../../../components/admin-settings/SectionFormHelpers';
import { Chat } from '@carbon/icons-react';

/**
 * Die Layout-Primitive der Admin-Settings. Sie sind der EINZIGE Ort, an dem der Rhythmus der
 * Seite steht — vorher lag z. B. der Gruppen-Abstand als `mt-4 mb-2`-Literal 12× kopiert in
 * vier Sektionsdateien. Diese Tests pinnen deshalb zwei Dinge: dass die Primitive existieren
 * und die gemeinsame Struktur liefern, und dass `Toggle` seine Erklärungen/Warnungen als
 * eigene Slots trägt statt als frei schwebende Geschwister-Elemente am Call-Site.
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
    // Regel + Luft darüber sind der Grund, warum es die Komponente gibt.
    expect(heading.className).toContain('border-t');
    expect(heading.className).toMatch(/\bmt-\d/);
    expect(heading.className).toMatch(/\bpt-\d/);
    // …aber nicht für die Gruppe, die eine Karte eröffnet.
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
    // Eingerückt am Checkbox-Vorsprung vorbei — sonst liest sich der Text als Zwischentext
    // zwischen zwei Schaltern statt als Erklärung zu diesem einen.
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
    // Skins überschreiben die Status-Tokens bewusst nicht — ein `amber-500`-Literal
    // (der Vorgänger) hängt dagegen an Tailwinds Palette fest.
    expect(note.className).toContain('border-warning/40');
    expect(note.className).toContain('bg-warning-container/25');
    expect(note.className).toContain('text-on-warning-container');
    expect(note.className).not.toMatch(/\bamber-/);
  });
});
