import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { ACTIVITY_CATALOG } from '../../lib/activityCatalog.generated';

/**
 * Every activity in ACTIVITY_CATALOG is rendered with three CSS custom properties by
 * src/components/designer/nodes/activityConfig.ts: --act-<type>-color, --act-<type>-bg and
 * --act-<type>-border. A missing property resolves to an empty string, so the node draws
 * without fill or border. Each variable must be declared twice, once under :root for light
 * mode and once under html.dark, otherwise one of the two themes is broken.
 */

const REQUIRED_SUFFIXES = ['color', 'bg', 'border'] as const;

// Resolve index.css relative to this test file.
const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const cssText = readFileSync(join(__dirname, '..', '..', 'index.css'), 'utf8');

function countDeclarations(css: string, varName: string): number {
  // Match the declaration form `--act-foo-color:`. Call sites write `var(--act-foo-color)`
  // without a trailing colon, so a reference alone does not count as a declaration.
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
