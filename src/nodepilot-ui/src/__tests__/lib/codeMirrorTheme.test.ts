import { describe, expect, it } from 'vitest';
import { nodePilotCodeMirrorTheme, nodePilotHighlightStyle } from '../../lib/codeMirrorTheme';

describe('nodePilotCodeMirrorTheme', () => {
  it('returnsAStableIdentityPerBase', () => {
    // A fresh array on every render would make CodeMirror reconfigure the editor
    // on each keystroke, so the extension must be memoised per light/dark base.
    expect(nodePilotCodeMirrorTheme(true)).toBe(nodePilotCodeMirrorTheme(true));
    expect(nodePilotCodeMirrorTheme(false)).toBe(nodePilotCodeMirrorTheme(false));
    expect(nodePilotCodeMirrorTheme(true)).not.toBe(nodePilotCodeMirrorTheme(false));
  });

  it('shipsBothChromeAndSyntaxHighlighting', () => {
    expect(nodePilotCodeMirrorTheme(true)).toHaveLength(2);
  });
});

describe('nodePilotHighlightStyle', () => {
  const rules = nodePilotHighlightStyle.module?.getRules() ?? '';

  it('drawsSyntaxColoursFromTheSharedNpCodeTokens', () => {
    // The editors and the AI-chat `.hljs-*` blocks read the same custom properties, so a
    // colour is declared once in index.css and both surfaces follow every skin switch.
    expect(rules).not.toBe('');
    for (const token of [
      '--np-code-comment',
      '--np-code-keyword',
      '--np-code-string',
      '--np-code-number',
      '--np-code-function',
      '--np-code-type',
      '--np-code-variable',
      '--np-code-attribute',
      '--np-code-meta',
      '--np-code-deletion',
      '--np-code-addition',
    ]) {
      expect(rules).toContain(`var(${token})`);
    }
  });

  it('containsNoHardcodedHexColours', () => {
    // A literal here would pin one skin's palette into the editor and silently
    // drift from index.css.
    expect(rules).not.toMatch(/#[0-9a-fA-F]{3,8}\b/);
  });
});
