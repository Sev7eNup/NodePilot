import { Fragment } from 'react';
import { useTranslation } from 'react-i18next';
import { Navigate } from 'react-router';
import { useAuthStore } from '../stores/authStore';

export function ProtectedRoute({ children }: Readonly<{ children: React.ReactNode }>) {
  const { t } = useTranslation();
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const userId = useAuthStore((state) => state.userId);
  const authBoundaryEpoch = useAuthStore((state) => state.authBoundaryEpoch);

  if (isAuthenticated === null) {
    return (
      <div className="min-h-screen flex items-center justify-center text-on-surface-variant">
        {t('common:loading')}
      </div>
    );
  }

  if (!isAuthenticated) return <Navigate to="/login" />;

  // React can batch clear(false) and accept(true) into a single render when the signed-in identity
  // changes. This key forces the whole protected subtree to unmount and remount, dropping
  // component-local SQL and results and running AbortController cleanup for AI and SSE consumers.
  return (
    <Fragment key={`${authBoundaryEpoch}:${userId ?? 'unknown'}`}>
      {children}
    </Fragment>
  );
}
