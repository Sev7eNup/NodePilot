import { readFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const EXACT_ELECTRON_VERSION = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/;

export async function readPinnedElectronVersion(manifestPath) {
  const manifest = JSON.parse(await readFile(resolve(manifestPath), 'utf8'));
  const version = manifest.devDependencies?.electron;
  if (typeof version !== 'string' || !EXACT_ELECTRON_VERSION.test(version)) {
    throw new Error('package.json must pin devDependencies.electron to one exact version.');
  }
  return version;
}

/**
 * Verifies the Electron runtime version embedded at the root of a packaged application.
 */
export async function assertElectronRuntimeVersion(packageRoot, manifestPath) {
  const expectedVersion = await readPinnedElectronVersion(manifestPath);
  const versionPath = join(resolve(packageRoot), 'version');
  let actualVersion;
  try {
    actualVersion = (await readFile(versionPath, 'utf8')).trim();
  } catch (error) {
    if (error?.code === 'ENOENT') {
      throw new Error(`Electron version file is missing: ${versionPath}`, { cause: error });
    }
    throw error;
  }

  if (actualVersion !== expectedVersion) {
    throw new Error(
      `Packaged Electron version ${actualVersion} does not match pinned version ${expectedVersion}.`,
    );
  }

  return actualVersion;
}

const invokedPath = process.argv[1] ? pathToFileURL(resolve(process.argv[1])).href : undefined;
if (invokedPath === import.meta.url) {
  const packageRoot = process.argv[2];
  const manifestPath = process.argv[3];
  if (!packageRoot || !manifestPath) {
    throw new Error(
      'Usage: node scripts/assert-electron-runtime-version.mjs <package-root> <package.json>',
    );
  }
  const version = await assertElectronRuntimeVersion(packageRoot, manifestPath);
  console.log(`electron-runtime-version: verified ${version}`);
}
