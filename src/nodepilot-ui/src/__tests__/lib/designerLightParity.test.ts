import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Drift guard across two files: in a light skin the workflow designer must use the same surface
 * vocabulary as every other page. The canvas is the shell's page ground and the floating chrome
 * is a white plate on top of it. The app ramp lives in `index.css`, the designer ramp in
 * `designer-atelier.css`, and nothing else compares them.
 *
 * Dark skins are deliberately not covered *for the surface vocabulary*: there the chrome lifting
 * off a deeper canvas floor is the intended reading, and `e2e/designer-atelier.spec.ts` pins that
 * separately. The dot-grid contrast at the bottom of this file does span both bases — it is the
 * one designer token whose whole job is to stay legible on every ground.
 */

const CSS_DIR = join(__dirname, '..', '..');
const indexCss = readFileSync(join(CSS_DIR, 'index.css'), 'utf8');
const atelierCss = readFileSync(join(CSS_DIR, 'styles', 'designer-atelier.css'), 'utf8');
const minimalCss = readFileSync(join(CSS_DIR, 'styles', 'skin-minimal.css'), 'utf8');

/**
 * Text of a brace-matched block whose selector list contains `needle`. `html.dark {` occurs
 * several times in index.css, so `mustContain` picks the one that actually carries the ramp.
 */
function blockAfter(css: string, needle: string, mustContain = ''): string {
  let from = 0;
  for (;;) {
    const at = css.indexOf(needle, from);
    expect(at, `selector not found: ${needle}`).toBeGreaterThan(-1);
    const open = css.indexOf('{', at);
    let depth = 0;
    for (let i = open; i < css.length; i++) {
      if (css[i] === '{') depth++;
      else if (css[i] === '}' && --depth === 0) {
        const body = css.slice(open + 1, i);
        if (body.includes(mustContain)) return body;
        from = i;
        break;
      }
    }
    expect(depth, `unbalanced block for ${needle}`).toBe(0);
  }
}

function decl(block: string, name: string): string {
  const m = new RegExp(`${name}\\s*:\\s*([^;]+);`).exec(block);
  expect(m, `missing ${name}`).not.toBeNull();
  return m![1].trim();
}

/** The three light skins: where the shell ramp lives, and which Atelier block mirrors it. */
const LIGHT_SKINS = [
  { skin: 'light', shell: '@theme', atelier: 'html .np-designer.wd-atelier' },
  { skin: 'light-grey', shell: 'html[data-skin="light-grey"] .np-shell', atelier: 'html[data-skin="light-grey"] .np-designer.wd-atelier' },
  { skin: 'light-bank', shell: 'html[data-skin="light-bank"] .np-shell {', atelier: 'html[data-skin="light-bank"] .np-designer.wd-atelier' },
] as const;

