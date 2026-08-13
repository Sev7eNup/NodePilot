import { lstat, readdir, realpath } from 'node:fs/promises';
import { isAbsolute, relative, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';

function isOutside(root, candidate) {
  const child = relative(root, candidate);
  return child === '..' || child.startsWith(`..${sep}`) || isAbsolute(child);
}

/**
 * Reject links and reparse-backed paths in the packaged desktop tree. Electron's vendor
 * extractor already validates archive entries before writing; this second boundary ensures the
 * exact directory handed to Inno Setup cannot redirect a later recursive copy outside itself.
 */
export async function assertPackageBoundary(packageRoot) {
  const absoluteRoot = resolve(packageRoot);
  const rootMetadata = await lstat(absoluteRoot);
  if (rootMetadata.isSymbolicLink()) {
    throw new Error(`Desktop package root is a symbolic link or reparse point: ${absoluteRoot}`);
  }

  const canonicalRoot = await realpath(absoluteRoot);

  async function inspect(directory) {
    const entries = await readdir(directory, { withFileTypes: true });
    for (const entry of entries) {
      const candidate = resolve(directory, entry.name);
      const metadata = await lstat(candidate);
      if (metadata.isSymbolicLink()) {
        throw new Error(`Desktop package contains a symbolic link or reparse point: ${candidate}`);
      }

      const canonicalCandidate = await realpath(candidate);
      if (isOutside(canonicalRoot, canonicalCandidate)) {
        throw new Error(`Desktop package path escapes its root: ${candidate}`);
      }
      if (metadata.isDirectory()) await inspect(candidate);
    }
  }

  await inspect(absoluteRoot);
}

const invokedPath = process.argv[1] ? pathToFileURL(resolve(process.argv[1])).href : undefined;
if (invokedPath === import.meta.url) {
  const packageRoot = process.argv[2];
  if (!packageRoot) throw new Error('Usage: node scripts/assert-package-boundary.mjs <package-root>');
  await assertPackageBoundary(packageRoot);
  console.log(`package-boundary: verified ${resolve(packageRoot)}`);
}
