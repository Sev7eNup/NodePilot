import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it } from 'vitest';
import { assertElectronRuntimeVersion } from '../scripts/assert-electron-runtime-version.mjs';
import { hardenElectronFuses } from '../scripts/harden-electron-fuses.mjs';

interface PackageManifest {
  devDependencies: Record<string, string>;
  '//types-node': string[];
}

interface PackageLock {
  packages: Record<string, {
    version?: string;
    devDependencies?: Record<string, string>;
  }>;
}

function readJson<T>(relativePath: string): T {
  const path = fileURLToPath(new URL(relativePath, import.meta.url));
  return JSON.parse(readFileSync(path, 'utf8')) as T;
}

describe('desktop runtime dependency contract', () => {
  it('pins Electron 44.9.0 consistently in the manifest and lockfile', () => {
    const manifest = readJson<PackageManifest>('../package.json');
    const lock = readJson<PackageLock>('../package-lock.json');

    expect(manifest.devDependencies.electron).toBe('44.9.0');
    expect(lock.packages[''].devDependencies?.electron).toBe('44.9.0');
    expect(lock.packages['node_modules/electron'].version).toBe('44.9.0');
  });

  // The embedded Node major changes with Electron majors. Naming the pinned version in the note
  // makes a bump fail here until someone has checked the Node release behind the new one.
  it('names the pinned Electron in the embedded-Node note', () => {
    const manifest = readJson<PackageManifest>('../package.json');

    expect(manifest['//types-node'].join(' ')).toContain(`Electron ${manifest.devDependencies.electron} bundles Node`);
  });

  it('uses Electron vendor extraction without the vulnerable extract-zip package', () => {
    const manifest = readJson<PackageManifest>('../package.json');
    const lock = readJson<PackageLock>('../package-lock.json');

    expect(manifest.devDependencies['@electron/packager']).toBe('20.3.0');
    expect(manifest.devDependencies['@electron-internal/extract-zip']).toBe('1.0.5');
    expect(manifest.devDependencies['@electron-forge/cli']).toBeUndefined();
    expect(manifest.devDependencies['@electron-forge/maker-zip']).toBeUndefined();
    expect(lock.packages['node_modules/@electron/packager'].version).toBe('20.3.0');
    expect(lock.packages['node_modules/@electron-internal/extract-zip'].version).toBe('1.0.5');
    expect(lock.packages['node_modules/@electron-forge/cli']).toBeUndefined();
    expect(lock.packages['node_modules/extract-zip']).toBeUndefined();
  });

  it('validates the packaged runtime version after packager returns', () => {
    const packagingScript = readFileSync(
      fileURLToPath(new URL('../scripts/package.mjs', import.meta.url)),
      'utf8',
    );
    const packagerCall = packagingScript.indexOf('await packager(');
    const runtimeVersionGate = packagingScript.indexOf(
      'await assertElectronRuntimeVersion(packagePaths[0], manifestPath)',
    );

    expect(packagerCall).toBeGreaterThan(-1);
    expect(runtimeVersionGate).toBeGreaterThan(packagerCall);
  });

  it('hardens the Electron fuses of the packaged executable after packager returns', () => {
    const manifest = readJson<PackageManifest>('../package.json');
    const lock = readJson<PackageLock>('../package-lock.json');
    const packagingScript = readFileSync(
      fileURLToPath(new URL('../scripts/package.mjs', import.meta.url)),
      'utf8',
    );
    const packagerCall = packagingScript.indexOf('await packager(');
    const fuseHardening = packagingScript.indexOf(
      "await hardenElectronFuses(join(packagePaths[0], 'NodePilot.exe'))",
    );

    expect(manifest.devDependencies['@electron/fuses']).toBe('2.1.3');
    expect(lock.packages['node_modules/@electron/fuses'].version).toBe('2.1.3');
    expect(fuseHardening).toBeGreaterThan(packagerCall);
    expect(packagerCall).toBeGreaterThan(-1);
  });

  it('revalidates the packaged runtime before the installer stages or compiles it', () => {
    const installerScript = readFileSync(
      fileURLToPath(new URL('../../../deploy/desktop/Build-DesktopInstaller.ps1', import.meta.url)),
      'utf8',
    );
    const packageCall = installerScript.indexOf('& npm.cmd run package');
    const runtimeVersionGate = installerScript.indexOf(
      '& node $electronVersionGate $desktopPackageOut $electronManifest',
    );
    const stagingCopy = installerScript.indexOf('Copy-Item -Path $desktopPackageOut');
    const installerCompile = installerScript.indexOf("Write-Step 'Compiling installer (Inno Setup)'");

    expect(packageCall).toBeGreaterThan(-1);
    expect(runtimeVersionGate).toBeGreaterThan(packageCall);
    expect(stagingCopy).toBeGreaterThan(runtimeVersionGate);
    expect(installerCompile).toBeGreaterThan(runtimeVersionGate);
  });
});

