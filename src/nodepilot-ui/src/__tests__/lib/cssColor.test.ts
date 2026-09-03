import { describe, it, expect, afterEach, vi } from 'vitest';
import { cssColorToHex } from '../../lib/cssColor';

/**
 * Stands in for a 2D context. `normalize` decides what the browser makes of an assigned value;
 * returning null models an invalid color, which leaves fillStyle untouched.
 */
function installContext(normalize: (value: string) => string | null) {
  // Plain hex always parses in a real browser, including the seeds the probe writes.
  const parse = (value: string) => (/^#[0-9a-f]{6}$/i.test(value) ? value.toLowerCase() : normalize(value));
  const ctx = {
    _fill: '#000000',
    get fillStyle() { return this._fill; },
    set fillStyle(value: string) {
      const normalized = parse(value);
      if (normalized) this._fill = normalized;
    },
    measureText: (text: string) => ({ width: text.length * 8 }),
  };
  return vi.spyOn(HTMLCanvasElement.prototype, 'getContext')
    .mockReturnValue(ctx as unknown as CanvasRenderingContext2D);
}

afterEach(() => vi.restoreAllMocks());

describe('cssColorToHex', () => {
  // The production CSS minifier shortens colors inside custom properties, so these are the
  // shapes a design token actually arrives in.
  it('expands three- and four-digit shorthand', () => {
    expect(cssColorToHex('#fff')).toBe('#ffffff');
    expect(cssColorToHex('#abc')).toBe('#aabbcc');
    expect(cssColorToHex('#e00')).toBe('#ee0000');
    expect(cssColorToHex('#222')).toBe('#222222');
    expect(cssColorToHex('#fff8')).toBe('#ffffff');
  });

  it('keeps six-digit hex, drops an eight-digit alpha and trims', () => {
    expect(cssColorToHex('#004ac6')).toBe('#004ac6');
    expect(cssColorToHex('  #004AC6  ')).toBe('#004ac6');
    expect(cssColorToHex('#004ac680')).toBe('#004ac6');
  });

  it('parses rgb()/rgba() in both syntaxes', () => {
    expect(cssColorToHex('rgb(255, 0, 0)')).toBe('#ff0000');
    expect(cssColorToHex('rgb(255 0 0)')).toBe('#ff0000');
    expect(cssColorToHex('rgb(255 0 0 / 40%)')).toBe('#ff0000');
    expect(cssColorToHex('rgba(148,163,184,.16)')).toBe('#94a3b8');
    expect(cssColorToHex('rgb(100% 0% 0%)')).toBe('#ff0000');
    expect(cssColorToHex('rgb(300 -5 0)')).toBe('#ff0000');
  });

  it('returns null for values that are not a color', () => {
    expect(cssColorToHex('')).toBeNull();
    expect(cssColorToHex('   ')).toBeNull();
    expect(cssColorToHex('#ff')).toBeNull();
    expect(cssColorToHex('#fffff')).toBeNull();
    expect(cssColorToHex('not-a-color')).toBeNull();
    expect(cssColorToHex('var(--x)')).toBeNull();
    expect(cssColorToHex(null)).toBeNull();
    expect(cssColorToHex(undefined)).toBeNull();
  });

  // The suite-wide canvas stub exposes only measureText, so the browser path is unavailable and
  // everything outside hex/rgb() degrades to the caller's fallback.
  it('returns null for named and modern notations without a usable context', () => {
    expect(cssColorToHex('red')).toBeNull();
    expect(cssColorToHex('azure')).toBeNull();
    expect(cssColorToHex('oklch(0.98 0 0)')).toBeNull();
    expect(cssColorToHex('color-mix(in srgb, #fff 14%, transparent)')).toBeNull();
  });
});

describe('cssColorToHex with a working 2D context', () => {
  it('resolves named colors', () => {
    installContext((v) => ({ red: '#ff0000', azure: '#f0ffff' } as Record<string, string>)[v] ?? null);
    expect(cssColorToHex('red')).toBe('#ff0000');
    expect(cssColorToHex('azure')).toBe('#f0ffff');
  });

  it('resolves hsl() and drops alpha from an rgba() round-trip', () => {
    installContext((v) => (v === 'hsl(0 100% 50%)' ? '#ff0000' : v === 'rgba(255,0,0,.5)' ? 'rgba(255, 0, 0, 0.5)' : null));
    expect(cssColorToHex('hsl(0 100% 50%)')).toBe('#ff0000');
    expect(cssColorToHex('rgba(255,0,0,.5)')).toBe('#ff0000');
  });

  it('returns null when the parser rejects the value', () => {
    installContext(() => null);
    expect(cssColorToHex('not-a-color')).toBeNull();
  });

  // A context that ignores every write would leave both probe runs on the same value and make
  // two rejections look like agreement; the seed read-back is what rules that out.
  it('returns null when the context cannot even store a plain hex', () => {
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({
      get fillStyle() { return '#000000'; },
      set fillStyle(_value: string) { /* ignores every write */ },
      measureText: (text: string) => ({ width: text.length }),
    } as unknown as CanvasRenderingContext2D);
    expect(cssColorToHex('red')).toBeNull();
    expect(cssColorToHex('not-a-color')).toBeNull();
  });

  // A context that stores whatever it is handed — the jsdom stub's behaviour. The re-parse of
  // the round-trip result is what keeps this from yielding garbage.
  it('does not trust a context that echoes its input', () => {
    installContext((v) => v);
    expect(cssColorToHex('red')).toBeNull();
    expect(cssColorToHex('oklch(0.5 0.1 200)')).toBeNull();
    expect(cssColorToHex('#ff0000')).toBe('#ff0000');
    expect(cssColorToHex('rgb(1 2 3)')).toBe('#010203');
  });
});
