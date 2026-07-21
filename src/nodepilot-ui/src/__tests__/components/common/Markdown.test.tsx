import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { Markdown } from '../../../components/common/Markdown';

describe('Markdown code highlighting', () => {
  it('syntax-highlights a fenced code block via rehype-highlight', () => {
    const md = [
      '```powershell',
      '$svc = Get-Service',
      'if ($svc.Status -eq "Running") { Write-Output "up" }',
      '```',
    ].join('\n');
    const { container } = render(<Markdown>{md}</Markdown>);

    // rehype-highlight tags the <code> with `hljs language-xxx`; the block override forwards it.
    const code = container.querySelector('code.hljs');
    expect(code).not.toBeNull();
    expect(code!.className).toContain('language-powershell');

    // Tokens are wrapped in .hljs-* spans (e.g. hljs-string for "Running").
    expect(container.querySelector('[class*="hljs-"]')).not.toBeNull();

    // The full source survives through the nested token spans → CopyButton's nodeText() still works.
    expect(code!.textContent).toContain('Get-Service');
    expect(code!.textContent).toContain('Running');
  });

  it('renders inline code without the hljs treatment', () => {
    const { container } = render(<Markdown>{'Use `Get-Service` inline.'}</Markdown>);
    const inline = container.querySelector('code:not(.hljs)');
    expect(inline).not.toBeNull();
    expect(inline!.textContent).toBe('Get-Service');
    expect(container.querySelector('code.hljs')).toBeNull();
  });

  it('leaves GFM markdown (bold) unaffected', () => {
    const { getByText } = render(<Markdown>{'This is **bold** text.'}</Markdown>);
    expect(getByText('bold').tagName.toLowerCase()).toBe('strong');
  });
});
