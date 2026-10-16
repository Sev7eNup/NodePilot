import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { createMemoryRouter, Outlet } from 'react-router';
import { RouterProvider } from 'react-router/dom';
import { ErrorBoundary, RouteErrorFallback } from '../../components/ErrorBoundary';

// Suppress the console noise React emits when a child throws on purpose.
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
    expect(screen.getByText('Unexpected error')).toBeInTheDocument();
    expect(screen.getByText('kaboom default')).toBeInTheDocument();
  });

  it('renders the custom fallback when one is provided', () => {
    render(
      <ErrorBoundary fallback={(err) => <div>Custom: {err.message}</div>}>
        <Boom message="kaboom custom" />
      </ErrorBoundary>,
    );
    expect(screen.getByText('Custom: kaboom custom')).toBeInTheDocument();
    // The default fallback must not render when a custom fallback is provided.
    expect(screen.queryByText('Unexpected error')).not.toBeInTheDocument();
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
    // React emits its own console.error calls when a boundary catches, so the scope tag from
    // componentDidCatch only has to appear in one of them.
    const allMessages = consoleErrorSpy.mock.calls
      .map((args: unknown[]) => args.filter((a: unknown) => typeof a === 'string').join(' '))
      .join('|');
    expect(allMessages).toContain('TestScope');
  });
});

describe('RouteErrorFallback', () => {
  it('shows a route render error in the localized fallback and retries the route', () => {
    let shouldThrow = true;
    function Page() {
      if (shouldThrow) throw new Error('route kaboom');
      return <p>Page content</p>;
    }
    const router = createMemoryRouter([
      { element: <Outlet />, errorElement: <RouteErrorFallback />, children: [{ path: '/', element: <Page /> }] },
    ]);
    render(<RouterProvider router={router} />);

    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByText('Unexpected error')).toBeInTheDocument();
    expect(screen.getByText('route kaboom')).toBeInTheDocument();
    expect(screen.queryByText(/Unexpected Application Error/i)).not.toBeInTheDocument();

    shouldThrow = false;
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(screen.getByText('Page content')).toBeInTheDocument();
  });

  it('shows an unmatched path in the localized fallback', () => {
    const router = createMemoryRouter(
      [{ element: <Outlet />, errorElement: <RouteErrorFallback />, children: [{ path: '/', element: <p>Home</p> }] }],
      { initialEntries: ['/missing'] },
    );
    render(<RouterProvider router={router} />);

    expect(screen.getByText('Unexpected error')).toBeInTheDocument();
    expect(screen.getByText('404 Not Found')).toBeInTheDocument();
  });
});
