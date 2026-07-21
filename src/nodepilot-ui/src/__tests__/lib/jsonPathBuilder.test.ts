import { describe, expect, it } from 'vitest';
import { buildJsonPath, tryParseJson } from '../../lib/jsonPathBuilder';

describe('jsonPathBuilder', () => {
  it('builds Newtonsoft-compatible object and array paths', () => {
    expect(buildJsonPath(['items', 0, 'name'])).toBe('$.items[0].name');
    expect(buildJsonPath(['weird-key', "owner's name"])).toBe("$['weird-key']['owner\\'s name']");
  });

  it('parses JSON defensively', () => {
    expect(tryParseJson('{"ok":true}')).toEqual({ ok: true });
    expect(tryParseJson('not json')).toBeNull();
  });
});
