import { describe, it, expect } from 'vitest';
import { parseOutputParametersJson } from '../../lib/outputParameters';

/**
 * parseOutputParametersJson is the shared defensive-parse helper for the backend-persisted
 * `StepExecution.OutputParametersJson` (string->string). We pin:
 *   - empty/missing JSON -> null (no params; the caller decides on a fallback)
 *   - a valid flat object -> a map with stringified values
 *   - non-string values (number/boolean/null) get stringified, null becomes ''
 *   - arrays / primitives / malformed JSON -> null (never throws)
 */
describe('parseOutputParametersJson', () => {
  it('emptyOrMissingJson_returnsNull', () => {
    expect(parseOutputParametersJson(undefined)).toBeNull();
    expect(parseOutputParametersJson(null)).toBeNull();
    expect(parseOutputParametersJson('')).toBeNull();
  });

  it('validObject_returnsStringifiedMap', () => {
    const map = parseOutputParametersJson('{"host":"srv01","count":3}');
    expect(map).toEqual({ host: 'srv01', count: '3' });
  });

  it('nonStringValues_areStringified_withNullBecomingEmpty', () => {
    const map = parseOutputParametersJson('{"flag":true,"n":0,"gap":null}');
    expect(map).toEqual({ flag: 'true', n: '0', gap: '' });
  });

  it('array_returnsNull_notArray', () => {
    expect(parseOutputParametersJson('[1,2,3]')).toBeNull();
  });

  it('primitiveJson_returnsNull', () => {
    expect(parseOutputParametersJson('"hello"')).toBeNull();
    expect(parseOutputParametersJson('42')).toBeNull();
  });

  it('malformedJson_returnsNull_doesNotThrow', () => {
    expect(parseOutputParametersJson('{not json')).toBeNull();
  });

  it('preservesAllKeys_forDatabusHydration', () => {
    // signalrReducer.buildDatabusFromHydratedSteps relies on ALL declared params making it
    // through — a missing key here would corrupt downstream {{step.param.X}} resolution.
    const map = parseOutputParametersJson('{"a":"1","b":"2","c":"3"}');
    expect(Object.keys(map ?? {})).toEqual(['a', 'b', 'c']);
  });
});