import { Component, type ErrorInfo, type ReactNode } from 'react';
import { isRouteErrorResponse, useLocation, useNavigate, useRouteError } from 'react-router';
import i18n from '../i18n';

type FallbackRenderer = (error: Error, reset: () => void) => ReactNode;

type Props = {
  children: ReactNode;
  /** Optional custom fallback that replaces the default full-page error UI. Lets a boundary
   *  sit inside a page so a partial crash does not blank the whole app. */
  fallback?: FallbackRenderer;
  /** Tag for the console log so nested boundaries are distinguishable in DevTools. */
  scope?: string;
};
type State = { error: Error | null };

export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    const scope = this.props.scope ?? 'ErrorBoundary';
    console.error(`[${scope}] render failure`, error, info.componentStack);
  }

  private readonly handleReset = () => {
    this.setState({ error: null });
  };

  render() {
    if (!this.state.error) return this.props.children;

    if (this.props.fallback) {
      return this.props.fallback(this.state.error, this.handleReset);
    }

    return <ErrorFallback message={this.state.error.message} onRetry={this.handleReset} />;
  }
}

/** The localized full-page error UI shared by the app boundary and the router's error element. */
function ErrorFallback({ message, onRetry }: Readonly<{ message: string; onRetry: () => void }>) {
  return (
    <div
      role="alert"
      className="min-h-screen flex items-center justify-center bg-surface p-6"
    >
      <div className="max-w-lg w-full rounded-lg border border-error/30 bg-surface-container p-6 shadow-sm">
        <h1 className="text-xl font-semibold text-error mb-2">{i18n.t('common:errorBoundary.title')}</h1>
        <p className="text-sm text-on-surface-variant mb-4">
          {i18n.t('common:errorBoundary.message')}
        </p>
        <pre className="text-xs bg-surface-container-high text-on-surface p-3 rounded overflow-auto max-h-48 mb-4">
          {message}
        </pre>
        <div className="flex gap-2">
          <button
            type="button"
            onClick={onRetry}
            className="px-3 py-1.5 text-sm rounded bg-primary text-on-primary hover:opacity-90"
          >
            {i18n.t('common:retry')}
          </button>
          <button
            type="button"
            onClick={() => globalThis.location.reload()}
            className="px-3 py-1.5 text-sm rounded border border-outline text-on-surface hover:bg-surface-container-high"
          >
            {i18n.t('common:errorBoundary.reloadPage')}
          </button>
        </div>
      </div>
    </div>
  );
}

function routeErrorMessage(error: unknown): string {
  if (isRouteErrorResponse(error)) return `${error.status} ${error.statusText}`.trim();
  if (error instanceof Error) return error.message;
  return String(error);
}

/**
 * Error element for the router. A data router catches render errors itself, so they never reach
 * the app boundary; this shows them in the same localized UI. Retry re-enters the current location,
 * which makes the router drop the caught error and render the route again.
 */
export function RouteErrorFallback() {
  const error = useRouteError();
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <ErrorFallback
      message={routeErrorMessage(error)}
      onRetry={() => void navigate(
        { pathname: location.pathname, search: location.search, hash: location.hash },
        { replace: true },
      )}
    />
  );
}
