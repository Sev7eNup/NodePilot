// Copies non-TS runtime assets (the local setup page) into dist/ so they sit next to the
// compiled main process and get packed into the asar by Electron Packager.
import { copyFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');
mkdirSync(join(root, 'dist'), { recursive: true });
copyFileSync(join(root, 'src', 'setup.html'), join(root, 'dist', 'setup.html'));
console.log('copy-static: setup.html -> dist/');
