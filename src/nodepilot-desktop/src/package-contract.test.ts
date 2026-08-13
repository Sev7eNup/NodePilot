import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it } from 'vitest';
import { assertElectronRuntimeVersion } from '../scripts/assert-electron-runtime-version.mjs';

interface PackageManifest {
  devDependencies: Record<string, string>;
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
  it('pins Electron 43.4.0 consistently in the manifest and lockfile', () => {
    const manifest = readJson<PackageManifest>('../package.json');
    const lock = readJson<PackageLock>('../package-lock.json');

    expect(manifest.devDependencies.electron).toBe('43.4.0');
    expect(lock.packages[''].devDependencies?.electron).toBe('43.4.0');
    expect(lock.packages['node_modules/electron'].version).toBe('43.4.0');
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
