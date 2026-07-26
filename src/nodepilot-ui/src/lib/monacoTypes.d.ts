declare module 'monaco-editor/editor/editor.api' {
  export * from 'monaco-editor';
}

declare module 'monaco-editor/languages/definitions/powershell/register';

declare module 'monaco-editor/editor/editor.worker?worker' {
  const Worker: { new (): Worker };
  export default Worker;
}
