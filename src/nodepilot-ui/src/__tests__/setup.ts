import '@testing-library/jest-dom/vitest';
import { configure } from '@testing-library/react';
import { vi, beforeEach } from 'vitest';
import * as React from 'react';
import i18n from '../i18n';

// Raise the default async-utility timeout (`waitFor` / `findBy*`) from Testing Library's
// 1000 ms default to 5000 ms. Under v8 coverage instrumentation on the 2-core CI runners,
// MSW + React-Query mutation round-trips intermittently exceed 1000 ms, which flaked a
// varying 4-5 of the ~89 tests in WorkflowEditorPage.test.tsx every run (main was red on
// every push since 2026-07-02). This does NOT mask real failures — a condition that never
// becomes true still fails, just after 5 s instead of 1 s. Paired with testTimeout=15000 in
// vitest.config.ts so the enclosing test has headroom above the wait.
configure({ asyncUtilTimeout: 5000 });

// Force a deterministic language for the entire suite. Tests assert against UI text;
// without this, the language detector picks navigator.language (usually 'en-US') in
// jsdom, which would silently flip strings depending on the test runner host.
//
// Pinned to 'en': the component-test corpus was written asserting English UI strings.
// This MUST be awaited — i18next.changeLanguage resolves on a later tick, so the old
// un-awaited call left the language racing (early tests rendered the 'de' fallback, later
// ones 'en'), which silently flipped strings by test order/load and made a handful of
// tests flaky. The language switch itself is exercised by the dedicated i18n + Playwright
// e2e tests.
beforeEach(async () => {
  if (i18n.language !== 'en') await i18n.changeLanguage('en');
});

// Pin the designer to the CLASSIC look for the whole unit suite. The component-test corpus
// asserts DOM the classic rendering produces (canvas background variant, minimap chrome,
// root scope class). The Atelier design (designStore default 'atelier') is exercised by its
// own dedicated tests, which set the store state explicitly.
// Some suites vi.mock() the designStore module with a bare hook — then there is no real
// zustand store (and nothing to pin), so only touch setState when it actually exists.
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
  // jsdom returns all-zero rects; only override when that's the case so individual
  // tests that *do* set explicit dimensions keep their values.
  if (rect.width === 0 && rect.height === 0) {
    return { x: 0, y: 0, width: 600, height: 800, top: 0, left: 0, right: 600, bottom: 800, toJSON() { return this; } } as DOMRect;
  }
  return rect;
};
