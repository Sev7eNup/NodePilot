import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { desktopConfigPath, loadDesktopConfig, type DesktopConfig } from './config';

/**
 * `loadDesktopConfig` is the trust boundary between the installer and the shell: it decides which
 * origin gets loaded, which certificate is pinned, and which Windows service the elevated restart
 * may touch. Every rejection below is a security property, not a cosmetic validation.
 */
describe('loadDesktopConfig', () => {
  const FINGERPRINT = 'A'.repeat(64);
  let dir: string;

  const write = (contents: unknown): string => {
    const path = join(dir, 'desktop.json');
    writeFileSync(path, typeof contents === 'string' ? contents : JSON.stringify(contents), 'utf8');
    return path;
  };

  const valid = (overrides: Record<string, unknown> = {}) => ({
    schemaVersion: 1,
    origin: 'https://localhost:5001',
    certificateSha256: FINGERPRINT,
    serviceName: 'NodePilot.Api',
    ...overrides,
  });

  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), 'np-desktop-cfg-'));
  });

  afterEach(() => {
    rmSync(dir, { recursive: true, force: true });
  });

  it('accepts a well-formed config and returns the normalised origin', () => {
    const cfg: DesktopConfig = loadDesktopConfig(write(valid({ origin: 'https://localhost:5001/app/' })));

    expect(cfg.schemaVersion).toBe(1);
    // URL.origin drops path and trailing slash — the value is later compared against navigations.
    expect(cfg.origin).toBe('https://localhost:5001');
    expect(cfg.certificateSha256).toBe(FINGERPRINT);
    expect(cfg.serviceName).toBe('NodePilot.Api');
  });

  it('accepts 127.0.0.1 as loopback', () => {
    expect(loadDesktopConfig(write(valid({ origin: 'https://127.0.0.1:5001' }))).origin)
      .toBe('https://127.0.0.1:5001');
  });

  it('uppercases a lowercase fingerprint so the pin comparison is case-stable', () => {
    expect(loadDesktopConfig(write(valid({ certificateSha256: 'a'.repeat(64) }))).certificateSha256)
      .toBe(FINGERPRINT);
  });

  it('throws when the file does not exist', () => {
    expect(() => loadDesktopConfig(join(dir, 'missing.json')))
      .toThrow(/Configuration file not found/);
  });

  it('throws on malformed JSON', () => {
    expect(() => loadDesktopConfig(write('{ not json'))).toThrow(/not valid JSON/);
  });

  it.each([undefined, 0, 2, '1', null])('rejects schemaVersion %s', (schemaVersion) => {
    expect(() => loadDesktopConfig(write(valid({ schemaVersion }))))
      .toThrow(/schemaVersion/);
  });

  // ── origin: the shell must never be pointed at anything but the local backend ──

  it.each([
    ['http instead of https', 'http://localhost:5001'],
    ['a remote host', 'https://evil.example.com'],
    ['a host that merely contains "localhost"', 'https://localhost.evil.example.com'],
    ['an IPv6 loopback literal (not in the allow-list)', 'https://[::1]:5001'],
    ['a file URL', 'file:///C:/payload.html'],
    ['garbage', 'not-a-url'],
  ])('rejects origin — %s', (_label, origin) => {
    expect(() => loadDesktopConfig(write(valid({ origin })))).toThrow(/origin/);
  });

  it('rejects a missing origin', () => {
    const cfg = valid();
    delete (cfg as Record<string, unknown>).origin;
    expect(() => loadDesktopConfig(write(cfg))).toThrow(/origin/);
  });

  // ── certificate fingerprint: wrong shape means no meaningful pin ──

  it.each([
    ['too short', 'A'.repeat(63)],
    ['too long', 'A'.repeat(65)],
    ['non-hex characters', `${'A'.repeat(63)}Z`],
    ['colon-separated openssl form', 'A8:20:B1:8F'],
    ['empty', ''],
  ])('rejects certificateSha256 — %s', (_label, certificateSha256) => {
    expect(() => loadDesktopConfig(write(valid({ certificateSha256 }))))
      .toThrow(/certificateSha256/);
  });

  // ── serviceName: this string reaches an elevated PowerShell restart, so the charset
  //    restriction is the injection barrier ──

  it.each([
    ['a space', 'NodePilot Api'],
    ['a semicolon', 'NodePilot;calc'],
    ['a quote', "NodePilot'"],
    ['a backtick', 'NodePilot`whoami`'],
    ['an ampersand', 'NodePilot&calc'],
    ['a pipe', 'NodePilot|calc'],
    ['a dollar sign', 'NodePilot$(calc)'],
    ['a newline', 'NodePilot\nStop-Computer'],
    ['empty', ''],
    ['over 64 characters', 'a'.repeat(65)],
  ])('rejects serviceName — %s', (_label, serviceName) => {
    expect(() => loadDesktopConfig(write(valid({ serviceName }))))
      .toThrow(/serviceName/);
  });

  it.each(['NodePilot.Api', 'nodepilot-api', 'NodePilot_Api_2', 'a'.repeat(64)])(
    'accepts serviceName %s',
    (serviceName) => {
      expect(loadDesktopConfig(write(valid({ serviceName }))).serviceName).toBe(serviceName);
    },
  );
});

describe('desktopConfigPath', () => {
  const original = process.env.ProgramData;
  afterEach(() => {
    if (original === undefined) delete process.env.ProgramData;
    else process.env.ProgramData = original;
  });

  it('resolves under %ProgramData%', () => {
    process.env.ProgramData = 'D:\\PD';
    expect(desktopConfigPath()).toBe(join('D:\\PD', 'NodePilot', 'desktop.json'));
  });

  it('falls back to C:\\ProgramData when the variable is absent', () => {
    delete process.env.ProgramData;
    expect(desktopConfigPath()).toBe(join('C:\\ProgramData', 'NodePilot', 'desktop.json'));
  });
});
