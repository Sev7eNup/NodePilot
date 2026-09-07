/**
 * Mirror of `NodePilot.Core.Activities.KnownProgramLaunchers`.
 *
 * `startProgram` requires a fully qualified `filePath`: the engine launches through CreateProcess
 * and never searches the target's PATH, so a bare name would resolve differently per machine and
 * could not be checked against `FileSystemOperation:AllowedRoots`. These four launchers are
 * completed to their system locations instead of being rejected, so an author can still type
 * `cmd.exe` and the stored definition carries the absolute path.
 *
 * Pinned to the backend by `KnownProgramLaunchersFrontendSyncTests`.
 */
export const KNOWN_PROGRAM_LAUNCHERS: Readonly<Record<string, string>> = {
  cmd: 'C:\\Windows\\System32\\cmd.exe',
  powershell: 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe',
  cscript: 'C:\\Windows\\System32\\cscript.exe',
  wscript: 'C:\\Windows\\System32\\wscript.exe',
};

/**
 * Completes a bare launcher name to its absolute path. Returns null for anything carrying a
 * directory of its own, so a deliberate path to another copy stays untouched.
 */
export function resolveKnownLauncher(program: string): string | null {
  const value = program.trim();
  if (!value || value.includes('\\') || value.includes('/') || value.includes(':')) return null;
  const stem = value.replace(/\.[^.]*$/, '').toLowerCase();
  return KNOWN_PROGRAM_LAUNCHERS[stem] ?? null;
}

/** True for `C:\dir\file.exe` — the only shape the engine's path guard accepts. */
export function isFullyQualifiedLocalPath(value: string): boolean {
  return /^[A-Za-z]:[\\/]/.test(value.trim());
}

/** True for `\\server\share\...`, which the engine rejects outright (SMB coercion). */
export function isUncPath(value: string): boolean {
  const trimmed = value.trim();
  return trimmed.startsWith('\\\\') || trimmed.startsWith('//');
}
