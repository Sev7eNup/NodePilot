import { app, BrowserWindow, Tray, Menu, dialog, ipcMain, nativeImage, net, session } from 'electron';
import { existsSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { loadDesktopConfig, type DesktopConfig } from './config';
import { hardenSession, hardenWindow } from './security';
import { defaultIcons, skinIconsForFavicons, type SkinIcons } from './skins';

/** Persistent session partition shared by the login request, the setup window, and the SPA window
 *  so the auth cookies set during first-run setup are available to the app. */
const PARTITION = 'persist:nodepilot';

/** Generated icon set — sits next to dist/ inside the asar (see
 * scripts/generate-desktop-icons.ps1). */
const ASSETS_DIR = join(__dirname, '..', 'assets');

/** Per-user handoff copy of the admin bootstrap token, written by the elevated installer into the
 *  profile of the interactive user, which is the user this process runs as. The real token under
 *  ProgramData is SYSTEM-owned and unreadable here. The installer resolves the interactive user
 *  explicitly, because an elevated installer does not always run under that account. */
const HANDOFF_PATH = join(process.env.LOCALAPPDATA ?? '', 'NodePilot', 'admin-setup.handoff');

/** Must stay above the provisioner's own readiness budget (180 s in Provision-LocalDb.ps1), so a
 *  backend that is still running the first EF migration against a fresh cluster is not reported
 *  as dead. */
const READY_TIMEOUT_MS = 240_000;

let config: DesktopConfig;
let mainWindow: BrowserWindow | null = null;
let setupWindow: BrowserWindow | null = null;
let splashWindow: BrowserWindow | null = null;
let tray: Tray | null = null;
let quitting = false;
/** Icon pair currently in use. Starts on the build-time default (blue) and follows the SPA's
 *  color skin from its first favicon report onwards. */
let icons: SkinIcons = defaultIcons(ASSETS_DIR);

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

function appSession(): Electron.Session {
  return session.fromPartition(PARTITION);
}

// -------------------------------------------------------------------------------------------
// Single-instance: a second launch focuses the existing window instead of starting a rival shell.
// -------------------------------------------------------------------------------------------
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.setAppUserModelId('com.nodepilot.desktop');

  app.on('second-instance', () => {
    if (setupWindow) { focus(setupWindow); return; }
    openMainWindow();
  });

  // Stay resident in the tray when all windows are closed (background services keep running too).
  app.on('window-all-closed', () => { /* intentional no-op: quit only via the tray */ });

  app.whenReady().then(bootstrap).catch(fatal);

  ipcMain.handle('setup:complete', handleSetupComplete);
}

function fatal(err: unknown): void {
  const message = err instanceof Error ? err.message : String(err);
  dialog.showErrorBox('NodePilot', `NodePilot could not start:\n\n${message}`);
  quitting = true;
  app.quit();
}

async function bootstrap(): Promise<void> {
  config = loadDesktopConfig();
  hardenSession(appSession(), config.certificateSha256);

  showSplash();
  const ready = await waitForReady(config.origin, READY_TIMEOUT_MS);
  closeSplash();

  if (!ready) {
    fatal(new Error(
      `The NodePilot backend did not become ready within ${READY_TIMEOUT_MS / 1000} seconds.\n\n` +
      `Check the "${config.serviceName}" service in services.msc, and the logs under ` +
      `%ProgramData%\\NodePilot\\logs (installation issues: %TEMP%\\nodepilot-provision.log).\n\n` +
      'If the service is running but slow to start, closing this and launching NodePilot again is enough.'));
    return;
  }

  createTray();

  // First run: the installer left a bootstrap-token handoff -> show the local setup page.
  if (existsSync(HANDOFF_PATH)) {
    openSetupWindow();
  } else {
    openMainWindow();
  }
}

// -------------------------------------------------------------------------------------------
// Health gate
// -------------------------------------------------------------------------------------------
function httpStatus(url: string, method: 'GET' | 'POST', headers?: Record<string, string>, body?: string): Promise<number> {
  return new Promise((resolve, reject) => {
    const request = net.request({ url, method, session: appSession() });
    if (headers) for (const [k, v] of Object.entries(headers)) request.setHeader(k, v);
    request.on('response', (response) => {
      response.on('data', () => { /* drain */ });
      response.on('end', () => resolve(response.statusCode));
    });
    request.on('error', reject);
    if (body !== undefined) request.write(body);
    request.end();
  });
}

