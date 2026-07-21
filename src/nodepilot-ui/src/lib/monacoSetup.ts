// We import Monaco from sub-paths instead of the root entry so the bundle gets
// editor-core + ONLY the PowerShell tokenizer (~1.5 MB minified) instead of the
// full editor.main with every basic-language registered (~3 MB). The sub-paths
// resolve through monaco-editor's `"./*"` export wildcard but TS bundler-mode
// resolution doesn't pick up the adjacent .d.ts files; the ambient declaration
// in `src/lib/monacoTypes.d.ts` re-exports the public types onto these paths.
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api';
import 'monaco-editor/esm/vs/basic-languages/powershell/powershell.contribution';
import EditorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker';
import { loader } from '@monaco-editor/react';

self.MonacoEnvironment = {
  getWorker: () => new EditorWorker(),
};

loader.config({ monaco });

export { monaco };
