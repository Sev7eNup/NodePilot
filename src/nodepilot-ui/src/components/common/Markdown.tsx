import { type ReactNode } from 'react';
import ReactMarkdown, { type Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import remarkBreaks from 'remark-breaks';
import rehypeHighlight from 'rehype-highlight';
import powershell from 'highlight.js/lib/languages/powershell';
import csharp from 'highlight.js/lib/languages/csharp';
import typescript from 'highlight.js/lib/languages/typescript';
import javascript from 'highlight.js/lib/languages/javascript';
import json from 'highlight.js/lib/languages/json';
import bash from 'highlight.js/lib/languages/bash';
import sql from 'highlight.js/lib/languages/sql';
import yaml from 'highlight.js/lib/languages/yaml';
import xml from 'highlight.js/lib/languages/xml';
import css from 'highlight.js/lib/languages/css';
import python from 'highlight.js/lib/languages/python';
import markdown from 'highlight.js/lib/languages/markdown';
import diff from 'highlight.js/lib/languages/diff';
import { CopyButton } from './CopyButton';

// Scoped highlight.js grammar set — the languages NodePilot answers actually use. Deliberately
// registers powershell + csharp, which are NOT in rehype-highlight's default "common" set yet are
// the primary languages here (runScript / source-code answers). Aliases (ts/js/sh/yml/…) let a
// fenced block match either spelling. Keeping it scoped also keeps the lazy chunk lean.
const highlightLanguages = {
  powershell, ps1: powershell,
  csharp, cs: csharp, 'c#': csharp,
  typescript, ts: typescript, tsx: typescript,
  javascript, js: javascript, jsx: javascript,
  json,
  bash, sh: bash, shell: bash,
  sql,
  yaml, yml: yaml,
  xml, html: xml,
  css,
  python, py: python,
  markdown, md: markdown,
  diff,
};

/** Extracts the plain text from a (possibly nested) ReactNode — used for the "copy code block" button. */
function nodeText(node: ReactNode): string {
  if (typeof node === 'string') return node;
  if (typeof node === 'number') return String(node);
  if (Array.isArray(node)) return node.map(nodeText).join('');
  if (node && typeof node === 'object' && 'props' in node) {
    return nodeText((node as { props: { children?: ReactNode } }).props.children);
  }
  return '';
}

/** Prose scale. `sm` = the original compact size (workflow-designer dock); `base` = a larger,
 *  more readable size for the full-page AI Chat. */
export type MarkdownSize = 'sm' | 'base';

/**
 * Lightweight Markdown renderer for AI chat answers. The project doesn't include
 * `@tailwindcss/typography`, so block/inline elements are styled by hand with Tailwind
 * classes to match the rest of the UI (`on-surface` tones). `remark-gfm` adds GitHub-flavored
 * Markdown; `rehype-highlight` colors fenced code blocks. The `size` prop scales the type — the
 * default (`sm`) is byte-identical to the previous behavior, so existing callers are unaffected.
 */
function makeComponents(size: MarkdownSize): Components {
  const base = size === 'base';
  const codeText = base ? 'text-xs' : 'text-[11px]';
  const hSize12 = base ? 'text-base' : 'text-sm';
  const hSize3 = base ? 'text-sm' : 'text-xs';
  // Vertical rhythm — `base` (full-page chat) is roomier for human readability (more space between
  // paragraphs, clear breathing room before headings, airier lists); `sm` (designer dock) stays
  // compact and byte-identical to before.
  const pSpace = base ? 'my-3 leading-relaxed' : 'my-1.5 leading-snug';
  const hSpace12 = base ? 'mt-6 mb-2.5' : 'mt-2 mb-1';
  const hSpace3 = base ? 'mt-5 mb-2' : 'mt-2 mb-1';
  const listSpace = base ? 'my-3 ml-5' : 'my-1.5 ml-4';
  const liGap = base ? 'space-y-1.5' : 'space-y-0.5';
  const liLead = base ? 'leading-relaxed' : 'leading-snug';
  const blockSpace = base ? 'my-4' : 'my-1.5';
  const hrSpace = base ? 'my-6' : 'my-2';
  return {
    p: ({ children }) => <p className={`${pSpace} first:mt-0 last:mb-0`}>{children}</p>,
    h1: ({ children }) => <h1 className={`${hSpace12} ${hSize12} font-headline font-bold text-on-surface first:mt-0`}>{children}</h1>,
    h2: ({ children }) => <h2 className={`${hSpace12} ${hSize12} font-headline font-bold text-on-surface first:mt-0`}>{children}</h2>,
    h3: ({ children }) => <h3 className={`${hSpace3} ${hSize3} font-label font-bold uppercase tracking-wide text-on-surface-variant first:mt-0`}>{children}</h3>,
    ul: ({ children }) => <ul className={`${listSpace} list-disc ${liGap}`}>{children}</ul>,
    ol: ({ children }) => <ol className={`${listSpace} list-decimal ${liGap}`}>{children}</ol>,
    li: ({ children }) => <li className={liLead}>{children}</li>,
    strong: ({ children }) => <strong className="font-semibold text-on-surface">{children}</strong>,
    em: ({ children }) => <em className="italic">{children}</em>,
    a: ({ children, href }) => (
      <a href={href} target="_blank" rel="noopener noreferrer" className="text-primary underline hover:brightness-110">
        {children}
      </a>
    ),
    code: ({ className, children }) => {
      // After rehype-highlight runs, a fenced block arrives with `hljs language-xxx` on the <code>
      // and token <span>s as children. Forward that className (instead of discarding it) so the
      // .hljs-* token colors apply; keep the surface-box styling alongside.
      const isBlock = (className ?? '').includes('language-') || (className ?? '').includes('hljs');
      if (isBlock) {
        return (
          <code className={`${className ?? ''} block overflow-x-auto rounded bg-surface-high px-2 py-1.5 font-mono ${codeText} text-on-surface`}>
            {children}
          </code>
        );
      }
      return <code className={`rounded bg-surface-high px-1 py-0.5 font-mono ${codeText} text-on-surface`}>{children}</code>;
    },
    pre: ({ children }) => (
      <div className={`group relative ${blockSpace}`}>
        <pre className="overflow-x-auto">{children}</pre>
        <div className="absolute right-1 top-1 opacity-0 transition-opacity focus-within:opacity-100 group-hover:opacity-100">
          <CopyButton
            text={nodeText(children).replace(/\n$/, '')}
            size={12}
            className="rounded bg-surface/80 p-1 text-on-surface-variant backdrop-blur-sm transition-colors hover:bg-surface-high hover:text-on-surface"
          />
        </div>
      </div>
    ),
    blockquote: ({ children }) => (
      <blockquote className={`${blockSpace} border-l-2 border-outline-variant/50 pl-2 text-on-surface-variant`}>{children}</blockquote>
    ),
    table: ({ children }) => (
      <div className={`${blockSpace} overflow-x-auto`}>
        <table className={`w-full border-collapse ${codeText}`}>{children}</table>
      </div>
    ),
    th: ({ children }) => <th className="border border-outline-variant/30 px-1.5 py-0.5 text-left font-semibold">{children}</th>,
    td: ({ children }) => <td className="border border-outline-variant/30 px-1.5 py-0.5">{children}</td>,
      hr: () => <hr className={`${hrSpace} border-outline-variant/30`} />,
  };
}

// Precomputed once (avoids rebuilding the component map per render).
const componentsSm = makeComponents('sm');
const componentsBase = makeComponents('base');

export function Markdown({ children, size = 'sm' }: Readonly<{ children: string; size?: MarkdownSize }>) {
  return (
    <div className={`${size === 'base' ? 'text-sm' : 'text-xs'} text-on-surface`}>
      <ReactMarkdown
        remarkPlugins={size === 'base' ? [remarkGfm, remarkBreaks] : [remarkGfm]}
        rehypePlugins={[[rehypeHighlight, { languages: highlightLanguages }]]}
        components={size === 'base' ? componentsBase : componentsSm}
      >
        {children}
      </ReactMarkdown>
    </div>
  );
}

export default Markdown;