async function waitForReady(origin: string, timeoutMs: number): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      if (await httpStatus(`${origin}/healthz/ready`, 'GET') === 200) return true;
    } catch { /* service still starting */ }
    await delay(2000);
  }
  return false;
}

// -------------------------------------------------------------------------------------------
// First-run admin setup — the bootstrap token never reaches the renderer.
// -------------------------------------------------------------------------------------------
async function handleSetupComplete(
  event: Electron.IpcMainInvokeEvent,
  credentials: { username: string; password: string },
): Promise<{ ok: boolean; error?: string }> {
  // Accept only from the dedicated setup window's web contents.
  if (!setupWindow || event.sender !== setupWindow.webContents) {
    return { ok: false, error: 'Unauthorized setup request.' };
  }

  const username = String(credentials?.username ?? '').trim();
  const password = String(credentials?.password ?? '');
  if (username.length < 1) return { ok: false, error: 'A username is required.' };
  if (password.length < 8) return { ok: false, error: 'The password must be at least 8 characters.' };

  let token: string;
  try {
    token = readFileSync(HANDOFF_PATH, 'utf8').trim();
  } catch {
    return { ok: false, error: 'The setup token was not found. Please reinstall NodePilot.' };
  }
  if (!token) return { ok: false, error: 'The setup token is empty. Please reinstall NodePilot.' };

  let status: number;
  try {
    status = await httpStatus(
      `${config.origin}/api/auth/login`,
      'POST',
      { 'Content-Type': 'application/json', 'X-Setup-Token': token },
      JSON.stringify({ username, password }),
    );
  } catch (e) {
    return { ok: false, error: (e as Error).message };
  }

  if (status !== 200) {
    return { ok: false, error: `Setup login failed (HTTP ${status}).` };
  }

  // Success: the server consumed its one-shot token; remove the local handoff copy too.
  try { rmSync(HANDOFF_PATH, { force: true }); } catch { /* best-effort */ }

  // Swap the setup window for the real SPA window (auth cookies are already in the shared session).
  setTimeout(() => {
    const toClose = setupWindow;
    setupWindow = null;
    openMainWindow();
    toClose?.close();
  }, 50);

  return { ok: true };
}

// -------------------------------------------------------------------------------------------
// Icons — the shell's window + tray icon track the color skin selected in the SPA.
// -------------------------------------------------------------------------------------------
/** `undefined` (not an empty image) when the asset is missing, so Electron keeps its default
 *  rather than being handed a blank icon. */
function windowIcon(): Electron.NativeImage | undefined {
  const image = nativeImage.createFromPath(icons.window);
  return image.isEmpty() ? undefined : image;
}

function trayIcon(): Electron.NativeImage {
  const candidate = nativeImage.createFromPath(icons.tray);
  if (!candidate.isEmpty()) return candidate;
  // 1x1 fallback so Tray construction never fails on a build missing the asset.
  return nativeImage.createFromDataURL(
    'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==');
}

/**
 * Repaints every icon after the SPA reported a new favicon. The SPA rewrites `<link rel="icon">`
 * to `/appicon-<skin>.png` on every skin switch, which Chromium surfaces here — no preload and no
 * IPC channel on the production window. An unknown skin (or a build without the generated set)
 * simply leaves the current icon in place.
 */
function applySkinIcons(favicons: readonly string[]): void {
  const next = skinIconsForFavicons(ASSETS_DIR, favicons);
  if (!next || next.window === icons.window) return;
  icons = next;

  const image = windowIcon();
  if (image) {
    for (const win of [mainWindow, setupWindow]) {
      if (win && !win.isDestroyed()) win.setIcon(image);
    }
  }
  try { tray?.setImage(trayIcon()); } catch { /* tray already destroyed */ }
}

// -------------------------------------------------------------------------------------------
// Windows
// -------------------------------------------------------------------------------------------
function baseWebPreferences(): Electron.WebPreferences {
  return {
    contextIsolation: true,
    nodeIntegration: false,
    sandbox: true,
    webSecurity: true,
    partition: PARTITION,
  };
}

