declare module 'monaco-editor/esm/vs/editor/editor.api' {
  export * from 'monaco-editor';
}

declare module 'monaco-editor/esm/vs/basic-languages/powershell/powershell.contribution';

declare module 'monaco-editor/esm/vs/editor/editor.worker?worker' {
  const Worker: { new (): Worker };
  export default Worker;
}
