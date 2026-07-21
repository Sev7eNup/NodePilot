import { describe, it, expect } from 'vitest';
import { buildChatMarkdown } from '../../lib/chatExport';
import type { ChatMessage } from '../../stores/aiChatStore';

const labels = { user: 'Question', assistant: 'Assistant', proposal: 'Proposal' };

describe('buildChatMarkdown', () => {
  it('renders a title + alternating question/answer headings', () => {
    const msgs: ChatMessage[] = [
      { role: 'user', content: 'What does this do?' },
      { role: 'assistant', content: 'It runs daily.' },
    ];
    const md = buildChatMarkdown('Chat 1', msgs, labels);
    expect(md).toContain('# Chat 1');
    expect(md).toContain('## Question\n\nWhat does this do?');
    expect(md).toContain('## Assistant\n\nIt runs daily.');
  });

  it('appends a proposal summary line when a turn carries a proposal', () => {
    const msgs: ChatMessage[] = [
      { role: 'assistant', content: 'Added a step.', proposal: { definitionJson: '', summary: 'Add log step', nodeCount: 2, edgeCount: 1, baseDefinitionHash: 'h' } },
    ];
    expect(buildChatMarkdown('t', msgs, labels)).toContain('> **Proposal:** Add log step');
  });

  it('falls back to node/edge counts when the proposal summary is empty', () => {
    const msgs: ChatMessage[] = [
      { role: 'assistant', content: 'x', proposal: { definitionJson: '', summary: '', nodeCount: 3, edgeCount: 2, baseDefinitionHash: 'h' } },
    ];
    expect(buildChatMarkdown('t', msgs, labels)).toContain('> **Proposal:** 3 nodes, 2 edges');
  });

  it('ends with exactly one trailing newline', () => {
    const md = buildChatMarkdown('t', [{ role: 'user', content: 'hi' }], labels);
    expect(md.endsWith('\n')).toBe(true);
    expect(md.endsWith('\n\n')).toBe(false);
  });
});