function openMainWindow(): void {
  if (mainWindow && !mainWindow.isDestroyed()) {
    focus(mainWindow);
    return;
  }
  mainWindow = new BrowserWindow({
    width: 1440,
    height: 900,
    minWidth: 1024,
    minHeight: 640,
    show: false,
    title: 'NodePilot',
    icon: windowIcon(),
    backgroundColor: '#0b1020',
    // NO preload and NO IPC for the production SPA window.
    webPreferences: baseWebPreferences(),
  });
  mainWindow.removeMenu();
  hardenWindow(mainWindow, config.origin);
  mainWindow.webContents.on('page-favicon-updated', (_event, favicons) => applySkinIcons(favicons));
  mainWindow.once('ready-to-show', () => mainWindow?.show());
  mainWindow.on('close', (event) => {
    if (!quitting) { event.preventDefault(); mainWindow?.hide(); }
  });
  mainWindow.on('closed', () => { mainWindow = null; });
  void mainWindow.loadURL(config.origin);
}

function openSetupWindow(): void {
  if (setupWindow && !setupWindow.isDestroyed()) { focus(setupWindow); return; }
  setupWindow = new BrowserWindow({
    width: 460,
    height: 620,
    resizable: false,
    title: 'NodePilot — First-time setup',
    icon: windowIcon(),
    backgroundColor: '#0b1020',
    webPreferences: {
      ...baseWebPreferences(),
      preload: join(__dirname, 'setup-preload.js'),
    },
  });
  setupWindow.removeMenu();
  hardenWindow(setupWindow, config.origin);
  setupWindow.on('closed', () => { setupWindow = null; });
  void setupWindow.loadFile(join(__dirname, 'setup.html'));
}

function showSplash(): void {
  splashWindow = new BrowserWindow({
    width: 420, height: 240, frame: false, resizable: false, show: true,
    icon: windowIcon(),
    backgroundColor: '#0b1020',
    webPreferences: { contextIsolation: true, sandbox: true },
  });
  const html =
    '<!doctype html><meta charset="utf-8"><body style="margin:0;height:100vh;display:flex;' +
    'flex-direction:column;align-items:center;justify-content:center;font-family:Segoe UI,sans-serif;' +
    'background:#0b1020;color:#e6e8ef"><div style="font-size:20px;font-weight:600">NodePilot</div>' +
    '<div style="margin-top:10px;opacity:.7;font-size:13px">Starting local services...</div></body>';
  void splashWindow.loadURL('data:text/html;charset=utf-8,' + encodeURIComponent(html));
}

function closeSplash(): void {
  splashWindow?.close();
  splashWindow = null;
}

function focus(win: BrowserWindow): void {
  if (win.isMinimized()) win.restore();
  win.show();
  win.focus();
}

// -------------------------------------------------------------------------------------------
// Tray
// -------------------------------------------------------------------------------------------
function createTray(): void {
  try {
    tray = new Tray(trayIcon());
    tray.setToolTip('NodePilot');
    tray.setContextMenu(Menu.buildFromTemplate([
      { label: 'Open NodePilot', click: () => openMainWindow() },
      { label: 'Restart backend', click: () => restartBackend() },
      { type: 'separator' },
      { label: 'Quit Electron', click: () => { quitting = true; app.quit(); } },
    ]));
    tray.on('click', () => openMainWindow());
  } catch {
    // A missing tray icon must not prevent the app from running.
    tray = null;
  }
}

/**
 * Restarts ONLY the API service, using the validated service name from desktop.json, via an
 * elevated (UAC) PowerShell. The background Postgres service is untouched.
 */
function restartBackend(): void {
  const service = config.serviceName; // validated to ^[A-Za-z0-9_.-]{1,64}$ in config.ts
  const inner = `Restart-Service -Name '${service}' -Force`;
  const outer = `Start-Process powershell -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile','-Command','${inner}')`;
  try {
    spawn('powershell.exe', ['-NoProfile', '-Command', outer], { detached: true, stdio: 'ignore' }).unref();
  } catch {
    dialog.showErrorBox('NodePilot', 'Could not launch the elevated restart. Restart the "NodePilot" service manually.');
  }
}
