import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock the low-level client so we can feed a real, chunked SSE ReadableStream into the parser.
const postEventStreamMock = vi.fn();
vi.mock('../../api/client', () => ({
  api: { post: vi.fn() },
  postEventStream: (...args: unknown[]) => postEventStreamMock(...args),
}));

import { chatStream, generateScriptStream, type WorkflowChatProposal } from '../../api/ai';

/** Builds a real Response backed by a chunked ReadableStream — exercises the SSE frame/boundary parsing logic. */
function sseResponse(chunks: string[]): Response {
  const encoder = new TextEncoder();
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const c of chunks) controller.enqueue(encoder.encode(c));
      controller.close();
    },
  });
  return new Response(stream, { status: 200, headers: { 'Content-Type': 'text/event-stream' } });
}

beforeEach(() => postEventStreamMock.mockReset());

describe('chatStream SSE parser', () => {
  it('parses delta + building + proposal frames that are split across chunk boundaries', async () => {
    postEventStreamMock.mockResolvedValue(sseResponse([
      'event: delta\ndata: {"text":"Hel',                         // frame split mid-JSON
      'lo"}\n\nevent: delta\nda',                                  // boundary split mid-field
      'ta: {"text":" world"}\n\n',
      'event: building\ndata: {}\n\n',                             // switch into the "building a proposal" phase
      'event: proposal\ndata: {"definitionJson":"{}","summary":"","nodeCount":0,"edgeCount":0,"baseDefinitionHash":"x"}\n\n',
      'event: done\ndata: {"model":"m","durationMs":1,"promptTokens":12,"completionTokens":7}\n\n',
    ]));

    const deltas: string[] = [];
    let proposal: WorkflowChatProposal | null = null;
    let building = 0;
    let done: { model: string; durationMs: number; promptTokens?: number | null; completionTokens?: number | null } | null = null;
    await chatStream({ question: 'q', workflowJson: '{}', baseDefinitionHash: 'x', history: [] }, {
      onDelta: (t) => deltas.push(t),
      onBuilding: () => { building++; },
      onProposal: (p) => { proposal = p; },
      onDone: (m) => { done = m; },
    });

    expect(deltas.join('')).toBe('Hello world');
    // Incremental delivery: expect multiple onDelta calls rather than a single one.
    expect(deltas.length).toBeGreaterThan(1);
    expect(building).toBe(1);
    expect(proposal).toEqual({ definitionJson: '{}', summary: '', nodeCount: 0, edgeCount: 0, baseDefinitionHash: 'x' });
    expect(done).toEqual({ model: 'm', durationMs: 1, promptTokens: 12, completionTokens: 7 });
  });

  it('parses tool_call + tool_result frames between deltas', async () => {
    postEventStreamMock.mockResolvedValue(sseResponse([
      'event: delta\ndata: {"text":"Pruefe… "}\n\n',
      'event: tool_call\ndata: {"toolName":"analyze_workflow","toolId":"c1"}\n\n',
      'event: tool_result\ndata: {"toolName":"analyze_workflow","toolId":"c1"}\n\n',
      'event: delta\ndata: {"text":"Fertig."}\n\n',
      'event: done\ndata: {"model":"m","durationMs":1}\n\n',
    ]));

    const deltas: string[] = [];
    const calls: string[] = [];
    const results: string[] = [];
    await chatStream({ question: 'q', workflowJson: '{}', baseDefinitionHash: 'x', history: [] }, {
      onDelta: (t) => deltas.push(t),
      onProposal: () => {},
      onToolCall: (name, id) => calls.push(`${name}:${id}`),
      onToolResult: (name, id) => results.push(`${name}:${id}`),
    });

    expect(deltas.join('')).toBe('Pruefe… Fertig.');
    expect(calls).toEqual(['analyze_workflow:c1']);
    expect(results).toEqual(['analyze_workflow:c1']);
  });

  it('throws on an error event', async () => {
    postEventStreamMock.mockResolvedValue(sseResponse(['event: error\ndata: {"code":"LLM_TIMEOUT","message":"too slow"}\n\n']));
    await expect(
      chatStream({ question: 'q', workflowJson: '{}', baseDefinitionHash: 'x', history: [] }, { onDelta: () => {}, onProposal: () => {} }),
    ).rejects.toThrow(/too slow/);
  });

  it('ignores comment lines and a trailing frame without blank line', async () => {
    postEventStreamMock.mockResolvedValue(sseResponse([
      ': keep-alive comment\nevent: delta\ndata: {"text":"a"}\n\n',
      'event: delta\ndata: {"text":"b"}', // no trailing blank line — must still flush
    ]));
    const deltas: string[] = [];
    await chatStream({ question: 'q', workflowJson: '{}', baseDefinitionHash: 'x', history: [] }, {
      onDelta: (t) => deltas.push(t),
      onProposal: () => {},
    });
    expect(deltas.join('')).toBe('ab');
  });
});

describe('generateScriptStream SSE parser', () => {
  it('emits script deltas in order', async () => {
    postEventStreamMock.mockResolvedValue(sseResponse([
      'event: delta\ndata: {"text":"Get-"}\n\n',
      'event: delta\ndata: {"text":"Service"}\n\nevent: done\ndata: {"model":"m","durationMs":1}\n\n',
    ]));
    const out: string[] = [];
    await generateScriptStream({ prompt: 'p', upstreamVariables: [] }, { onDelta: (t) => out.push(t) });
    expect(out.join('')).toBe('Get-Service');
  });

  it('forwards the current script in the request body (refactor base)', async () => {
    postEventStreamMock.mockResolvedValue(sseResponse(['event: done\ndata: {"model":"m","durationMs":1}\n\n']));
    await generateScriptStream(
      { prompt: 'refactor', upstreamVariables: [], currentScript: '$now = Get-Date' },
      { onDelta: () => {} },
    );
    const body = postEventStreamMock.mock.calls[0][1] as { currentScript?: string };
    expect(body.currentScript).toBe('$now = Get-Date');
  });
});
