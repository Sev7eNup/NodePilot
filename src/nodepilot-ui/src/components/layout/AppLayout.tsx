import { useState, useEffect, lazy, Suspense } from 'react';
import { Outlet, useLocation } from 'react-router';
import { useTranslation } from 'react-i18next';
import { Sidebar } from './Sidebar';
import { TopBar } from './TopBar';
import { useIsMobile } from '../../hooks/useMediaQuery';
import { AiChatWidget } from '../ai/AiChatWidget';

// Lazy so @xyflow stays out of the main bundle and loads only when a phone user opens a
// workflow. Desktop keeps rendering the full editor route through <Outlet/>.
const MobileWorkflowView = lazy(() =>
  import('../../pages/MobileWorkflowView').then((m) => ({ default: m.MobileWorkflowView })),
);

export function AppLayout() {
  return (
    <>
      <div id="np-app-content"><AppContent /></div>
      <AiChatWidget />
    </>
  );
}

function AppContent() {
  const location = useLocation();
  const { t } = useTranslation(['nav']);
  const isMobile = useIsMobile();
  // Open state of the mobile nav drawer. Not persisted: it starts closed on every load and
  // closes again on navigation (effect below) and on backdrop click.
  const [drawerOpen, setDrawerOpen] = useState(false);
  // Workflow whose desktop editor is mounted. Narrowing the window keeps that editor, because
  // swapping in the mobile view would unmount it and drop its unsaved draft.
  const [desktopEditorId, setDesktopEditorId] = useState<string | null>(null);

  // Close the mobile drawer whenever the route changes (tapping a nav link navigates).
  useEffect(() => {
    setDrawerOpen(false);
  }, [location.pathname]);

  const editorMatch = location.pathname.match(/^\/workflows\/([^/]+)$/);
  const editorId = editorMatch?.[1] ?? null;
  const showMobileEditor = isMobile && (editorId === null || editorId !== desktopEditorId);
  const mountedDesktopEditor = editorId !== null && !showMobileEditor ? editorId : null;
  if (mountedDesktopEditor !== desktopEditorId) setDesktopEditorId(mountedDesktopEditor);

  if (editorMatch) {
    return (
      <>
        {showMobileEditor ? (
          // Phones get a read-only, pannable graph with live status. Editing is desktop only.
          <Suspense fallback={null}>
            <MobileWorkflowView workflowId={editorMatch[1]} />
          </Suspense>
        ) : (
          <Outlet />
        )}
      </>
    );
  }

  return (
    <>
      <div className="np-shell flex h-screen bg-surface">
        {/* Backdrop: mobile only, behind the drawer (z-40) but above content. */}
        {drawerOpen && (
          <button
            type="button"
            aria-label={t('nav:closeMenu')}
            onClick={() => setDrawerOpen(false)}
            className="fixed inset-0 z-30 bg-black/40 lg:hidden"
          />
        )}
        <Sidebar mobileOpen={drawerOpen} onClose={() => setDrawerOpen(false)} />
        <main className="flex-1 flex flex-col overflow-hidden bg-surface-low min-w-0">
          <TopBar onOpenMenu={() => setDrawerOpen(true)} />
          <div id="np-main-scroll" className="flex-1 overflow-auto">
            <div className="p-3 sm:p-4 lg:p-6">
              <Outlet />
            </div>
          </div>
        </main>
      </div>
    </>
  );
}
