import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * The non-secret handoff contract written by the installer/provisioner to
 * %ProgramData%\NodePilot\desktop.json. It tells the Electron shell which loopback origin to
 * load, which certificate fingerprint to pin, and which Windows service the "Restart backend"
 * action may control. It contains NO secrets.
 */
export interface DesktopConfig {
  schemaVersion: 1;
  origin: string;
  /** SHA-256 of the server certificate (DER), uppercase hex, no separators. */
  certificateSha256: string;
  /** Windows service name of the API host — validated to a safe charset. */
  serviceName: string;
}

const HEX_SHA256 = /^[0-9A-F]{64}$/;
const SERVICE_NAME = /^[A-Za-z0-9_.-]{1,64}$/;

export function desktopConfigPath(): string {
  const programData = process.env.ProgramData ?? 'C:\\ProgramData';
  return join(programData, 'NodePilot', 'desktop.json');
}

/**
 * Loads and strictly validates desktop.json. Every field is checked because these values drive
 * security-sensitive behaviour (which origin loads, which cert is trusted, which service the
 * elevated restart touches). Any deviation throws rather than falling back to a permissive default.
 */
export function loadDesktopConfig(path: string = desktopConfigPath()): DesktopConfig {
  if (!existsSync(path)) {
    throw new Error(`Configuration file not found: ${path}. Please reinstall NodePilot.`);
  }

  let raw: unknown;
  try {
    raw = JSON.parse(readFileSync(path, 'utf8'));
  } catch (e) {
    throw new Error(`Configuration file ${path} is not valid JSON: ${(e as Error).message}`);
  }

  const cfg = raw as Partial<DesktopConfig>;

  if (cfg.schemaVersion !== 1) {
    throw new Error(`Unsupported desktop.json schemaVersion: ${String(cfg.schemaVersion)} (expected 1).`);
  }

  const originRaw = String(cfg.origin ?? '');
  let parsed: URL;
  try {
    parsed = new URL(originRaw);
  } catch {
    throw new Error(`desktop.json origin is not a valid URL: ${originRaw}`);
  }
  if (parsed.protocol !== 'https:' || (parsed.hostname !== 'localhost' && parsed.hostname !== '127.0.0.1')) {
    throw new Error(`desktop.json origin must be an https loopback URL, got: ${originRaw}`);
  }

  const fingerprint = String(cfg.certificateSha256 ?? '').toUpperCase();
  if (!HEX_SHA256.test(fingerprint)) {
    throw new Error('desktop.json certificateSha256 must be 64 hexadecimal characters (a SHA-256 hash).');
  }

  const serviceName = String(cfg.serviceName ?? '');
  if (!SERVICE_NAME.test(serviceName)) {
    throw new Error('desktop.json serviceName is missing or contains unsupported characters.');
  }

  return {
    schemaVersion: 1,
    origin: parsed.origin,
    certificateSha256: fingerprint,
    serviceName,
  };
}