describe('designer light-skin parity', () => {
  for (const { skin, shell, atelier } of LIGHT_SKINS) {
    it(`${skin}: the designer canvas is the shell's page ground`, () => {
      const ground = decl(blockAfter(indexCss, shell), '--color-surface-low');
      expect(decl(blockAfter(atelierCss, atelier), '--wd-canvas')).toBe(ground);
    });

    it(`${skin}: the designer chrome is a white plate, like .np-card`, () => {
      const plate = decl(blockAfter(indexCss, shell), '--color-surface-lowest');
      expect(plate).toBe('#ffffff');
      expect(decl(blockAfter(atelierCss, atelier), '--wd-panel')).toBe(plate);
    });
  }

  it('the mobile read-only graph gets the same light ground, and only in light', () => {
    // The mobile view borrows the `.np-designer` token scope but has no Atelier palette of its
    // own, so it would pick `bg-surface` for the canvas — the app's light reading upside down.
    // `html:not(.dark)` keeps the re-point out of the dark skins, where a deeper canvas floor
    // is the intended reading.
    const ground = blockAfter(indexCss, 'html:not(.dark) .np-mobile-canvas');
    expect(ground).toContain('var(--color-surface-low)');

    const mobile = readFileSync(join(CSS_DIR, 'pages', 'MobileWorkflowView.tsx'), 'utf8');
    expect(mobile, 'the mobile canvas needs its stable hook').toContain('np-mobile-canvas');

    const editor = readFileSync(join(CSS_DIR, 'pages', 'WorkflowEditorPage.tsx'), 'utf8');
    expect(editor, 'the canvas needs its stable hook').toContain('np-canvas flex-1');
  });

  it('the canvas dot grid and minimap mask are tokens in BOTH bases, not literals', () => {
    // A hardcoded rgba() in WorkflowEditorPage.tsx would pin the grid to one shade, so it would
    // no longer follow the active skin.
    const darkBase = blockAfter(indexCss, 'html.dark {', '--color-surface:');
    for (const token of ['--np-canvas-dot', '--np-minimap-mask']) {
      expect(blockAfter(indexCss, '@theme'), `light ${token}`).toContain(token);
      expect(darkBase, `dark ${token}`).toContain(token);
    }

    const editor = readFileSync(join(CSS_DIR, 'pages', 'WorkflowEditorPage.tsx'), 'utf8');
    const background = /<Background[\s\S]{0,400}?\/>/g;
    for (const el of editor.match(background) ?? []) {
      expect(el, 'Background must not carry a colour literal').not.toMatch(/color=\{?['"]?rgba?\(/);
    }
    expect(editor, 'maskColor must not carry a colour literal').not.toMatch(/maskColor=\{[^}]*rgba?\(/);
  });
});

/**
 * Every skin's canvas ground, and where its dot colour is declared. The three light skins and the
 * three non-Azur dark skins share their base declaration; Azur is the only skin that moves the
 * token itself, because its dots are deliberately blue-tinted.
 */
const CANVAS_GRIDS = [
  { skin: 'light', base: 'light', token: '@theme', ground: 'html .np-designer.wd-atelier' },
  { skin: 'light-grey', base: 'light', token: '@theme', ground: 'html[data-skin="light-grey"] .np-designer.wd-atelier' },
  { skin: 'light-bank', base: 'light', token: '@theme', ground: 'html[data-skin="light-bank"] .np-designer.wd-atelier' },
  { skin: 'dark', base: 'dark', token: 'html.dark[data-skin="dark"] {', ground: 'html.dark .np-designer.wd-atelier' },
  { skin: 'dark-lila', base: 'dark', token: 'html.dark {', ground: 'html.dark[data-skin="dark-lila"] .np-designer.wd-atelier' },
  { skin: 'dark-bank', base: 'dark', token: 'html.dark {', ground: 'html.dark[data-skin="dark-bank"] .np-designer.wd-atelier' },
  { skin: 'dark-nebula', base: 'dark', token: 'html.dark {', ground: 'html.dark[data-skin="dark-nebula"] .np-designer.wd-atelier' },
] as const;

/**
 * Two thresholds, not one: a near-white ground cannot physically reach a dark ground's ratio
 * without the dots reading as a texture rather than a scale reference.
 */
const MIN_CONTRAST = { light: 1.6, dark: 2.6 };

function parseHex(hex: string): [number, number, number] {
  const m = /^#([0-9a-f]{6})$/i.exec(hex.trim());
  expect(m, `not a 6-digit hex colour: ${hex}`).not.toBeNull();
  const n = Number.parseInt(m![1], 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function parseRgba(value: string): [number, number, number, number] {
  const m = /^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)$/.exec(value.trim());
  expect(m, `not an rgb(a) colour: ${value}`).not.toBeNull();
  return [Number(m![1]), Number(m![2]), Number(m![3]), m![4] === undefined ? 1 : Number(m![4])];
}

/** WCAG relative luminance of an opaque sRGB colour. */
function luminance([r, g, b]: [number, number, number]): number {
  const lin = [r, g, b].map((c) => {
    const v = c / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2];
}

/** Contrast of a translucent dot composited over its opaque ground. */
function dotContrast(dot: string, groundHex: string): number {
  const ground = parseHex(groundHex);
  const [r, g, b, alpha] = parseRgba(dot);
  const over = [r, g, b].map((c, i) => ground[i] + alpha * (c - ground[i])) as [number, number, number];
  const [lo, hi] = [luminance(over), luminance(ground)].sort((x, y) => x - y);
  return (hi + 0.05) / (lo + 0.05);
}

describe('canvas dot grid contrast', () => {
  for (const { skin, base, token, ground } of CANVAS_GRIDS) {
    it(`${skin}: the grid stays legible on its own canvas ground`, () => {
      // The dot grid is the scale reference for the whole graph. Both halves of the ratio live in
      // different files, so raising a canvas floor without following the dot alpha silently
      // dissolves it — which is exactly how it went unreadable before.
      const dot = decl(blockAfter(indexCss, token, '--np-canvas-dot'), '--np-canvas-dot');
      const canvas = decl(blockAfter(atelierCss, ground, '--wd-canvas'), '--wd-canvas');

      expect(dotContrast(dot, canvas)).toBeGreaterThanOrEqual(MIN_CONTRAST[base]);
    });
  }
});

function contrast(foreground: string, background: string): number {
  const [lo, hi] = [luminance(parseHex(foreground)), luminance(parseHex(background))].sort((a, b) => a - b);
  return (hi + 0.05) / (lo + 0.05);
}

describe('Minimal palette parity and contrast', () => {
  const sharedSelector = 'html:is([data-skin="light-minimal"], [data-skin="dark-minimal"]),';
  const shared = blockAfter(minimalCss, sharedSelector, '--wd-canvas');

  it('applies the same palette to document portals, shell and Atelier scopes', () => {
    const start = minimalCss.indexOf(sharedSelector);
    const selector = minimalCss.slice(start, minimalCss.indexOf('{', start));
    for (const scope of ['.np-shell', '.np-designer', '.np-designer.wd-atelier', '.np-tooltip-portal']) {
      expect(selector).toContain(scope);
    }
    for (const [designer, shell] of [
      ['--wd-canvas', '--color-surface'], ['--wd-panel', '--color-surface-lowest'],
      ['--wd-ink', '--color-on-surface'], ['--wd-ink-muted', '--color-on-surface-variant'],
      ['--wd-outline', '--color-outline'], ['--wd-accent', '--color-primary'],
    ]) {
      expect(decl(shared, designer), `${designer} matches ${shell}`).toBe(decl(shared, shell));
    }
    expect(decl(shared, '--np-reduced-decoration')).toBe('1');
  });

  for (const base of ['light', 'dark'] as const) {
    const palette = blockAfter(minimalCss, `html[data-skin="${base}-minimal"] {`);
    const color = (name: string) => decl(palette, `--np-min-${name}`);

    it(`${base}-minimal: main and secondary text meet 4.5:1 across neutral surfaces`, () => {
      for (const background of ['ground', 'panel', 'subtle', 'hover']) {
        for (const foreground of ['ink', 'muted']) {
          expect(contrast(color(foreground), color(background)), `${foreground} on ${background}`).toBeGreaterThanOrEqual(4.5);
        }
      }
    });

    it(`${base}-minimal: action and status text meet 4.5:1 on their rendered fills`, () => {
      for (const background of ['accent', 'accent-hover']) {
        expect(contrast(color('on-accent'), color(background)), `button text on ${background}`).toBeGreaterThanOrEqual(4.5);
      }
      for (const status of ['accent', 'success', 'warning', 'error', 'custom']) {
        for (const background of ['panel', `${status}-soft`]) {
          expect(contrast(color(status), color(background)), `${status} on ${background}`).toBeGreaterThanOrEqual(4.5);
        }
      }
    });

    it(`${base}-minimal: controls, focus and idle edges meet 3:1 on neutral surfaces`, () => {
      for (const foreground of ['control', 'accent']) {
        for (const background of ['ground', 'panel', 'subtle', 'hover']) {
          expect(contrast(color(foreground), color(background)), `${foreground} on ${background}`).toBeGreaterThanOrEqual(3);
        }
      }
      expect(decl(shared, '--np-edge-idle')).toBe('var(--np-min-control)');
    });

    it(`${base}-minimal: code syntax meets 4.5:1 on editor and markdown surfaces`, () => {
      for (const token of ['comment', 'keyword', 'string', 'number', 'function', 'type', 'variable', 'attribute', 'meta', 'deletion', 'addition']) {
        for (const background of ['panel', 'ground', 'subtle']) {
          expect(contrast(decl(palette, `--np-code-${token}`), color(background)), `${token} on ${background}`).toBeGreaterThanOrEqual(4.5);
        }
      }
    });

    it(`${base}-minimal: the dot grid remains legible after alpha compositing`, () => {
      const dot = decl(palette, '--np-canvas-dot');
      expect(dotContrast(dot, color('ground'))).toBeGreaterThanOrEqual(MIN_CONTRAST[base]);
    });
  }
});
