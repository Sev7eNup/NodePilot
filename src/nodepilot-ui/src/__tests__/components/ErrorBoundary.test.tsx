import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ErrorBoundary } from '../../components/ErrorBoundary';

// Suppress React's componentDidCatch noise that would otherwise pollute test output
// when a child renders an exception on purpose.
let consoleErrorSpy: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  consoleErrorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
});

afterEach(() => {
  consoleErrorSpy.mockRestore();
});

function Boom({ message }: { message: string }): never {
  throw new Error(message);
}

describe('ErrorBoundary', () => {
  it('renders children when nothing throws', () => {
    render(
      <ErrorBoundary>
        <p>Healthy content</p>
      </ErrorBoundary>,
    );
    expect(screen.getByText('Healthy content')).toBeInTheDocument();
  });

  it('renders the default full-page fallback when no custom fallback is provided', () => {
    render(
      <ErrorBoundary>
        <Boom message="kaboom default" />
      </ErrorBoundary>,
    );
    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByText('Unerwarteter Fehler')).toBeInTheDocument();
    expect(screen.getByText('kaboom default')).toBeInTheDocument();
  });

  it('renders the custom fallback when one is provided', () => {
    render(
      <ErrorBoundary fallback={(err) => <div>Custom: {err.message}</div>}>
        <Boom message="kaboom custom" />
      </ErrorBoundary>,
    );
    expect(screen.getByText('Custom: kaboom custom')).toBeInTheDocument();
    // Default fallback's UI must NOT be rendered when a custom fallback is used.
    expect(screen.queryByText('Unerwarteter Fehler')).not.toBeInTheDocument();
  });

  it('reset callback recovers when the underlying tree no longer throws', () => {
    let shouldThrow = true;
    function MaybeBoom() {
      if (shouldThrow) throw new Error('temporary');
      return <p>Recovered</p>;
    }

    render(
      <ErrorBoundary fallback={(_err, reset) => (
        <button onClick={() => { shouldThrow = false; reset(); }}>Reset</button>
      )}>
        <MaybeBoom />
      </ErrorBoundary>,
    );

    expect(screen.getByRole('button', { name: 'Reset' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Reset' }));
    expect(screen.getByText('Recovered')).toBeInTheDocument();
  });

  it('console.error is tagged with the scope when one is provided', () => {
    render(
      <ErrorBoundary scope="TestScope" fallback={() => <div>fallback</div>}>
        <Boom message="kaboom scoped" />
      </ErrorBoundary>,
    );
    // React itself emits a few console.error calls when a boundary catches —
    // we want OUR componentDidCatch tag to appear in at least one of them.
    const allMessages = consoleErrorSpy.mock.calls
      .map((args: unknown[]) => args.filter((a: unknown) => typeof a === 'string').join(' '))
      .join('|');
    expect(allMessages).toContain('TestScope');
  });
});
