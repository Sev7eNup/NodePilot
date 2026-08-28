import { contextBridge, ipcRenderer } from 'electron';

/**
 * The app's only preload script, attached exclusively to the first-run setup window. It exposes
 * a single narrow bridge: the renderer may submit a username and password, nothing more. The
 * bootstrap token stays out of the renderer; the main process reads it from disk and attaches
 * it to the login request itself.
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
