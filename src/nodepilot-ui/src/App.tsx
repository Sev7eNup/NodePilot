import { lazy, Suspense, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { createBrowserRouter, Navigate, RouterProvider } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AppLayout } from './components/layout/AppLayout';
import { ErrorBoundary } from './components/ErrorBoundary';
import { ConfirmHost } from './components/common/ConfirmHost';
import { ToastHost } from './components/common/ToastHost';
import { DashboardPage } from './pages/DashboardPage';
import { WorkflowsPage } from './pages/WorkflowsPage';
import { LoginPage } from './pages/LoginPage';
import { useAuthStore } from './stores/authStore';
import { useThemeStore, applyTheme } from './stores/themeStore';
import { applyFavicon } from './lib/appIcon';

// Lazy-loaded heavy pages — only fetched/compiled on first navigation.
// Keeps the boot bundle lean (Dashboard + Workflows + Login eager covers the hot path).
const WorkflowEditorPage = lazy(() => import('./pages/WorkflowEditorPage').then(m => ({ default: m.WorkflowEditorPage })));
const ExecutionsPage = lazy(() => import('./pages/ExecutionsPage').then(m => ({ default: m.ExecutionsPage })));
const OperationsPage = lazy(() => import('./pages/OperationsPage').then(m => ({ default: m.OperationsPage })));
const AiChatPage = lazy(() => import('./pages/AiChatPage').then(m => ({ default: m.AiChatPage })));
const MachinesPage = lazy(() => import('./pages/MachinesPage').then(m => ({ default: m.MachinesPage })));
const GlobalVariablesPage = lazy(() => import('./pages/GlobalVariablesPage').then(m => ({ default: m.GlobalVariablesPage })));
const CustomActivitiesPage = lazy(() => import('./pages/CustomActivitiesPage').then(m => ({ default: m.CustomActivitiesPage })));
const MaintenanceWindowsPage = lazy(() => import('./pages/MaintenanceWindowsPage').then(m => ({ default: m.MaintenanceWindowsPage })));
const AlertingPage = lazy(() => import('./pages/AlertingPage').then(m => ({ default: m.AlertingPage })));
const SupportLogPage = lazy(() => import('./pages/SupportLogPage').then(m => ({ default: m.SupportLogPage })));
const SettingsPage = lazy(() => import('./pages/SettingsPage').then(m => ({ default: m.SettingsPage })));
const UsersPage = lazy(() => import('./pages/UsersPage').then(m => ({ default: m.UsersPage })));
const AuditLogPage = lazy(() => import('./pages/AuditLogPage').then(m => ({ default: m.AuditLogPage })));
const DbViewerPage = lazy(() => import('./pages/DbViewerPage').then(m => ({ default: m.DbViewerPage })));
const BackupPage = lazy(() => import('./pages/BackupPage').then(m => ({ default: m.BackupPage })));
const MetricsPage = lazy(() => import('./pages/MetricsPage').then(m => ({ default: m.MetricsPage })));

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 1,
      staleTime: 10_000,
      // By default React Query refetches every active query whenever the browser
      // tab regains focus. With many tabs/pages holding active list queries
      // (Executions, Audit, Dashboard), each tab switch would trigger a refetch
      // storm against the backend. Turned off because SignalR events already
      // invalidate the relevant caches precisely (see useSignalR).
      refetchOnWindowFocus: false,
    },
  },
});

// Apply persisted theme before first paint
applyTheme(useThemeStore.getState().theme);
// Recolor the browser-tab favicon to match the persisted skin pre-paint too, so the
// tab icon never flashes the wrong hue before React mounts.
applyFavicon(useThemeStore.getState().theme, useThemeStore.getState().resolvedTheme);

// Kick off the auth probe once at bundle load. The store flips `isAuthenticated` from
// null → true/false when the /auth/me call resolves. ProtectedRoute renders a loading
// shell while `isAuthenticated === null` so we don't flash the login page for a user
// whose cookie is still valid.
void useAuthStore.getState().initialize();

function ProtectedRoute({ children }: Readonly<{ children: React.ReactNode }>) {
  const { t } = useTranslation();
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  if (isAuthenticated === null) {
    return (
      <div className="min-h-screen flex items-center justify-center text-on-surface-variant">
        {t('common:loading')}
      </div>
    );
  }
  return isAuthenticated ? <>{children}</> : <Navigate to="/login" />;
}

/**
 * Admin-only route guard. Redirects non-Admin users to the dashboard silently.
 * The API layer enforces Admin-only on /api/users, this just hides the page.
 */
function AdminOnly({ children }: { children: React.ReactNode }) {
  const role = useAuthStore((s) => s.role);
  return role === 'Admin' ? <>{children}</> : <Navigate to="/" replace />;
}

function ThemeWatcher() {
  const theme = useThemeStore((s) => s.theme);
  const resolvedTheme = useThemeStore((s) => s.resolvedTheme);
  const syncResolved = useThemeStore((s) => s.syncResolved);
  // Recolor the favicon whenever the resolved skin changes (manual switch or OS
  // prefers-color-scheme flip under `system`). Mirrors the BrandLogo subscription.
  useEffect(() => {
    applyFavicon(theme, resolvedTheme);
  }, [theme, resolvedTheme]);
  useEffect(() => {
    if (theme !== 'system') return;
    const mq = globalThis.matchMedia('(prefers-color-scheme: dark)');
    mq.addEventListener('change', syncResolved);
    return () => mq.removeEventListener('change', syncResolved);
  }, [theme, syncResolved]);
  return null;
}

const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  {
    element: (
      <ProtectedRoute>
        <AppLayout />
      </ProtectedRoute>
    ),
    children: [
      { path: '/', element: <DashboardPage /> },
      { path: '/workflows', element: <WorkflowsPage /> },
      { path: '/workflows/:id', element: <WorkflowEditorPage /> },
      { path: '/executions', element: <ExecutionsPage /> },
      { path: '/operations', element: <OperationsPage /> },
      { path: '/ai-chat', element: <AiChatPage /> },
      { path: '/machines', element: <MachinesPage /> },
      { path: '/global-variables', element: <GlobalVariablesPage /> },
      { path: '/custom-activities', element: <CustomActivitiesPage /> },
      { path: '/maintenance-windows', element: <MaintenanceWindowsPage /> },
      { path: '/alerts', element: <AlertingPage /> },
      { path: '/metrics', element: <Navigate to="/metrics/mission-control" replace /> },
      { path: '/metrics/:section', element: <MetricsPage /> },
      { path: '/support-log', element: <AdminOnly><SupportLogPage /></AdminOnly> },
      { path: '/users', element: <AdminOnly><UsersPage /></AdminOnly> },
      { path: '/audit', element: <AdminOnly><AuditLogPage /></AdminOnly> },
      { path: '/database', element: <AdminOnly><DbViewerPage /></AdminOnly> },
      { path: '/backup', element: <AdminOnly><BackupPage /></AdminOnly> },
      { path: '/settings', element: <SettingsPage /> },
    ],
  },
]);

export default function App() {
  const { t } = useTranslation();
  return (
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <ThemeWatcher />
        <ToastHost />
        <ConfirmHost />
        <Suspense
          fallback={
            <div className="min-h-screen flex items-center justify-center text-on-surface-variant">
              {t('common:loading')}
            </div>
          }
        >
          <RouterProvider router={router} />
        </Suspense>
      </QueryClientProvider>
    </ErrorBoundary>
  );
}
