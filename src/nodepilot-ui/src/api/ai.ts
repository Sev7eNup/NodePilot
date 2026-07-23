import { api, postEventStream } from './client';

/**
 * Frontend mirror of the backend's `UpstreamVariableDto` records. Deliberately uses the
 * same property names as the `UpstreamVariable` shape in `lib/upstreamVariables.ts`, so
 * callers can pass the list straight through without remapping it.
 */
export interface AiUpstreamVariable {
  stepId: string;
  label: string;
  variable: string;
  expression: string;
  type: string;
}

export interface GenerateScriptRequest {
  prompt: string;
  workflowId?: string | null;
  stepId?: string | null;
  upstreamVariables: AiUpstreamVariable[];
  /** Current editor content — the basis for "refactor/fix this script" requests (without
   *  it, the LLM would have to guess/hallucinate the existing script). */
  currentScript?: string | null;
}

export interface GenerateWorkflowRequest {
  prompt: string;
}

export interface GenerateWorkflowResponse {
  definitionJson: string;
  suggestedName: string;
  suggestedDescription: string | null;
  nodeCount: number;
  edgeCount: number;
  retried: boolean;
  durationMs: number;
  model: string;
}

/**
 * Frontend-side hard cap. The backend enforces the same cap again for safety, but an
 * oversized request would still waste bandwidth and LLM tokens getting there.
 */
export const MAX_UPSTREAM_VARIABLES = 30;

/** One previous chat message in the workflow assistant conversation. */
export interface AiChatTurn {
  role: 'user' | 'assistant';
  content: string;
}

export interface WorkflowChatRequest {
  question: string;
  /** Current canvas definition (cleaned via stripRuntimeDefinition), as a JSON string. */
  workflowJson: string;
  workflowId?: string | null;
  /** SHA-256 of that same canvas state — used to detect and reject a stale proposal
   *  when the user tries to apply it (the canvas may have changed in the meantime). */
  baseDefinitionHash: string;
  history: AiChatTurn[];
}

export interface WorkflowChatProposal {
  definitionJson: string;
  summary: string;
  nodeCount: number;
  edgeCount: number;
  baseDefinitionHash: string;
}

// ---- SSE streaming -------------------------------------------------------------------

/**
 * Reads a Server-Sent-Events stream and calls `onEvent(eventName, dataJson)` for each event.
 * Handles the format robustly: splits frames on blank lines, joins multiple `data:` lines
 * with `\n`, ignores `:`-comment lines, and normalizes `\r\n`. Re-throws `AbortError` as-is
 * so callers can detect a user-initiated stop.
 */
async function readEventStream(response: Response, onEvent: (event: string, data: string) => void): Promise<void> {
  const body = response.body;
  if (!body) return;
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  const dispatch = (frame: string) => {
    let event = 'message';
    const dataLines: string[] = [];
    for (const raw of frame.split('\n')) {
      const line = raw.endsWith('\r') ? raw.slice(0, -1) : raw;
      if (line.length === 0 || line.startsWith(':')) continue;
      if (line.startsWith('event:')) event = line.slice(6).trim();
      else if (line.startsWith('data:')) dataLines.push(line.slice(5).replace(/^ /, ''));
    }
    if (dataLines.length > 0) onEvent(event, dataLines.join('\n'));
  };

  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let sep: number;
      while ((sep = buffer.indexOf('\n\n')) >= 0) {
        dispatch(buffer.slice(0, sep));
        buffer = buffer.slice(sep + 2);
      }
    }
    if (buffer.trim().length > 0) dispatch(buffer);
  } finally {
    reader.releaseLock();
  }
}

function sseError(data: string): Error {
  try {
    const e = JSON.parse(data) as { code?: string; message?: string };
    return new Error(e.message || e.code || 'AI error');
  } catch {
    return new Error('AI error');
  }
}

/** Completion metadata for a chat turn (model, duration, token usage) — shown in the usage footer. */
export interface ChatDoneMeta {
  model: string;
  durationMs: number;
  promptTokens?: number | null;
  completionTokens?: number | null;
}

export interface ChatStreamHandlers {
  onDelta: (text: string) => void;
  onProposal: (proposal: WorkflowChatProposal) => void;
  /** Signals that the prose reply is done and the workflow definition is now being
   *  generated (the client shows a "building change..." indicator). */
  onBuilding?: () => void;
  /** The assistant is calling a read-only tool (e.g. `analyze_workflow`) — the client
   *  shows a "calling tool X..." indicator. */
  onToolCall?: (toolName: string, toolId: string) => void;
  /** The tool result has arrived — the client can close out the indicator. */
  onToolResult?: (toolName: string, toolId: string) => void;
  /** Stream completion: model + duration + token usage (sent last, right before the stream ends). */
  onDone?: (meta: ChatDoneMeta) => void;
  signal?: AbortSignal;
}

/** Streams one chat turn: `onDelta` per prose token, `onBuilding` when it switches to
 *  generating the workflow definition, `onProposal` at the end, `onDone` with metadata. */
