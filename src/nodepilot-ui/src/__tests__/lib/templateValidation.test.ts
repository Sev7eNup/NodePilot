import { describe, expect, it } from 'vitest';
import { validateTemplateExpression, hasTemplatePlaceholder } from '../../lib/templateValidation';
import type { UpstreamVariable } from '../../lib/upstreamVariables';

const upstream: UpstreamVariable[] = [
  { stepId: 'a', label: 'A', variable: 'alpha.output', expression: '{{alpha.output}}', type: 'string' },
  { stepId: 'a', label: 'A -> count', variable: 'alpha.param.count', expression: '{{alpha.param.count}}', type: 'number' },
];

describe('validateTemplateExpression', () => {
  it('accepts upstream and global references', () => {
    expect(validateTemplateExpression('x {{alpha.output}} {{globals.SECRET}}', upstream).status).toBe('ok');
  });

  it('errors on malformed expressions', () => {
    const result = validateTemplateExpression('x {{alpha.output', upstream);
    expect(result.status).toBe('error');
    expect(result.issues[0].message).toMatch(/closing braces/);
  });

  it('warns when the producer is not upstream', () => {
    const result = validateTemplateExpression('{{beta.output}}', upstream);
    expect(result.status).toBe('warning');
    expect(result.issues[0].message).toMatch(/not upstream/);
  });

  it('accepts manual.* runtime references without warning', () => {
    // Trigger data and forEach child params ({{manual.item}}/{{manual.index}}) are
    // runtime-injected — the publish-time lint tolerates them, so the inline field
    // validator must too (this was a false-positive ERROR before).
    expect(validateTemplateExpression('{{manual.item}}', upstream).status).toBe('ok');
    expect(validateTemplateExpression('{{manual.index}} of {{manual.ticketId}}', upstream).status).toBe('ok');
  });

  it('accepts the success tail (documented contract tail)', () => {
    expect(validateTemplateExpression('{{alpha.success}}', upstream).status).toBe('ok');
  });

  it('skips nested/dynamic template construction instead of mis-flagging it', () => {
    expect(validateTemplateExpression('{{globals.{{x}}}}', upstream).status).toBe('ok');
  });

  it('still errors on genuinely unsupported shapes', () => {
    const result = validateTemplateExpression('{{alpha.nonsenseTail}}', upstream);
    expect(result.status).toBe('error');
  });

  it('tolerates a non-string value instead of crashing', () => {
    // Field values are typed `string`, but malformed node data can carry a non-string (e.g. a
    // restApi `headers` object). This must never throw `value.includes is not a function` and
    // take down the whole designer.
    const objectValue = { 'Content-Type': 'application/json' } as unknown as string;
    expect(hasTemplatePlaceholder(objectValue)).toBe(false);
    expect(() => validateTemplateExpression(objectValue, upstream)).not.toThrow();
    expect(validateTemplateExpression(objectValue, upstream).status).toBe('ok');
  });
});
