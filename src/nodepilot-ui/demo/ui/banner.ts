/**
 * The demo notice.
 *
 * Plain DOM rather than a React component, so the product's tree is not touched: the banner
 * lives beside `#root`, outside anything the app renders or re-renders.
 *
 * Reset rebuilds the seed world. Because the state is per-tab and in memory, a reload does
 * the same thing — the button just makes that discoverable.
 */
import './demo.css';
import { demoStrings } from './strings';

const HIDDEN_CLASS = 'np-demo-bar--hidden';

/**
 * Build marker, written into the DOM so it survives into the shipped chunk.
 *
 * CI asserts it is present in the demo bundle and absent from the product bundle. As an
 * unused export it was tree-shaken out of both, which made the product half of that check
 * pass for the wrong reason — it proved nothing.
 */
export const DEMO_BUILD_SENTINEL = 'nodepilot-browser-demo-8f3a21c6';

function button(label: string, title: string, className: string, onClick: () => void): HTMLButtonElement {
  const element = document.createElement('button');
  element.type = 'button';
  element.className = className;
  element.textContent = label;
  element.title = title;
  element.addEventListener('click', onClick);
  return element;
}

/**
 * Renders the banner and returns a disposer. `onReset` is called when the visitor asks for
 * the sample data back.
 */
export function mountDemoBanner(onReset: () => void, onTour?: () => void): () => void {
  const strings = demoStrings();

  const bar = document.createElement('div');
  bar.className = 'np-demo-bar';
  bar.setAttribute('role', 'status');
  bar.dataset.demoBuild = DEMO_BUILD_SENTINEL;

  const badge = document.createElement('span');
  badge.className = 'np-demo-bar__badge';
  badge.textContent = strings.badge;

  const message = document.createElement('span');
  message.className = 'np-demo-bar__message';
  message.textContent = strings.message;

  const actions = document.createElement('span');
  actions.className = 'np-demo-bar__actions';

  // Back to the project website, which sits one level above the demo on GitHub Pages.
  // `target` is deliberately not `_self`: the anchor guard in net/anchors.ts routes every
  // same-origin link that would leave the demo through the patched fetch instead.
  const home = document.createElement('a');
  home.className = 'np-demo-bar__button np-demo-bar__button--quiet np-demo-bar__home';
  home.href = '../';
  home.target = '_blank';
  home.rel = 'noopener noreferrer';
  home.textContent = strings.website;
  home.title = strings.website;

  const restore = button(strings.restore, strings.restore, 'np-demo-pill', () => {
    bar.classList.remove(HIDDEN_CLASS);
    restore.classList.add(HIDDEN_CLASS);
  });
  restore.classList.add(HIDDEN_CLASS);

  const resetButton = button(strings.reset, strings.resetTitle, 'np-demo-bar__button', onReset);
  const dismissButton = button(strings.dismiss, strings.dismiss, 'np-demo-bar__button np-demo-bar__button--quiet', () => {
    bar.classList.add(HIDDEN_CLASS);
    restore.classList.remove(HIDDEN_CLASS);
  });
  const tourButton = onTour ? button(strings.tour, strings.tour, 'np-demo-bar__button', onTour) : null;

  actions.append(home, resetButton, dismissButton);
  if (tourButton) actions.prepend(tourButton);

  bar.append(badge, message, actions);
  document.body.append(bar, restore);

  // The banner is plain DOM and is built once, so a visitor switching the language inside the
  // app left it in the language it started in. The app writes its language onto <html>, which
  // is the signal this follows.
  const applyStrings = (): void => {
    const next = demoStrings();
    badge.textContent = next.badge;
    message.textContent = next.message;
    home.textContent = next.website;
    home.title = next.website;
    resetButton.textContent = next.reset;
    resetButton.title = next.resetTitle;
    dismissButton.textContent = next.dismiss;
    dismissButton.title = next.dismiss;
    restore.textContent = next.restore;
    restore.title = next.restore;
    if (tourButton) {
      tourButton.textContent = next.tour;
      tourButton.title = next.tour;
    }
  };
  const languageWatch = new MutationObserver(applyStrings);
  languageWatch.observe(document.documentElement, { attributes: true, attributeFilter: ['lang'] });

  return () => {
    languageWatch.disconnect();
    bar.remove();
    restore.remove();
  };
}
