import type { ChatMessage } from '../stores/aiChatStore';

export interface ChatMarkdownLabels {
  /** Heading for a user turn, e.g. "Frage". */
  user: string;
  /** Heading for an assistant turn, e.g. "Assistent". */
  assistant: string;
  /** Inline label for a proposal summary, e.g. "Vorschlag". */
  proposal: string;
}

/**
 * Builds a Markdown transcript of a chat thread: a heading plus the (trimmed) content per
 * turn, with a short note for any attached workflow proposal. Pure and deterministic —
 * no DOM/Date access — the caller supplies the title/labels (i18n) and file name.
 */
export function buildChatMarkdown(title: string, messages: ChatMessage[], labels: ChatMarkdownLabels): string {
  const lines: string[] = [`# ${title}`, ''];
  for (const m of messages) {
    const content = m.content.trim();
    if (m.role === 'user') {
      lines.push(`## ${labels.user}`, '', content, '');
    } else {
      lines.push(`## ${labels.assistant}`, '', content, '');
      if (m.proposal) {
        const summary = m.proposal.summary || `${m.proposal.nodeCount} nodes, ${m.proposal.edgeCount} edges`;
        lines.push(`> **${labels.proposal}:** ${summary}`, '');
      }
    }
  }
  return `${lines.join('\n').trimEnd()}\n`;
}

/** Triggers a browser download for plain text content (no API round-trip). */
export function downloadTextFile(filename: string, content: string, mime = 'text/markdown'): void {
  const blob = new Blob([content], { type: `${mime};charset=utf-8` });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}
