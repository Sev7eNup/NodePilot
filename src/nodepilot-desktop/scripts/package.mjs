import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { packager } from '@electron/packager';
import { assertPackageBoundary } from './assert-package-boundary.mjs';
import {
  assertElectronRuntimeVersion,
  readPinnedElectronVersion,
} from './assert-electron-runtime-version.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const output = join(root, 'out');
const manifestPath = join(root, 'package.json');
const pinnedElectronVersion = await readPinnedElectronVersion(manifestPath);

const packagePaths = await packager({
  dir: root,
  out: output,
  name: 'NodePilot',
  executableName: 'NodePilot',
  appCopyright: 'NodePilot',
  platform: 'win32',
  arch: 'x64',
  electronVersion: pinnedElectronVersion,
  icon: join(root, 'assets', 'icon.ico'),
  asar: true,
  overwrite: true,
  prune: true,
  ignore: [
    /^\/out\//,
    /^\/src\//,
    /^\/scripts\//,
    /^\/tsconfig\.json$/,
    /^\/vitest\.config\.ts$/,
    /^\/\.gitignore$/,
  ],
});

if (packagePaths.length !== 1) {
  throw new Error(`Desktop packaging produced ${packagePaths.length} outputs; expected exactly one.`);
}
await assertPackageBoundary(packagePaths[0]);
await assertElectronRuntimeVersion(packagePaths[0], manifestPath);
console.log(`desktop-package: verified ${packagePaths[0]}`);
