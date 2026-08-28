// Monaco is imported from sub-paths instead of the root entry so the bundle
// gets editor-core plus only the PowerShell tokenizer, not the full
// editor.main with every basic language registered. The sub-paths resolve
// through monaco-editor's `"./*"` export wildcard, but TS bundler-mode
// resolution does not pick up the adjacent .d.ts files; the ambient
// declaration in `src/lib/monacoTypes.d.ts` re-exports the public types onto
// these paths.
//
// The exports map points the wildcard at `./esm/vs/*.js`, so paths carry no
// `esm/vs/` prefix, and per-language side-effect registration lives under
// `languages/definitions/<lang>/register`.
import * as monaco from 'monaco-editor/editor/editor.api';
import 'monaco-editor/languages/definitions/powershell/register';
import EditorWorker from 'monaco-editor/editor/editor.worker?worker';
import { loader } from '@monaco-editor/react';

self.MonacoEnvironment = {
  getWorker: () => new EditorWorker(),
};

loader.config({ monaco });

/**
 * The mono stack written out, the one deliberate duplicate of `--font-mono` in
 * index.css.
 *
 * Monaco cannot use a CSS variable here: it measures character widths itself in
 * JS (cursor position, selection, column math) and sanitizes the value first,
 * which a `var(--font-mono)` does not survive. CodeMirror has no such limit and
 * references the token directly.
 *
 * `fontTokens.test.ts` keeps both sides in sync.
 */
export const MONO_FONT_STACK =
  "'IBM Plex Mono', ui-monospace, 'Cascadia Code', Consolas, 'SFMono-Regular', Menlo, monospace";

export { monaco };
