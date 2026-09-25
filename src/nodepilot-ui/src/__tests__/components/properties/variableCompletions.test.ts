import { describe, it, expect } from 'vitest';
import { EditorState } from '@codemirror/state';
import { CompletionContext } from '@codemirror/autocomplete';
import { variableCompletions } from '../../../components/designer/properties/panelChrome';

const REFS = [{ expression: '{{disk.output}}', label: 'Check Disk' }];

function complete(doc: string, pos: number, explicit = false) {
  const state = EditorState.create({ doc });
  const result = variableCompletions(new CompletionContext(state, pos, explicit), REFS);
  const apply = (label: string) => result ? doc.slice(0, result.from) + label + doc.slice(result.to ?? pos) : doc;
  return { result, apply };
}

describe('variableCompletions', () => {
  it('variableCompletions_AutoClosedBraces_ReplacesThemWithThePick', () => {
    // closeBrackets turned the typed `{{` into `{{}}` with the cursor in the middle.
    const doc = '$free = {{}}';
    const { result, apply } = complete(doc, 10);
    expect(result?.from).toBe(8);
    expect(result?.to).toBe(12);
    expect(apply('{{disk.output}}')).toBe('$free = {{disk.output}}');
  });

  it('variableCompletions_NoClosers_ReplacesOnlyTypedPrefix', () => {
    const doc = '$free = {{di';
    const { result, apply } = complete(doc, doc.length);
    expect(result?.to).toBe(doc.length);
    expect(apply('{{disk.output}}')).toBe('$free = {{disk.output}}');
  });

  it('variableCompletions_SingleClosingBrace_IsAlsoConsumed', () => {
    const doc = '{{di}';
    const { apply } = complete(doc, 4);
    expect(apply('{{disk.output}}')).toBe('{{disk.output}}');
  });

  it('variableCompletions_NoDoubleBraceBeforeCursor_ReturnsNull', () => {
    expect(complete('$free = 5', 9).result).toBeNull();
  });
});
