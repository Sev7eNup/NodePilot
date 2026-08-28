import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { EnvOverrideBadge } from '../../../components/admin-settings/EnvOverrideBadge';

describe('EnvOverrideBadge', () => {
  it('renders nothing for non-overridden sources', () => {
    const { container } = render(<EnvOverrideBadge source="runtime" configKey="Smtp:Host" />);
    expect(container.firstChild).toBeNull();
  });

  it('renders the env badge with the canonical ASP.NET-Core env-var name', () => {
    render(<EnvOverrideBadge source="env" configKey="Smtp:Host" />);
    const badge = screen.getByLabelText(/Smtp__Host/);
    expect(badge).toBeInTheDocument();
  });

  it('renders the CLI variant when the source is cli', () => {
    render(<EnvOverrideBadge source="cli" configKey="Engine:MaxConcurrentSteps" />);
    // The CLI tooltip names no specific argument; it only marks the field as overridden
    // on the command line.
    const badge = screen.getByText(/Environment|Wert/i);
    expect(badge).toBeInTheDocument();
  });
});
