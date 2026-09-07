import { describe, it, expect } from 'vitest';
import {
  KNOWN_PROGRAM_LAUNCHERS,
  isFullyQualifiedLocalPath,
  isUncPath,
  resolveKnownLauncher,
} from '../../lib/knownProgramLaunchers';

/**
 * Mirror of NodePilot.Core.Activities.KnownProgramLaunchers — the map itself is pinned to the
 * backend by KnownProgramLaunchersFrontendSyncTests; these tests cover the resolution rules.
 */
describe('resolveKnownLauncher', () => {
  it('bareName_withAndWithoutExtension_resolves', () => {
    expect(resolveKnownLauncher('cmd.exe')).toBe(KNOWN_PROGRAM_LAUNCHERS.cmd);
    expect(resolveKnownLauncher('cmd')).toBe(KNOWN_PROGRAM_LAUNCHERS.cmd);
  });

  it('caseInsensitive', () => {
    expect(resolveKnownLauncher('POWERSHELL.EXE')).toBe(KNOWN_PROGRAM_LAUNCHERS.powershell);
  });

  it('surroundingWhitespace_ignored', () => {
    expect(resolveKnownLauncher('  wscript.exe  ')).toBe(KNOWN_PROGRAM_LAUNCHERS.wscript);
  });

  it('ownDirectory_leftAlone', () => {
    // A deliberate path to another copy must survive untouched.
    expect(resolveKnownLauncher('D:\\Tools\\cmd.exe')).toBeNull();
    expect(resolveKnownLauncher('.\\cmd.exe')).toBeNull();
  });

  it('unknownProgram_leftAlone', () => {
    expect(resolveKnownLauncher('7z.exe')).toBeNull();
    expect(resolveKnownLauncher('')).toBeNull();
  });
});

describe('path shape helpers', () => {
  it('driveLetterPaths_areFullyQualified', () => {
    expect(isFullyQualifiedLocalPath('C:\\Windows\\notepad.exe')).toBe(true);
    expect(isFullyQualifiedLocalPath('c:/windows/notepad.exe')).toBe(true);
  });

  it('bareAndRelativePaths_areNot', () => {
    expect(isFullyQualifiedLocalPath('cmd.exe')).toBe(false);
    expect(isFullyQualifiedLocalPath('.\\cmd.exe')).toBe(false);
    expect(isFullyQualifiedLocalPath('\\\\srv\\share\\x.exe')).toBe(false);
  });

  it('uncIsDetectedInBothSlashStyles', () => {
    expect(isUncPath('\\\\srv\\share')).toBe(true);
    expect(isUncPath('//srv/share')).toBe(true);
    expect(isUncPath('C:\\srv')).toBe(false);
  });
});
