import { Component, type ErrorInfo, type ReactNode } from 'react';

type FallbackRenderer = (error: Error, reset: () => void) => ReactNode;

type Props = {
  children: ReactNode;
  /** Optional custom fallback. When provided, replaces the default full-page error UI —
   *  useful for nesting a boundary inside a page so a partial crash doesn't blank the
   *  entire app. */
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

  private readonly handleReload = () => {
    globalThis.location.reload();
  };

  render() {
    if (!this.state.error) return this.props.children;

    if (this.props.fallback) {
      return this.props.fallback(this.state.error, this.handleReset);
    }

    return (
      <div
        role="alert"
        className="min-h-screen flex items-center justify-center bg-surface p-6"
      >
        <div className="max-w-lg w-full rounded-lg border border-error/30 bg-surface-container p-6 shadow-sm">
          <h1 className="text-xl font-semibold text-error mb-2">Unerwarteter Fehler</h1>
          <p className="text-sm text-on-surface-variant mb-4">
            Die Oberfläche konnte nicht gerendert werden. Der Fehler wurde in der Browser-Konsole protokolliert.
          </p>
          <pre className="text-xs bg-surface-container-high text-on-surface p-3 rounded overflow-auto max-h-48 mb-4">
            {this.state.error.message}
          </pre>
          <div className="flex gap-2">
            <button
              type="button"
              onClick={this.handleReset}
              className="px-3 py-1.5 text-sm rounded bg-primary text-on-primary hover:opacity-90"
            >
              Erneut versuchen
            </button>
            <button
              type="button"
              onClick={this.handleReload}
              className="px-3 py-1.5 text-sm rounded border border-outline text-on-surface hover:bg-surface-container-high"
            >
              Seite neu laden
            </button>
          </div>
        </div>
      </div>
    );
  }
}
