import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import extract from '@electron-internal/extract-zip';
import { assertPackageBoundary } from '../scripts/assert-package-boundary.mjs';

function crc32(data: Buffer): number {
  let crc = 0xffffffff;
  for (const byte of data) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit += 1) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function singleEntryZip(name: string, data: string, unixMode: number): Buffer {
  const fileName = Buffer.from(name);
  const contents = Buffer.from(data);
  const checksum = crc32(contents);

  const local = Buffer.alloc(30);
  local.writeUInt32LE(0x04034b50, 0);
  local.writeUInt16LE(20, 4);
  local.writeUInt32LE(checksum, 14);
  local.writeUInt32LE(contents.length, 18);
  local.writeUInt32LE(contents.length, 22);
  local.writeUInt16LE(fileName.length, 26);

  const central = Buffer.alloc(46);
  central.writeUInt32LE(0x02014b50, 0);
  central.writeUInt16LE(0x031e, 4); // ZIP 3.0, created on Unix so the mode is authoritative.
  central.writeUInt16LE(20, 6);
  central.writeUInt32LE(checksum, 16);
  central.writeUInt32LE(contents.length, 20);
  central.writeUInt32LE(contents.length, 24);
  central.writeUInt16LE(fileName.length, 28);
  central.writeUInt32LE((unixMode << 16) >>> 0, 38);

  const localRecord = Buffer.concat([local, fileName, contents]);
  const centralRecord = Buffer.concat([central, fileName]);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(1, 8);
  end.writeUInt16LE(1, 10);
  end.writeUInt32LE(centralRecord.length, 12);
  end.writeUInt32LE(localRecord.length, 16);
  return Buffer.concat([localRecord, centralRecord, end]);
}

describe('desktop package filesystem boundary', () => {
  const roots: string[] = [];

  afterEach(() => {
    for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
  });

  it('rejects a symlink or Windows junction that escapes the package root', async () => {
    const root = mkdtempSync(join(tmpdir(), 'nodepilot-package-boundary-'));
    roots.push(root);
    const packageRoot = join(root, 'package');
    const outside = join(root, 'outside');
    mkdirSync(packageRoot);
    mkdirSync(outside);
    symlinkSync(outside, join(packageRoot, 'escape'), process.platform === 'win32' ? 'junction' : 'dir');

    await expect(assertPackageBoundary(packageRoot)).rejects.toThrow(/symbolic link|reparse point/i);
  });

  it('vendor extractor rejects an archive symlink that escapes the extraction root', async () => {
    const root = mkdtempSync(join(tmpdir(), 'nodepilot-archive-boundary-'));
    roots.push(root);
    const archive = join(root, 'malicious.zip');
    const output = join(root, 'output');
    mkdirSync(output);
    writeFileSync(archive, singleEntryZip('nested/escape', '../../outside.txt', 0o120777));

    await expect(extract(archive, { dir: output })).rejects.toThrow(/symlink|escape|outside/i);
  });
});