describe('packaged Electron runtime version contract', () => {
  const roots: string[] = [];

  afterEach(() => {
    for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
  });

  it('accepts a package whose version file exactly matches the pinned Electron version', async () => {
    const root = mkdtempSync(join(tmpdir(), 'nodepilot-electron-version-'));
    roots.push(root);
    const packageRoot = join(root, 'package');
    const manifestPath = join(root, 'package.json');
    mkdirSync(packageRoot);
    writeFileSync(manifestPath, JSON.stringify({ devDependencies: { electron: '43.4.0' } }));
    writeFileSync(join(packageRoot, 'version'), '43.4.0\n');

    await expect(assertElectronRuntimeVersion(packageRoot, manifestPath)).resolves.toBe('43.4.0');
  });

  it('rejects a package whose version file differs from the pinned Electron version', async () => {
    const root = mkdtempSync(join(tmpdir(), 'nodepilot-electron-version-'));
    roots.push(root);
    const packageRoot = join(root, 'package');
    const manifestPath = join(root, 'package.json');
    mkdirSync(packageRoot);
    writeFileSync(manifestPath, JSON.stringify({ devDependencies: { electron: '43.4.0' } }));
    writeFileSync(join(packageRoot, 'version'), '43.3.0\n');

    await expect(assertElectronRuntimeVersion(packageRoot, manifestPath)).rejects.toThrow(
      /43\.3\.0.*43\.4\.0|43\.4\.0.*43\.3\.0/,
    );
  });

  it('rejects a package whose Electron version file is missing', async () => {
    const root = mkdtempSync(join(tmpdir(), 'nodepilot-electron-version-'));
    roots.push(root);
    const packageRoot = join(root, 'package');
    const manifestPath = join(root, 'package.json');
    mkdirSync(packageRoot);
    writeFileSync(manifestPath, JSON.stringify({ devDependencies: { electron: '43.4.0' } }));

    await expect(assertElectronRuntimeVersion(packageRoot, manifestPath)).rejects.toThrow(
      /Electron version file is missing/,
    );
  });
});

describe('packaged Electron fuse hardening', () => {
  // Fuse wire layout in an Electron binary: sentinel, version byte, length byte, one '0'/'1' per fuse.
  const SENTINEL = 'dL7pKGdnNz796PbbjQWNKmHXBZaB9tsX';
  // Electron's own defaults for the nine fuses that Electron 44 ships.
  const ELECTRON_DEFAULT_WIRE = '101100011';
  const roots: string[] = [];

  afterEach(() => {
    for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
  });

  function writeFakeBinary(wire: string): string {
    const root = mkdtempSync(join(tmpdir(), 'nodepilot-electron-fuses-'));
    roots.push(root);
    const executable = join(root, 'NodePilot.exe');
    writeFileSync(executable, Buffer.concat([
      Buffer.from('MZ-leading-bytes'),
      Buffer.from(SENTINEL),
      Buffer.from([1, wire.length]),
      Buffer.from(wire),
      Buffer.from('trailing-bytes'),
    ]));
    return executable;
  }

  function readWire(executable: string, length: number): string {
    const bytes = readFileSync(executable);
    const start = bytes.indexOf(SENTINEL) + SENTINEL.length + 2;
    return bytes.subarray(start, start + length).toString();
  }

  it('turns off Node mode, NODE_OPTIONS, inspector flags and file:// privileges and enforces app.asar', async () => {
    const executable = writeFakeBinary(ELECTRON_DEFAULT_WIRE);

    await hardenElectronFuses(executable);

    // RunAsNode, CookieEncryption, NodeOptions, CliInspect, AsarIntegrity, OnlyLoadAppFromAsar,
    // BrowserV8Snapshot, FileProtocolExtraPrivileges, WasmTrapHandlers
    expect(readWire(executable, ELECTRON_DEFAULT_WIRE.length)).toBe('010011001');
  });

  it('fails when the Electron binary has a fuse without a configured value', async () => {
    const executable = writeFakeBinary(`${ELECTRON_DEFAULT_WIRE}1`);

    await expect(hardenElectronFuses(executable)).rejects.toThrow(/strictlyRequireAllFuses/);
  });
});
