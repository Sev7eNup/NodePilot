import '@testing-library/jest-dom/vitest';
import { configure } from '@testing-library/react';
import { vi, beforeEach } from 'vitest';
import * as React from 'react';
import i18n from '../i18n';

// Raise the async-utility timeout (`waitFor` / `findBy*`) from Testing Library's 1000 ms
// default to 5000 ms. Coverage instrumentation slows MSW and React-Query round-trips enough
// to flake tests at the default timeout. This does not mask real failures: a condition that
// never becomes true still fails, just later. Paired with testTimeout=15000 in
// vitest.config.ts so the enclosing test has headroom above the wait.
configure({ asyncUtilTimeout: 5000 });

// Force a deterministic language for the whole suite; without this, jsdom falls back to
// navigator.language and UI-text assertions become runner-dependent. Pinned to 'en' since
// the tests assert English strings. The await matters: changeLanguage resolves on a later
// tick, so skipping it lets language selection race across tests. Language switching itself
// is covered by the i18n and Playwright e2e tests.
beforeEach(async () => {
  if (i18n.language !== 'en') await i18n.changeLanguage('en');
});

// Pin the designer to the 'classic' look for the whole unit suite. The component-test
// corpus asserts DOM produced by the classic rendering (canvas background variant, minimap
// chrome, root scope class). The Atelier design (designStore default 'atelier') has its own
// dedicated tests that set the store state explicitly.
// Some suites mock the designStore module with a bare hook, so there is no real zustand
// store to pin; only call setState when the store actually exists.
beforeEach(async () => {
  const { useDesignStore } = await import('../stores/designStore');
  if (typeof useDesignStore?.setState === 'function') {
    useDesignStore.setState({ designerTheme: 'classic' });
  }
});

// Monaco Editor depends on canvas, web workers, and ResizeObserver internals that jsdom
// can't provide cheaply. Replace the React wrapper with a minimal `<textarea>` so
// ScriptEditorDialog renders + behaves like a basic input in tests. Real Monaco is only
// exercised in the browser (Playwright e2e or manual).
vi.mock('@monaco-editor/react', () => {
  const Editor = ({ value, onChange }: { value?: string; onChange?: (v: string | undefined) => void }) =>
    React.createElement('textarea', {
      'data-testid': 'monaco-editor-mock',
      value: value ?? '',
      onChange: (e: React.ChangeEvent<HTMLTextAreaElement>) => onChange?.(e.target.value),
    });
  return {
    default: Editor,
    loader: { config: () => {}, init: () => Promise.resolve({}) },
  };
});

vi.mock('../lib/monacoSetup', () => ({
  // Plain string, unrelated to Monaco — present only so the mock exposes the same shape
  // as the real module. The `--font-mono` drift guard (fontTokens.test.ts) reads the real
  // file as source text, so it never sees this value.
  MONO_FONT_STACK:
    "'IBM Plex Mono', ui-monospace, 'Cascadia Code', Consolas, 'SFMono-Regular', Menlo, monospace",
  monaco: {
    editor: { defineTheme: () => {}, setModelMarkers: () => {} },
    languages: {
      registerCompletionItemProvider: () => ({ dispose: () => {} }),
      CompletionItemKind: { Variable: 4 },
    },
    Range: class {
      // Constructor params are unused — tests don't read range coordinates back from Monaco.
      // Erasable-syntax-only forbids parameter properties, so we accept-and-discard.
      constructor(_a?: number, _b?: number, _c?: number, _d?: number) { void _a; void _b; void _c; void _d; }
    },
    KeyMod: { CtrlCmd: 0 },
    KeyCode: { KeyS: 0 },
    MarkerSeverity: { Warning: 4 },
  },
}));

// jsdom doesn't ship `window.matchMedia`. The themeStore (transitively imported by
// most designer components via shared.tsx) reads it at module load to pick light/dark/auto,
// so we stub it once for the whole suite. Returns an "always light" media query.
if (typeof window !== 'undefined' && !window.matchMedia) {
  window.matchMedia = (q: string) => ({
    matches: false, media: q, onchange: null,
    addListener: () => {}, removeListener: () => {},
    addEventListener: () => {}, removeEventListener: () => {},
    dispatchEvent: () => false,
  });
}

// jsdom doesn't ship `ResizeObserver`. @xyflow/react reads it on mount to track viewport
// size, so without this stub the entire designer page errors out. We expose a no-op:
// observe/unobserve do nothing, which is fine because layout assertions don't depend on
// reported sizes anyway in tests.
if (typeof globalThis !== 'undefined' && !('ResizeObserver' in globalThis)) {
  globalThis.ResizeObserver = class {
    observe() {} unobserve() {} disconnect() {}
  };
}

// jsdom also lacks `DOMMatrix` and `DOMMatrixReadOnly`, which @xyflow/react uses for
// pan/zoom transforms. Provide a minimal stand-in so the renderer doesn't throw.
if (typeof globalThis !== 'undefined' && !('DOMMatrixReadOnly' in globalThis)) {
  // @ts-expect-error - minimal stub, the real type carries static factory methods we don't need
  globalThis.DOMMatrixReadOnly = class { m11=1; m22=1; e=0; f=0; };
}

// ECharts uses the SVG renderer in unit tests, but zrender still creates a 2D canvas solely to
// measure text. jsdom deliberately leaves getContext() unimplemented and writes to stderr on every
// call. A deterministic measureText-only context is the complete surface this SVG path needs.
const canvasMeasureContext = {
  measureText: (text: string) => ({ width: String(text).length * 8 }),
} as unknown as CanvasRenderingContext2D;
Object.defineProperty(HTMLCanvasElement.prototype, 'getContext', {
  configurable: true,
  writable: true,
  value: vi.fn(() => canvasMeasureContext),
});

// `@tanstack/react-virtual` measures the scroll container via getBoundingClientRect /
// offsetHeight to decide which items are in the viewport. jsdom returns 0 for both,
// which means a virtualised list renders nothing and assertions that look for items
// fail spuriously. We give the prototype sensible non-zero defaults so virtualised
// components produce DOM in tests. Real measurements stay correct in the browser.
Object.defineProperty(HTMLElement.prototype, 'offsetHeight', {
  configurable: true, get() { return 800; },
});
Object.defineProperty(HTMLElement.prototype, 'offsetWidth', {
  configurable: true, get() { return 600; },
});
const _origGetRect = HTMLElement.prototype.getBoundingClientRect;
HTMLElement.prototype.getBoundingClientRect = function () {
  const rect = _origGetRect.call(this) as DOMRect;
  // jsdom returns all-zero rects; only override when that's the case, so tests that
  // set explicit dimensions keep their own values.
  if (rect.width === 0 && rect.height === 0) {
    return { x: 0, y: 0, width: 600, height: 800, top: 0, left: 0, right: 600, bottom: 800, toJSON() { return this; } } as DOMRect;
  }
  return rect;
};
