// Dependency-vulnerability gate for the Electron shell.
//
// The other three Node jobs in CI get away with a plain `npm audit --audit-level=moderate`.
// This package cannot, because it has an advisory with no fixed release anywhere upstream,
// and the two blunt escapes both destroy the check:
//
//   * `--omit=dev` audits nothing at all here. `dependencies` is empty and electron itself
//     is a devDependency (Electron Forge requires that), so omitting dev dependencies would
//     silently stop reporting Electron CVEs — the one package in this tree that reaches users.
//   * `--audit-level=critical` would let every future high-severity advisory through.
//
// So the audit stays full and single advisories are excused by id, each with a reason and a
// condition for dropping it again. Anything not on the list still fails the build.
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

/**
 * Reviewed advisories that cannot be resolved from this repository. Keep this list at zero
 * entries whenever upstream allows it: an entry here is a vulnerability we are shipping past,
 * not one we have fixed. `stale` output below tells you when an entry can go.
 */
export const ALLOWLIST = [
  {
    id: 'GHSA-jmr9-qjv8-65gv',
    package: 'extract-zip',
    reason:
      'Unvalidated symlink path traversal, high. Reaches us only through the Electron Forge ' +
      'dev tree (@electron/packager -> extract-zip) and has no patched release at all — ' +
      'first_patched_version is null, so neither an override nor `npm audit fix` can clear ' +
      'it. Forge is build-time only; nothing from this path is packaged into the installer. ' +
      'Drop this entry once Forge stops depending on extract-zip or a fixed version ships.',
  },
];

const SEVERITY_ORDER = ['info', 'low', 'moderate', 'high', 'critical'];

function advisoryIdFrom(url) {
  const match = /\/advisories\/(GHSA-[a-z0-9-]+)/i.exec(url ?? '');
  return match ? match[1] : null;
}

/**
 * Folds an `npm audit --json` report into one entry per advisory. npm reports the same
 * advisory once per affected package — the extract-zip finding alone fans out across 15
 * entries of the Forge chain — and expresses transitive hits as plain strings in `via`,
 * which carry no advisory of their own.
 */
export function collectAdvisories(report) {
  const byId = new Map();
  for (const entry of Object.values(report?.vulnerabilities ?? {})) {
    for (const via of entry?.via ?? []) {
      if (typeof via !== 'object' || via === null) continue;
      const id = advisoryIdFrom(via.url);
      if (!id) continue;
      if (!byId.has(id)) {
        byId.set(id, {
          id,
          url: via.url,
          title: via.title ?? '(no title)',
          severity: via.severity ?? 'info',
          packages: new Set(),
        });
      }
      if (via.name) byId.get(id).packages.add(via.name);
    }
  }
  return [...byId.values()]
    .map((a) => ({ ...a, packages: [...a.packages].sort() }))
    .sort((a, b) => a.id.localeCompare(b.id));
}

/**
 * Splits the report into what must fail the build, what is excused, and which allowlist
 * entries no longer match anything and should be deleted.
 */
export function evaluate(report, { allowlist = ALLOWLIST, minSeverity = 'moderate' } = {}) {
  const floor = SEVERITY_ORDER.indexOf(minSeverity);
  if (floor < 0) throw new Error(`unknown severity: ${minSeverity}`);
  const advisories = collectAdvisories(report);
  const excusedIds = new Set(allowlist.map((e) => e.id));
  const atOrAboveFloor = advisories.filter((a) => SEVERITY_ORDER.indexOf(a.severity) >= floor);
  const present = new Set(advisories.map((a) => a.id));
  return {
    blocking: atOrAboveFloor.filter((a) => !excusedIds.has(a.id)),
    excused: atOrAboveFloor.filter((a) => excusedIds.has(a.id)),
    stale: allowlist.filter((e) => !present.has(e.id)),
  };
}

function runNpmAudit() {
  const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm';
  try {
    // shell: true on Windows because npm is a .cmd shim, which execFileSync has refused to
    // spawn directly since Node 20 (CVE-2024-27980). The argument vector is a literal.
    return execFileSync(npm, ['audit', '--json'], {
      encoding: 'utf8',
      maxBuffer: 64 * 1024 * 1024,
      shell: process.platform === 'win32',
    });
  } catch (error) {
    // npm audit exits non-zero the moment it finds anything; the report still lands on stdout.
    if (error?.stdout) return error.stdout;
    throw error;
  }
}

function main() {
  const { blocking, excused, stale } = evaluate(JSON.parse(runNpmAudit()));

  for (const a of excused) {
    const entry = ALLOWLIST.find((e) => e.id === a.id);
    console.log(`excused  [${a.severity}] ${a.id} ${a.title}`);
    console.log(`         packages: ${a.packages.join(', ')}`);
    console.log(`         reason: ${entry.reason}`);
  }
  for (const e of stale) {
    console.log(`stale    ${e.id} (${e.package}) no longer reported — remove it from ALLOWLIST`);
  }
  for (const a of blocking) {
    console.error(`BLOCKING [${a.severity}] ${a.id} ${a.title}`);
    console.error(`         packages: ${a.packages.join(', ')}`);
    console.error(`         ${a.url}`);
  }

  if (blocking.length > 0) {
    console.error(
      `\nnpm audit found ${blocking.length} advisory/advisories at moderate or above that are ` +
        'not on the reviewed allowlist in scripts/audit-gate.mjs.',
    );
    process.exit(1);
  }
  console.log(`audit-gate: clean (${excused.length} excused, ${stale.length} stale).`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main();
}
