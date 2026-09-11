import { describe, it, expect } from 'vitest';
import { shallowEqual } from '../../lib/shallowEqual';

describe('shallowEqual', () => {
  it('treats a rebuilt object with the same entries as equal', () => {
    // This is the case the canvas memo comparators exist for: useDisplayedGraph builds a fresh
    // data object for every node on every projection pass.
    const config = { script: 'Get-Date' };
    expect(shallowEqual({ label: 'A', config }, { label: 'A', config })).toBe(true);
  });

  it('reports a difference when any value changes', () => {
    expect(shallowEqual({ label: 'A' }, { label: 'B' })).toBe(false);
  });

  it('compares nested objects by reference, not by content', () => {
    // Node config objects are replaced wholesale on edit, so reference comparison is enough
    // and keeps the check cheap.
    expect(shallowEqual({ config: { a: 1 } }, { config: { a: 1 } })).toBe(false);
  });

  it('reports a difference when a key is added or removed', () => {
    expect(shallowEqual({ a: 1 }, { a: 1, b: 2 })).toBe(false);
    expect(shallowEqual({ a: 1, b: 2 }, { a: 1 })).toBe(false);
  });

  it('distinguishes an explicit undefined from a missing key', () => {
    expect(shallowEqual({ a: undefined }, {})).toBe(false);
  });

  it('handles the same reference and both-undefined', () => {
    const o = { a: 1 };
    expect(shallowEqual(o, o)).toBe(true);
    expect(shallowEqual(undefined, undefined)).toBe(true);
  });

  it('reports a difference when only one side is undefined', () => {
    expect(shallowEqual(undefined, { a: 1 })).toBe(false);
    expect(shallowEqual({ a: 1 }, undefined)).toBe(false);
  });

  it('treats NaN values as equal', () => {
    // Object.is semantics, so a numeric marker that is NaN does not force an endless re-render.
    expect(shallowEqual({ a: Number.NaN }, { a: Number.NaN })).toBe(true);
  });
});
