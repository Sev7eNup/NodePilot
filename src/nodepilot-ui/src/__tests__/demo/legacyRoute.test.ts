import { describe, expect, it } from 'vitest';
import { legacyDemoPath } from '../../../demo/boot/legacyRoute';

describe('demo bookmarks', () => {
  it.each([
    ['https://www.nodepilot.run/demo/#/workflows/abc', '/demo/workflows/abc'],
    ['https://www.nodepilot.run/demo/?lang=de&tour=file#/workflows', '/demo/workflows?lang=de&tour=file'],
    ['https://www.nodepilot.run/demo/?id=old&lang=de#/executions?id=new', '/demo/executions?lang=de&id=new'],
    ['https://www.nodepilot.run/demo/#/', '/demo/'],
    ['https://www.nodepilot.run/demo/workflows#section', null],
    ['https://www.nodepilot.run/demo/#//evil.example', '/demo/'],
    ['https://www.nodepilot.run/demo/#//%', '/demo/'],
  ])('normalizes %s', (input, expected) => {
    expect(legacyDemoPath(input)).toBe(expected);
  });
});
