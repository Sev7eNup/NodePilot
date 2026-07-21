import { useState, useEffect, useRef, lazy, Suspense } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Sidebar } from './Sidebar';
import { TopBar } from './TopBar';
import { ScorchEasterEgg } from '../easter-eggs/ScorchEasterEgg';
import { useIsMobile } from '../../hooks/useMediaQuery';

// Lazy so @xyflow stays out of the main bundle — only loaded when a phone user opens a
// workflow. The full editor route keeps rendering for desktop via <Outlet/>.
const MobileWorkflowView = lazy(() =>
  import('../../pages/MobileWorkflowView').then((m) => ({ default: m.MobileWorkflowView })),
);

const KONAMI = ['ArrowUp', 'ArrowUp', 'ArrowDown', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'ArrowLeft', 'ArrowRight', 'b', 'a'];

export function AppLayout() {
  const location = useLocation();
  const { t } = useTranslation(['nav']);
  const isMobile = useIsMobile();
  const [showScorch, setShowScorch] = useState(false);
  // Transient: the mobile nav drawer. Deliberately not persisted — it must start closed
  // on every load and auto-close on navigation (effect below) and on backdrop click.
  const [drawerOpen, setDrawerOpen] = useState(false);
  const konamiIdx = useRef(0);

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === KONAMI[konamiIdx.current]) {
        konamiIdx.current++;
        if (konamiIdx.current === KONAMI.length) {
          setShowScorch(true);
          konamiIdx.current = 0;
        }
      } else {
        konamiIdx.current = e.key === KONAMI[0] ? 1 : 0;
      }
    };
    globalThis.addEventListener('keydown', handler);
    return () => globalThis.removeEventListener('keydown', handler);
  }, []);

  // Close the mobile drawer whenever the route changes (tapping a nav link navigates).
  useEffect(() => {
    setDrawerOpen(false);
  }, [location.pathname]);

  const editorMatch = location.pathname.match(/^\/workflows\/([^/]+)$/);

  if (editorMatch) {
    return (
      <>
        {isMobile ? (
          // Phones get a read-only, pannable graph with live status; editing is desktop-only.
          <Suspense fallback={null}>
            <MobileWorkflowView workflowId={editorMatch[1]} />
          </Suspense>
        ) : (
          <Outlet />
        )}
        {showScorch && <ScorchEasterEgg onClose={() => setShowScorch(false)} />}
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
      {showScorch && <ScorchEasterEgg onClose={() => setShowScorch(false)} />}
    </>
  );
}
