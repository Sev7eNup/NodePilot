import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render } from '@testing-library/react';

/**
 * Rendering markdown runs the full remark + rehype + highlight pipeline. Both chat surfaces
 * keep the composer draft in the same component that renders the message list, so every
 * keystroke re-renders every message — an unchanged one must not be re-parsed.
 *
 * The ReactMarkdown mock counts pipeline invocations, which is the thing that costs.
 */
const mocks = vi.hoisted(() => ({ renders: 0 }));

vi.mock('react-markdown', () => ({
  default: ({ children }: { children: string }) => {
    mocks.renders += 1;
    return <div data-testid="md">{children}</div>;
  },
}));
vi.mock('remark-gfm', () => ({ default: () => {} }));
vi.mock('remark-breaks', () => ({ default: () => {} }));
vi.mock('rehype-highlight', () => ({ default: () => {} }));

import { Markdown } from '../../../components/common/Markdown';

describe('Markdown — memoization', () => {
  beforeEach(() => { mocks.renders = 0; });

  it('does not re-parse an unchanged message', () => {
    const { rerender } = render(<Markdown size="base">{'# Hello'}</Markdown>);
    expect(mocks.renders).toBe(1);

    rerender(<Markdown size="base">{'# Hello'}</Markdown>);
    rerender(<Markdown size="base">{'# Hello'}</Markdown>);

    expect(mocks.renders).toBe(1);
  });

  it('re-parses when the text changes, so a streaming message still updates', () => {
    const { rerender } = render(<Markdown>{'partial'}</Markdown>);
    rerender(<Markdown>{'partial answer'}</Markdown>);

    expect(mocks.renders).toBe(2);
  });

  it('re-parses when the size changes, because the plugin set differs', () => {
    const { rerender } = render(<Markdown size="sm">{'text'}</Markdown>);
    rerender(<Markdown size="base">{'text'}</Markdown>);

    expect(mocks.renders).toBe(2);
  });
});
