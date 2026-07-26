// We import Monaco from sub-paths instead of the root entry so the bundle gets
// editor-core + ONLY the PowerShell tokenizer (~1.5 MB minified) instead of the
// full editor.main with every basic-language registered (~3 MB). The sub-paths
// resolve through monaco-editor's `"./*"` export wildcard but TS bundler-mode
// resolution doesn't pick up the adjacent .d.ts files; the ambient declaration
// in `src/lib/monacoTypes.d.ts` re-exports the public types onto these paths.
//
// 0.56 added an exports map that already points the wildcard at `./esm/vs/*.js`,
// so the old `esm/vs/` prefix would resolve twice and must be dropped. The same
// release moved the per-language side-effect registration from
// `basic-languages/<lang>/<lang>.contribution` to `languages/definitions/<lang>/register`.
import * as monaco from 'monaco-editor/editor/editor.api';
import 'monaco-editor/languages/definitions/powershell/register';
import EditorWorker from 'monaco-editor/editor/editor.worker?worker';
import { loader } from '@monaco-editor/react';

self.MonacoEnvironment = {
  getWorker: () => new EditorWorker(),
};

loader.config({ monaco });

export { monaco };
