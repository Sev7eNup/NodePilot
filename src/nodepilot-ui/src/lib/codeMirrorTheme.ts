import { EditorView } from '@codemirror/view';
import { HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { tags } from '@lezer/highlight';

/**
 * NodePilot's CodeMirror look, built from the app's own design tokens.
 *
 * CodeMirror compiles theme objects into real stylesheets, so `var(--…)` resolves at paint
 * time and the editor follows skin and light/dark changes without a token probe or a
 * MutationObserver. `isDark` is still passed in because CodeMirror uses it for selection
 * blending and `color-scheme`, not for colour lookup. Syntax colours come from the shared
 * `--np-code-*` set in index.css, the same set the `.hljs-*` rules use for AI-chat code blocks.
 */

function chrome(isDark: boolean) {
  return EditorView.theme(
    {
      '&': {
        backgroundColor: 'var(--color-surface-lowest)',
        color: 'var(--color-on-surface)',
      },
      // Without this CodeMirror falls back to its built-in `monospace`, so code would render
      // in the browser default font instead of the app font.
      '.cm-content': { caretColor: 'var(--color-primary)', fontFamily: 'var(--font-mono)' },
      '.cm-cursor, .cm-dropCursor': { borderLeftColor: 'var(--color-primary)' },
      '&.cm-focused .cm-selectionBackground, .cm-selectionBackground, .cm-content ::selection': {
        backgroundColor: 'color-mix(in srgb, var(--color-primary) 26%, transparent)',
      },
      '.cm-gutters': {
        backgroundColor: 'var(--color-surface-lowest)',
        color: 'var(--color-on-surface-variant)',
        borderRight: '1px solid var(--color-outline-variant)',
        // Gutters do not inherit from `.cm-content`, so line numbers would otherwise use a
        // different font than the code beside them.
        fontFamily: 'var(--font-mono)',
      },
      '.cm-activeLineGutter': { backgroundColor: 'transparent' },
      '.cm-lineNumbers .cm-gutterElement': { color: 'var(--np-code-meta)' },
      '.cm-placeholder': { color: 'var(--color-outline)' },
      '.cm-panels': {
        backgroundColor: 'var(--color-surface-high)',
        color: 'var(--color-on-surface)',
      },
      '.cm-searchMatch': {
        backgroundColor: 'color-mix(in srgb, var(--color-primary) 22%, transparent)',
        outline: '1px solid color-mix(in srgb, var(--color-primary) 45%, transparent)',
      },
      '.cm-searchMatch.cm-searchMatch-selected': {
        backgroundColor: 'color-mix(in srgb, var(--color-primary) 42%, transparent)',
      },
      '.cm-selectionMatch': {
        backgroundColor: 'color-mix(in srgb, var(--color-on-surface) 12%, transparent)',
      },
      '.cm-matchingBracket, .cm-nonmatchingBracket': {
        backgroundColor: 'color-mix(in srgb, var(--color-on-surface) 14%, transparent)',
        outline: '1px solid var(--color-outline)',
      },
      '.cm-tooltip': {
        backgroundColor: 'var(--color-surface-high)',
        borderColor: 'var(--color-outline-variant)',
        color: 'var(--color-on-surface)',
      },
      '.cm-tooltip-autocomplete > ul > li[aria-selected]': {
        backgroundColor: 'var(--color-primary-fixed)',
        color: 'var(--color-on-primary-fixed)',
      },
    },
    { dark: isDark },
  );
}

/** Maps Lezer tags onto the shared `--np-code-*` palette. Exported so the token wiring can
 *  be asserted without booting an editor. */
export const nodePilotHighlightStyle = HighlightStyle.define([
  { tag: [tags.comment, tags.lineComment, tags.blockComment, tags.quote], color: 'var(--np-code-comment)', fontStyle: 'italic' },
  { tag: [tags.keyword, tags.controlKeyword, tags.operatorKeyword, tags.modifier, tags.self, tags.null], color: 'var(--np-code-keyword)' },
  { tag: [tags.string, tags.special(tags.string), tags.regexp], color: 'var(--np-code-string)' },
  { tag: [tags.number, tags.bool, tags.literal], color: 'var(--np-code-number)' },
  { tag: [tags.function(tags.variableName), tags.function(tags.propertyName), tags.labelName, tags.heading], color: 'var(--np-code-function)' },
  { tag: [tags.typeName, tags.className, tags.namespace, tags.tagName], color: 'var(--np-code-type)' },
  { tag: [tags.variableName, tags.propertyName, tags.definition(tags.variableName)], color: 'var(--np-code-variable)' },
  { tag: [tags.attributeName, tags.attributeValue], color: 'var(--np-code-attribute)' },
  { tag: [tags.meta, tags.processingInstruction, tags.documentMeta], color: 'var(--np-code-meta)' },
  { tag: [tags.operator, tags.punctuation, tags.separator, tags.bracket], color: 'var(--color-on-surface-variant)' },
  { tag: tags.deleted, color: 'var(--np-code-deletion)' },
  { tag: tags.inserted, color: 'var(--np-code-addition)' },
  { tag: tags.invalid, color: 'var(--color-error)' },
  { tag: tags.link, color: 'var(--color-primary)', textDecoration: 'underline' },
  { tag: tags.strong, fontWeight: 'bold' },
  { tag: tags.emphasis, fontStyle: 'italic' },
]);

/**
 * The editor theme for the given base. Pass the result straight to `<CodeMirror theme={…}>`.
 *
 * Memoised per base so each extension array is created once; a new array identity on every
 * render would make CodeMirror reconfigure the editor on each keystroke.
 */
function build(isDark: boolean) {
  return [chrome(isDark), syntaxHighlighting(nodePilotHighlightStyle)];
}

const CACHE = new Map<boolean, ReturnType<typeof build>>();

export function nodePilotCodeMirrorTheme(isDark: boolean): ReturnType<typeof build> {
  const cached = CACHE.get(isDark);
  if (cached) return cached;
  const built = build(isDark);
  CACHE.set(isDark, built);
  return built;
}