export async function chatStream(req: WorkflowChatRequest, h: ChatStreamHandlers): Promise<void> {
  const resp = await postEventStream('/ai/chat', req, h.signal);
  await readEventStream(resp, (event, data) => {
    if (event === 'delta') h.onDelta((JSON.parse(data) as { text: string }).text);
    else if (event === 'building') h.onBuilding?.();
    else if (event === 'tool_call') {
      const t = JSON.parse(data) as { toolName: string; toolId: string };
      h.onToolCall?.(t.toolName, t.toolId);
    } else if (event === 'tool_result') {
      const t = JSON.parse(data) as { toolName: string; toolId: string };
      h.onToolResult?.(t.toolName, t.toolId);
    } else if (event === 'proposal') h.onProposal(JSON.parse(data) as WorkflowChatProposal);
    else if (event === 'done') h.onDone?.(JSON.parse(data) as ChatDoneMeta);
    else if (event === 'error') throw sseError(data);
  });
}

// ---- Global "AI Chat" knowledge assistant --------------------------------------------

export interface KnowledgeAskRequest {
  question: string;
  history: AiChatTurn[];
  /** Caller's IANA time zone (e.g. `Europe/Berlin`) so the assistant can anchor "now" and render
   *  times in the user's local zone. */
  timeZone: string;
  /** Current UTC offset of that zone, in minutes (positive = ahead of UTC). Fallback when the
   *  backend can't resolve the IANA id. */
  utcOffsetMinutes: number;
}

/** Effective knowledge-chat capabilities for the current user (drives nav visibility + source badges). */
export interface KnowledgeCapabilities {
  enabled: boolean;
  docs: boolean;
  operational: boolean;
  sourceCode: boolean;
  db: boolean;
}

/** Handlers for the read-only knowledge stream — deliberately leaner than {@link ChatStreamHandlers}
 *  (no `building`/`proposal`: the knowledge chat never proposes canvas changes). */
export interface KnowledgeStreamHandlers {
  onDelta: (text: string) => void;
  onToolCall?: (toolName: string, toolId: string) => void;
  onToolResult?: (toolName: string, toolId: string) => void;
  onDone?: (meta: ChatDoneMeta) => void;
  signal?: AbortSignal;
}

/** Streams one knowledge-chat turn: `onDelta` per token, tool-call indicators, `onDone` with metadata. */
export async function askStream(req: KnowledgeAskRequest, h: KnowledgeStreamHandlers): Promise<void> {
  const resp = await postEventStream('/ai/knowledge/ask', req, h.signal);
  await readEventStream(resp, (event, data) => {
    if (event === 'delta') h.onDelta((JSON.parse(data) as { text: string }).text);
    else if (event === 'tool_call') {
      const t = JSON.parse(data) as { toolName: string; toolId: string };
      h.onToolCall?.(t.toolName, t.toolId);
    } else if (event === 'tool_result') {
      const t = JSON.parse(data) as { toolName: string; toolId: string };
      h.onToolResult?.(t.toolName, t.toolId);
    } else if (event === 'done') h.onDone?.(JSON.parse(data) as ChatDoneMeta);
    else if (event === 'error') throw sseError(data);
  });
}

/** Which knowledge sources the current user's chat can draw from right now. */
export function getKnowledgeCapabilities(): Promise<KnowledgeCapabilities> {
  return api.get<KnowledgeCapabilities>('/ai/knowledge/capabilities');
}

export interface ScriptStreamHandlers {
  onDelta: (text: string) => void;
  signal?: AbortSignal;
}

/** Streams script generation: `onDelta` per token (with code-fence markers stripped) — used to
 *  make the script appear to type itself live into the Monaco editor. */
export async function generateScriptStream(req: GenerateScriptRequest, h: ScriptStreamHandlers): Promise<void> {
  const resp = await postEventStream('/ai/generate-script', req, h.signal);
  await readEventStream(resp, (event, data) => {
    if (event === 'delta') h.onDelta((JSON.parse(data) as { text: string }).text);
    else if (event === 'error') throw sseError(data);
  });
}

/** One AI audit entry for a workflow (used by the activity view in the chat panel). */
export interface ChatActivityEntry {
  timestamp: string;
  userId: string | null;
  username: string | null;
  action: string;
  details: string | null;
}

/** Tells the backend that an AI proposal was applied (audit action `AI_PROPOSAL_APPLIED`). Fire-and-forget. */
export function chatApplied(req: { workflowId: string; nodeCount: number; edgeCount: number }): Promise<void> {
  return api.post<void>('/ai/chat/applied', req);
}

/** Loads the most recent AI activity (asked/applied) for this workflow. */
export function chatActivity(workflowId: string, take = 20): Promise<ChatActivityEntry[]> {
  return api.get<ChatActivityEntry[]>(`/ai/chat/activity/${workflowId}?take=${take}`);
}

export const aiApi = {
  generateWorkflow: (req: GenerateWorkflowRequest) =>
    api.post<GenerateWorkflowResponse>('/ai/generate-workflow', req),
  chatStream,
  askStream,
  getKnowledgeCapabilities,
  generateScriptStream,
  chatApplied,
  chatActivity,
};
