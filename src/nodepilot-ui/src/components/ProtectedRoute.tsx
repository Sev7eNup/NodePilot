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

  // React can batch clear(false) + accept(true) into one render during a local A→B identity
  // switch. This key still forces the complete protected subtree to unmount/remount, dropping
  // component-local SQL/results and running AbortController cleanup for AI/SSE consumers.
  return (
    <Fragment key={`${authBoundaryEpoch}:${userId ?? 'unknown'}`}>
      {children}
    </Fragment>
  );
}
