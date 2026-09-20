/**
 * Entry point of the browser demo.
 *
 * The order here is the whole design. Static imports are evaluated before any module body
 * runs, so the app cannot be imported statically: `src/main.tsx` issues a request while it is
 * being evaluated, and that request has to meet an installed backend. Everything is wired up
 * first, then the app is pulled in dynamically.
 *
 * This file is also why the demo cannot reach the product bundle: `index.html` never names
 * it, so it is not an input to the product build. Reachability, not dead-code elimination.
 */
// First, and deliberately: it seeds the language and skin keys that i18n and the theme
// store read while they initialise.
import './boot/preferences';
import { isolateAuthBoundaryTransport } from '../src/security/authBoundary';
import { setExecutionHubFactory } from '../src/lib/hubConnection';
import { setDocsHref } from '../src/lib/docsLink';
import { configureWorld } from './state/world';
import { buildWorld } from './seed/build';
import { installDemoBackend } from './net/install';
import { installAnchorGuard } from './net/anchors';
import { demoRoutes } from './handlers';
import { createFakeHubConnection } from './hub/fakeHub';
import { mountDemoBanner } from './ui/banner';
import { mountTour } from './ui/tour';

function bootstrap(): void {
  // Several tabs of one browser must not disturb each other. Opening or reloading a tab
  // publishes an identity event, and every other tab answers it by clearing caches and
  // remounting, which throws away unsaved editor state.
  isolateAuthBoundaryTransport();

  configureWorld(() => buildWorld());
  installDemoBackend(demoRoutes);
  // A plain anchor never goes through the patched fetch; without this, the audit-log export
  // links navigate the tab out of the demo and onto the host's 404.
  installAnchorGuard();
  setExecutionHubFactory(createFakeHubConnection);

  // On GitHub Pages the documentation sits beside the demo, not above it.
  setDocsHref('../docs/');

  // The world is per-tab and in memory, so a reload is the reset. Doing it this way also
  // clears the query cache, which rebuilding the world in place would not.
  const tour = mountTour();
  mountDemoBanner(() => { globalThis.location.reload(); }, () => tour.start());
}

void (async () => {
  bootstrap();
  await import('../src/main.tsx');
})();
