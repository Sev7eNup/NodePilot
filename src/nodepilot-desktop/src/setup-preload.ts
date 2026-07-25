import { contextBridge, ipcRenderer } from 'electron';

/**
 * The ONLY preload in the app, attached exclusively to the first-run setup window. It exposes a
 * single narrowly-scoped bridge: the renderer may submit a username + password, nothing more.
 * The bootstrap token is never exposed to the renderer — the main process reads it from disk and
 * attaches it to the login request itself.
 */
export interface SetupResult {
  ok: boolean;
  error?: string;
}

contextBridge.exposeInMainWorld('nodepilotSetup', {
  completeAdminSetup: (credentials: { username: string; password: string }): Promise<SetupResult> =>
    ipcRenderer.invoke('setup:complete', {
      username: String(credentials?.username ?? ''),
      password: String(credentials?.password ?? ''),
    }),
});
