/**
 * Resolves a CSS color to `#rrggbb` for consumers that cannot take arbitrary CSS notation.
 *
 * The production build minifies CSS with Lightning CSS, which shortens colors *inside* custom
 * properties (`#ffffff` becomes `#fff`, `#ff0000` becomes `red`); the dev server does not. A
 * design token read back with getComputedStyle is therefore not reliably the notation the
 * stylesheet was written in.
 *
 * Handled: hex (3/4/6/8 digits), `rgb()`/`rgba()` in both syntaxes, and whatever the browser
 * normalizes to one of those — in practice named colors and `hsl()`. NOT handled: `oklch()`,
 * `color-mix()`, `lab()` and friends, because browsers serialize those back in their own color
 * space rather than as sRGB; they return null so the caller uses its fallback. The minifier
 * cannot produce those from a hex token, so this covers everything the build can emit.
 */

interface Rgb { r: number; g: number; b: number }

const HEX_RE = /^#([0-9a-f]{3,8})$/i;
// `rgb(1, 2, 3)`, `rgb(1 2 3)`, `rgb(1 2 3 / 40%)`, `rgba(1, 2, 3, .4)` — the forms browsers
// both accept and serialize to.
const RGB_RE = /^rgba?\(\s*([^\s,/)]+)[\s,]+([^\s,/)]+)[\s,]+([^\s,/)]+)(?:\s*[,/]\s*[^\s,/)]+)?\s*\)$/i;

const clampByte = (n: number) => Math.max(0, Math.min(255, Math.round(n)));

/** One `rgb()` channel: a number, or a percentage of 255. */
function channel(raw: string): number | null {
  const isPercent = raw.endsWith('%');
  const n = Number.parseFloat(isPercent ? raw.slice(0, -1) : raw);
  if (!Number.isFinite(n)) return null;
  return clampByte(isPercent ? (n / 100) * 255 : n);
}

function parseHex(raw: string): Rgb | null {
  const match = HEX_RE.exec(raw);
  if (!match) return null;
  const digits = match[1];
  // 3- and 4-digit shorthand doubles each nibble; 5 and 7 digits are not valid CSS.
  const full = digits.length === 3 || digits.length === 4
    ? [...digits].map((c) => c + c).join('')
    : digits;
  if (full.length !== 6 && full.length !== 8) return null;
  return {
    r: parseInt(full.slice(0, 2), 16),
    g: parseInt(full.slice(2, 4), 16),
    b: parseInt(full.slice(4, 6), 16),
  };
}

function parseRgbFunction(raw: string): Rgb | null {
  const match = RGB_RE.exec(raw);
  if (!match) return null;
  const r = channel(match[1]);
  const g = channel(match[2]);
  const b = channel(match[3]);
  return r === null || g === null || b === null ? null : { r, g, b };
}

// Only the element is cached. getContext() is called per read so a test can swap the
// implementation without a module reset.
let probeCanvas: HTMLCanvasElement | null = null;

const SEED_A = '#ff0000';
const SEED_B = '#0000ff';

/**
 * Hands the value to the browser's own color parser. An invalid value leaves `fillStyle`
 * untouched, so it is written against two different seeds: equal results mean the value was
 * parsed, differing results mean both seeds survived. Each seed is read back first, because a
 * context that cannot even store a plain hex would otherwise make two rejections look like
 * agreement. The result is re-parsed rather than trusted, which is what makes a stubbed context
 * (jsdom hands back whatever was assigned) fall through to null instead of yielding garbage.
 */
function parseViaBrowser(raw: string): Rgb | null {
  if (typeof document === 'undefined') return null;
  try {
    probeCanvas ??= document.createElement('canvas');
    const ctx = probeCanvas.getContext('2d');
    if (!ctx) return null;
    ctx.fillStyle = SEED_A;
    if (ctx.fillStyle !== SEED_A) return null;
    ctx.fillStyle = raw;
    const first = ctx.fillStyle;
    ctx.fillStyle = SEED_B;
    if (ctx.fillStyle !== SEED_B) return null;
    ctx.fillStyle = raw;
    if (typeof first !== 'string' || ctx.fillStyle !== first) return null;
    return parseHex(first) ?? parseRgbFunction(first);
  } catch {
    return null;
  }
}

const hex2 = (n: number) => n.toString(16).padStart(2, '0');

/** Returns lowercase `#rrggbb` (alpha dropped), or null when the value cannot be resolved. */
export function cssColorToHex(value: string | null | undefined): string | null {
  const raw = value?.trim();
  if (!raw) return null;
  const rgb = parseHex(raw) ?? parseRgbFunction(raw) ?? parseViaBrowser(raw);
  return rgb ? `#${hex2(rgb.r)}${hex2(rgb.g)}${hex2(rgb.b)}` : null;
}
