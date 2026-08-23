import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Drift guard across two files: in a LIGHT skin the workflow designer must use the same surface
 * vocabulary as every other page — the canvas is the shell's page ground, the floating chrome is
 * a white plate on top of it.
 *
 * Atelier used to invert that on a warm paper ramp (canvas `#faf8f3`, chrome `#ece9e1`), which is
 * why the designer read greyer and darker than the dashboard next to it. Nothing mechanical
 * stopped it: the app ramp lives in `index.css` and the designer ramp in `designer-atelier.css`,
 * and no test compared them. This one does.
 *
 * Dark skins are deliberately NOT covered — there the chrome lifting off a deeper canvas floor is
 * the intended reading, and `e2e/designer-atelier.spec.ts` pins that separately.
 */

const CSS_DIR = join(__dirname, '..', '..');
const indexCss = readFileSync(join(CSS_DIR, 'index.css'), 'utf8');
const atelierCss = readFileSync(join(CSS_DIR, 'styles', 'designer-atelier.css'), 'utf8');

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

  it('the canvas dot grid and minimap masks are tokens in BOTH bases, not literals', () => {
    // The light values used to be hardcoded rgba() in WorkflowEditorPage.tsx, so the grid stayed
    // a flat 42% black no matter which light skin was on.
    const darkBase = blockAfter(indexCss, 'html.dark {', '--color-surface:');
    for (const token of ['--np-canvas-dot', '--np-minimap-mask', '--np-minimap-mask-atelier']) {
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
