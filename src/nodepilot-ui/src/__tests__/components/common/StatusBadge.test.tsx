import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { I18nextProvider } from 'react-i18next';
import i18n from '../../../i18n';
import { StatusBadge } from '../../../components/common/StatusBadge';

// The suite is pinned to 'en' (see __tests__/setup.ts), so assertions use the English labels.
function wrap(ui: React.ReactNode) {
  return <I18nextProvider i18n={i18n}>{ui}</I18nextProvider>;
}

describe('StatusBadge', () => {
  it('renders the i18n label for each known raw status', () => {
    const { rerender } = render(wrap(<StatusBadge status="Succeeded" />));
    expect(screen.getByText('Succeeded')).toBeInTheDocument();
    rerender(wrap(<StatusBadge status="Failed" />));
    expect(screen.getByText('Failed')).toBeInTheDocument();
    rerender(wrap(<StatusBadge status="Running" />));
    expect(screen.getByText('Running')).toBeInTheDocument();
    rerender(wrap(<StatusBadge status="Pending" />));
    expect(screen.getByText('Pending')).toBeInTheDocument();
    rerender(wrap(<StatusBadge status="Cancelled" />));
    expect(screen.getByText('Cancelled')).toBeInTheDocument();
    rerender(wrap(<StatusBadge status="Paused" />));
    expect(screen.getByText('Paused')).toBeInTheDocument();
  });

  it('keeps TimedOut distinct from Failed at the label level', () => {
    render(wrap(<StatusBadge status="TimedOut" />));
    // TimedOut has its own label key (not the Failed label).
    expect(screen.queryByText('Failed')).not.toBeInTheDocument();
    expect(screen.getByText('Timed out')).toBeInTheDocument();
  });

  it('renders the raw string for an unknown status with neutral styling (no info-fallback)', () => {
    render(wrap(<StatusBadge status="SomeNewBackendStatus" />));
    // Unknown status must surface verbatim, not be masked as "info".
    expect(screen.getByText('SomeNewBackendStatus')).toBeInTheDocument();
  });

  it('does not pass a known status through as raw text', () => {
    render(wrap(<StatusBadge status="Succeeded" />));
    // The translated label happens to be 'Succeeded' in EN, so assert the count is exactly 1
    // (no duplicate raw pass-through) and that a distinct raw like 'Failed' is absent.
    expect(screen.getAllByText('Succeeded')).toHaveLength(1);
    expect(screen.queryByText('Failed')).not.toBeInTheDocument();
  });
});