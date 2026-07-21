import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { ACTIVITY_CATALOG } from '../../lib/activityCatalog.generated';

/**
 * Every activity in ACTIVITY_CATALOG renders with three CSS custom properties in
 * src/components/designer/nodes/activityConfig.ts:
 *
 *   color       → var(--act-<type>-color)
 *   bgColor     → var(--act-<type>-bg)
 *   borderColor → var(--act-<type>-border)
 *
 * If any of them is missing, the CSS engine silently returns an empty string and the
 * node renders as a transparent ghost on the canvas — visible icon and label, no fill,
 * no border. The Backend/Frontend catalog drift-test catches a missing TYPE entry; this
 * one catches a missing PALETTE entry, which is a third registry (index.css) the drift
 * test doesn't look at.
 *
 * We also require each variable to be declared TWICE: once in the light-mode `:root`
 * block and once under `html.dark` for dark-mode override. A single declaration would
 * leave one theme broken.
 */

const REQUIRED_SUFFIXES = ['color', 'bg', 'border'] as const;

// Resolve index.css relative to this test file. `import.meta.url` is the test file
// itself; up four levels (../../../../) lands at src/nodepilot-ui, then into src/index.css.
const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const cssText = readFileSync(join(__dirname, '..', '..', 'index.css'), 'utf8');

function countDeclarations(css: string, varName: string): number {
  // Match `--act-foo-color:` as the declaration form. Comments and the var() call sites
  // use `var(--act-foo-color)` (no trailing colon) and won't match — exactly the
  // disambiguation we want, otherwise a stray reference in an inline comment would
  // pretend the var is defined.
  const escaped = varName.replace(/[-\\]/g, '\\$&');
  const re = new RegExp(`${escaped}\\s*:`, 'g');
  return (css.match(re) ?? []).length;
}

describe('Activity CSS palette', () => {
  for (const activity of ACTIVITY_CATALOG) {
    describe(`activity "${activity.type}"`, () => {
      for (const suffix of REQUIRED_SUFFIXES) {
        const varName = `--act-${activity.type}-${suffix}`;

        it(`declares ${varName} in both light and dark mode`, () => {
          const count = countDeclarations(cssText, varName);
          expect(
            count,
            `Expected ${varName} to be declared exactly twice in index.css ` +
            `(once under :root for light mode, once under html.dark for dark mode). ` +
            `Found ${count} declaration(s). A missing palette entry renders the ` +
            `<ActivityNode> as a transparent ghost on the canvas — see lime-green ` +
            `palette for textFileEdit as the reference example.`
          ).toBe(2);
        });
      }
    });
  }
});
