import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import i18n from '../../../../i18n';
import { ExecutionStatusBadge, TriggerCell } from '../../../../components/designer/execution/ExecutionPanelParts';

// Regression guard for the German status-bubble overflow: the long DE label
// "Fehlgeschlagen" (14 chars vs EN "Failed" 6) used to break out of / clip the
// colored pill because the badge could wrap and shrink. The pill must keep its
// label on a single line (whitespace-nowrap) and never be squeezed in flex rows
// (shrink-0), so the background always wraps the full text in any language.
describe('ExecutionStatusBadge — fits long German labels', () => {
  it('renders the full German "Fehlgeschlagen" label on a non-wrapping, non-shrinking pill', async () => {
    await i18n.changeLanguage('de');
    render(<ExecutionStatusBadge status="Failed" />);

    const badge = screen.getByText('Fehlgeschlagen');
    expect(badge).toHaveClass('whitespace-nowrap');
    expect(badge).toHaveClass('shrink-0');
  });

  it('renders the German "Erfolgreich" label', async () => {
    await i18n.changeLanguage('de');
    render(<ExecutionStatusBadge status="Succeeded" />);

    expect(screen.getByText('Erfolgreich')).toBeInTheDocument();
  });
});

describe('TriggerCell — robust against squish next to sibling chips', () => {
  it('keeps the trigger pill nowrap + shrink-0', () => {
    render(<TriggerCell triggeredBy="manualTrigger" />);

    // The span carries the raw value as its title — query by it so the assertion
    // is language-agnostic.
    const pill = screen.getByTitle('manualTrigger');
    expect(pill).toHaveClass('whitespace-nowrap');
    expect(pill).toHaveClass('shrink-0');
  });
});
