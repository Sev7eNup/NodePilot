import {
  Add,
  Checkbox,
  Checkmark,
  ChevronDown,
  CircleDash,
  Close,
  Download,
  Edit,
  History,
  MagicWand,
  MagicWandFilled,
  Renew,
  Reset,
  Send,
  Subtract,
  Tools,
  TrashCan,
  WarningAltFilled,
} from '@carbon/icons-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Edge, Node } from '@xyflow/react';
import { aiApi, type AiChatTurn, type ChatDoneMeta, type WorkflowChatProposal, type ChatActivityEntry } from '../../api/ai';
import { hashDefinition, buildChangelog, assembleSelectiveDefinition, type WorkflowDefinition, type ChangelogEntry } from '../../lib/workflowDiff';
import { Markdown } from '../common/Markdown';
import { CopyButton } from '../common/CopyButton';
import { useAiChatStore, aiChatScopeKey, aiChatFullKey, type ChatMessage, type ChatThreadMeta } from '../../stores/aiChatStore';
import { useAuthStore } from '../../stores/authStore';
import { buildChatMarkdown, downloadTextFile } from '../../lib/chatExport';

const EMPTY_THREAD: ChatMessage[] = [];
const EMPTY_THREADS: ChatThreadMeta[] = [];
const SUGGESTION_KEYS = ['suggestion1', 'suggestion2', 'suggestion3', 'suggestion4'] as const;
// The backend caps history at 20 turns / 50k characters (AiChatController) → trim hard here,
// otherwise long threads get a 400 HISTORY_TOO_LONG response.
const MAX_HISTORY_TURNS = 19;
const MAX_HISTORY_CHARS = 48_000;

function isAbort(err: unknown): boolean {
  return (err instanceof DOMException || err instanceof Error) && err.name === 'AbortError';
}

/** Trims the history sent to the backend down to its caps (most recent turns, ≤ character limit). */
function trimHistory(history: AiChatTurn[]): AiChatTurn[] {
  let turns = history.slice(-MAX_HISTORY_TURNS);
  let total = turns.reduce((s, m) => s + m.content.length, 0);
  while (turns.length > 0 && total > MAX_HISTORY_CHARS) {
    total -= turns[0].content.length;
    turns = turns.slice(1);
  }
  return turns;
}

/** Appends text to the last assistant message (immutably). */
function appendToLastAssistant(prev: ChatMessage[], text: string): ChatMessage[] {
  const next = prev.slice();
  for (let i = next.length - 1; i >= 0; i--) {
    if (next[i].role === 'assistant') { next[i] = { ...next[i], content: next[i].content + text }; break; }
  }
  return next;
}

/** Patches the last assistant message (building/proposal/meta) immutably. */
function patchLastAssistant(prev: ChatMessage[], patch: Partial<ChatMessage>): ChatMessage[] {
  const next = prev.slice();
  for (let i = next.length - 1; i >= 0; i--) {
    if (next[i].role === 'assistant') { next[i] = { ...next[i], ...patch }; break; }
  }
  return next;
}

/** Appends an in-progress tool call to the last assistant message. */
function addToolCallToLast(prev: ChatMessage[], toolId: string, toolName: string): ChatMessage[] {
  const next = prev.slice();
  for (let i = next.length - 1; i >= 0; i--) {
    if (next[i].role === 'assistant') {
      next[i] = { ...next[i], toolCalls: [...(next[i].toolCalls ?? []), { toolId, toolName, done: false }] };
      break;
    }
  }
  return next;
}

/** Marks a tool call on the last assistant message as completed. */
function markToolDoneOnLast(prev: ChatMessage[], toolId: string): ChatMessage[] {
  const next = prev.slice();
  for (let i = next.length - 1; i >= 0; i--) {
    if (next[i].role === 'assistant') {
      next[i] = { ...next[i], toolCalls: (next[i].toolCalls ?? []).map((tc) => (tc.toolId === toolId ? { ...tc, done: true } : tc)) };
      break;
    }
  }
  return next;
}

/** Marks all assistant messages as done (streaming/building=false). */
function finalizeStreaming(prev: ChatMessage[]): ChatMessage[] {
  return prev.map((m) => (m.streaming || m.building ? { ...m, streaming: false, building: false } : m));
}

interface Props {
  workflowId: string | undefined;
  /** Returns the current canvas definition with runtime-only fields stripped. */
  getCurrentDefinition: () => WorkflowDefinition;
  /** Applies a definition to the canvas (updates history, dirty flag, and setNodes/setEdges). */
  applyDefinition: (def: { nodes: Node[]; edges: Edge[] }) => void;
  /** roleCanWrite && isLockedByMe — the "Apply" action is only enabled when this is true. */
  canApply: boolean;
  /** Viewers can see the chat but are not allowed to apply proposals. */
  isViewer: boolean;
  onClose: () => void;
  /** Editor undo — powers the "Undo" button on an applied proposal card. */
  onUndo?: () => void;
  /** Canvas auto-layout — powers the "Tidy up layout" action after applying a proposal. */
  onAutoLayout?: () => void;
  /** Current canvas selection (labels/count) — used to scope the question to the selected nodes. */
  selection?: { nodeLabels: string[]; edgeCount: number };
}

/**
 * Docked AI workflow assistant (right-hand panel). Multi-turn chat about the current workflow:
 * explains it (rendered as Markdown) and, on request, proposes full definition rewrites.
 * History is kept in memory per user+workflow ([aiChatStore]). Features: copy, regenerate/retry,
 * starter suggestions, @node mentions, usage footer, scroll-to-bottom, Esc closes, Up-arrow
 * recalls the last question.
 */
export function AiWorkflowChatPanel({
  workflowId, getCurrentDefinition, applyDefinition, canApply, isViewer, onClose, onUndo, onAutoLayout, selection,
}: Readonly<Props>) {
  const { t } = useTranslation(['ai', 'common']);
  const userId = useAuthStore((s) => s.userId);
  const scope = aiChatScopeKey(userId, workflowId);
  const threads = useAiChatStore((s) => s.threadsByScope[scope] ?? EMPTY_THREADS);
  const activeThreadId = useAiChatStore((s) => s.activeThreadByScope[scope]);
  const ensureActiveThread = useAiChatStore((s) => s.ensureActiveThread);
  const createThread = useAiChatStore((s) => s.newThread);
  const renameThread = useAiChatStore((s) => s.renameThread);
  const removeThread = useAiChatStore((s) => s.deleteThread);
  const setActiveThread = useAiChatStore((s) => s.setActiveThread);
  const updateMessages = useAiChatStore((s) => s.updateMessages);

  // Ensure there's an active thread (in an effect, not during render) — e.g. after switching workflow/user.
  useEffect(() => {
    ensureActiveThread(scope, t('ai:chat.threadDefault', { n: 1 }));
  }, [scope, ensureActiveThread, t]);

  const threadId = activeThreadId ?? '';
  const fullKey = threadId ? aiChatFullKey(scope, threadId) : '';
  const messages = useAiChatStore((s) => (fullKey ? (s.messagesByThread[fullKey] ?? EMPTY_THREAD) : EMPTY_THREAD));
  const setMessages = useCallback(
    (updater: ChatMessage[] | ((prev: ChatMessage[]) => ChatMessage[])) => {
      if (!threadId) return;
      updateMessages(scope, threadId, typeof updater === 'function' ? updater : () => updater);
    },
    [updateMessages, scope, threadId],
  );

  const [input, setInput] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [atBottom, setAtBottom] = useState(true);
  const [mention, setMention] = useState<{ query: string; index: number } | null>(null);
  // "Refine": the LLM iterates on the previous proposal (def), while the staleness/diff base stays the real canvas (applyBase).
  const [refineTarget, setRefineTarget] = useState<{ def: WorkflowDefinition; applyBase: WorkflowDefinition } | null>(null);
  // "Selection" scoping: relate the question to the current canvas selection.
  const [scopeToSelection, setScopeToSelection] = useState(false);
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const atBottomRef = useRef(true);

  // Abort an in-progress stream on unmount (panel closes / workflow switches).
  useEffect(() => () => abortRef.current?.abort(), []);

  // Only auto-scroll when the user is already at the bottom — don't yank them down while they're scrolling up.
  useEffect(() => {
    const el = scrollRef.current;
    if (el && atBottomRef.current && typeof el.scrollTo === 'function') {
      el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
    }
  }, [messages, sending]);

  // Auto-grow the composer textarea with its content (capped), reset after send.
  useEffect(() => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 168)}px`;
  }, [input]);

  const onScroll = useCallback((e: React.UIEvent<HTMLDivElement>) => {
    const el = e.currentTarget;
    const bottom = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
    atBottomRef.current = bottom;
    setAtBottom(bottom);
  }, []);

  const scrollToBottom = useCallback(() => {
    const el = scrollRef.current;
    if (el && typeof el.scrollTo === 'function') el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
    atBottomRef.current = true;
    setAtBottom(true);
  }, []);

  // Core: appends a streaming assistant bubble and drives the stream. Remembers the last
  // attempt for regenerate/retry. Expects the user message to already be appended.
  const streamAssistant = useCallback(
    // `llmBase` = what the LLM sees as its base (the previous proposal, when refining); `applyBase` =
    // the real canvas (used for the staleness guard + diff base). For normal turns both are the same.
    async (question: string, history: AiChatTurn[], llmBase: WorkflowDefinition, applyBase: WorkflowDefinition = llmBase) => {
      setError(null);
      const baseDefinitionHash = hashDefinition(applyBase);
      setMessages((prev) => [...prev, { role: 'assistant', content: '', streaming: true }]);
      setSending(true);
      atBottomRef.current = true;
      setAtBottom(true);
      const ac = new AbortController();
      abortRef.current = ac;
      try {
        await aiApi.chatStream(
          { question, workflowJson: JSON.stringify(llmBase), workflowId: workflowId ?? null, baseDefinitionHash, history: trimHistory(history) },
          {
            signal: ac.signal,
            onDelta: (text) => setMessages((prev) => appendToLastAssistant(prev, text)),
            onBuilding: () => setMessages((prev) => patchLastAssistant(prev, { building: true })),
            onToolCall: (toolName, toolId) => setMessages((prev) => addToolCallToLast(prev, toolId, toolName)),
            onToolResult: (_toolName, toolId) => setMessages((prev) => markToolDoneOnLast(prev, toolId)),
            onProposal: (proposal) => setMessages((prev) => patchLastAssistant(prev, { proposal, baseDef: applyBase, building: false })),
            onDone: (meta) => setMessages((prev) => patchLastAssistant(prev, { meta })),
          },
        );
      } catch (err: unknown) {
        // User-initiated stop (AbortError): the partial bubble stays, no error is shown.
        if (!isAbort(err)) setError(t('ai:chat.errorPrefix', { message: err instanceof Error ? err.message : String(err) }));
      } finally {
        setMessages(finalizeStreaming);
        setSending(false);
        abortRef.current = null;
      }
    },
    [workflowId, setMessages, t],
  );

  const sendQuestion = useCallback(
    (raw: string) => {
      const typed = raw.trim();
      if (!typed || sending) return;
      // Selection scoping: prefix the focused node names for the LLM (the chat bubble still shows only what the user typed).
      const scoped = scopeToSelection && selection && selection.nodeLabels.length > 0
        ? `[Bezogen auf die ausgewählten Nodes: ${selection.nodeLabels.join(', ')}] ${typed}`
        : typed;
      const history: AiChatTurn[] = messages.map((m) => ({ role: m.role, content: m.content }));
      setMessages((prev) => [...prev, { role: 'user', content: typed }]);
      setInput('');
      setMention(null);
      const refine = refineTarget;
      setRefineTarget(null);
      setScopeToSelection(false);
      if (refine) void streamAssistant(scoped, history, refine.def, refine.applyBase);
      else void streamAssistant(scoped, history, getCurrentDefinition());
    },
    [sending, messages, getCurrentDefinition, setMessages, streamAssistant, refineTarget, scopeToSelection, selection],
  );

  const startRefine = useCallback((def: WorkflowDefinition, applyBase: WorkflowDefinition) => {
    setRefineTarget({ def, applyBase });
    requestAnimationFrame(() => textareaRef.current?.focus());
  }, []);

  // Regenerate / retry: re-answer the last user turn (discarding the old assistant answer).
  const regenerate = useCallback(() => {
    if (sending) return;
    let lastUserIdx = -1;
    for (let i = messages.length - 1; i >= 0; i--) { if (messages[i].role === 'user') { lastUserIdx = i; break; } }
    if (lastUserIdx < 0) return;
    const question = messages[lastUserIdx].content;
    const history: AiChatTurn[] = messages.slice(0, lastUserIdx).map((m) => ({ role: m.role, content: m.content }));
    setMessages((prev) => prev.slice(0, lastUserIdx + 1)); // cut off everything after the last question
    void streamAssistant(question, history, getCurrentDefinition());
  }, [sending, messages, setMessages, streamAssistant, getCurrentDefinition]);

  const handleStop = useCallback(() => abortRef.current?.abort(), []);

  const nodeOptions = useMemo(() => {
    if (mention === null) return [];
    const q = mention.query.toLowerCase();
    return getCurrentDefinition().nodes
      .map((n) => ({ id: n.id, label: (n.data?.label as string) || n.id }))
      .filter((n) => n.label.toLowerCase().includes(q))
      .slice(0, 8);
  }, [mention, getCurrentDefinition]);

  const syncMention = useCallback((value: string, caret: number) => {
    const m = /@([\w-]*)$/.exec(value.slice(0, caret));
    setMention(m ? { query: m[1], index: 0 } : null);
  }, []);

  const insertMention = useCallback((label: string) => {
    const el = textareaRef.current;
    const caret = el?.selectionStart ?? input.length;
    const before = input.slice(0, caret);
    const m = /@([\w-]*)$/.exec(before);
    if (!m) return;
    const start = caret - m[0].length;
    const next = `${input.slice(0, start)}@\`${label}\` ${input.slice(caret)}`;
    setInput(next);
    setMention(null);
    requestAnimationFrame(() => {
      const pos = start + label.length + 4; // @`label` + space
      el?.focus();
      el?.setSelectionRange(pos, pos);
    });
  }, [input]);

  const handleChange = useCallback((e: React.ChangeEvent<HTMLTextAreaElement>) => {
    setInput(e.target.value);
    syncMention(e.target.value, e.target.selectionStart ?? e.target.value.length);
  }, [syncMention]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    // The @-mention popup takes keyboard priority.
    if (mention !== null && nodeOptions.length > 0) {
      if (e.key === 'ArrowDown') { e.preventDefault(); setMention((p) => p && { ...p, index: (p.index + 1) % nodeOptions.length }); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); setMention((p) => p && { ...p, index: (p.index - 1 + nodeOptions.length) % nodeOptions.length }); return; }
      if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); insertMention(nodeOptions[mention.index].label); return; }
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); setMention(null); return; }
    }
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); sendQuestion(input); return; }
    // Up-arrow in an empty composer: recall the last question typed (like shell history).
    if (e.key === 'ArrowUp' && input.length === 0) {
      const lastUser = [...messages].reverse().find((m) => m.role === 'user');
      if (lastUser) { e.preventDefault(); setInput(lastUser.content); }
    }
  }, [mention, nodeOptions, insertMention, sendQuestion, input, messages]);

  const handlePanelKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (e.key === 'Escape') onClose();
  }, [onClose]);

  const exportThread = useCallback(() => {
    if (messages.length === 0) return;
    const title = threads.find((th) => th.id === threadId)?.name ?? t('ai:chat.threadDefault', { n: 1 });
    const md = buildChatMarkdown(title, messages, {
      user: t('ai:chat.exportUser'),
      assistant: t('ai:chat.exportAssistant'),
      proposal: t('ai:chat.proposalTitle'),
    });
    const slug = title.replace(/[^\w.-]+/g, '-').replace(/^-+|-+$/g, '').toLowerCase() || 'chat';
    const date = new Date().toISOString().slice(0, 10);
    downloadTextFile(`ai-chat-${slug}-${date}.md`, md);
  }, [messages, threads, threadId, t]);

  // Best-effort audit trail entry that a proposal was applied (only for already-saved workflows).
  const handleApplied = useCallback((nodeCount: number, edgeCount: number) => {
    if (!workflowId) return;
    void aiApi.chatApplied({ workflowId, nodeCount, edgeCount }).catch(() => { /* audit logging is best-effort */ });
  }, [workflowId]);

  const lastIndex = messages.length - 1;

  return (
    <aside className="flex h-full w-full flex-col bg-surface-low" aria-label={t('ai:chat.title')} onKeyDown={handlePanelKeyDown}>
      {/* Header */}
      <div className="flex items-center justify-between gap-2 border-b border-outline-variant/20 bg-surface-low px-3 py-2">
        <div className="flex min-w-0 items-center gap-1.5">
          <MagicWandFilled size={15} className="shrink-0 text-primary" />
          <ThreadMenu
            threads={threads}
            activeId={threadId}
            disabled={sending}
            onSelect={(id) => { setActiveThread(scope, id); setError(null); }}
            onNew={() => { createThread(scope, t('ai:chat.threadDefault', { n: threads.length + 1 })); setError(null); }}
            onRename={(id, name) => renameThread(scope, id, name)}
            onDelete={(id) => { removeThread(scope, id); setError(null); }}
          />
        </div>
        <div className="flex shrink-0 items-center gap-1">
          {workflowId && !isViewer && <ActivityMenu workflowId={workflowId} />}
          {messages.length > 0 && (
            <button
              onClick={exportThread}
              className="rounded p-1 text-on-surface-variant transition-colors hover:bg-surface-high hover:text-on-surface"
              title={t('ai:chat.export')}
              aria-label={t('ai:chat.export')}
            >
              <Download size={14} />
            </button>
          )}
          {messages.length > 0 && (
            <button
              onClick={() => { setMessages(() => []); setError(null); }}
              disabled={sending}
              className="rounded p-1 text-on-surface-variant transition-colors hover:bg-surface-high hover:text-on-surface disabled:opacity-40"
              title={t('ai:chat.clear')}
              aria-label={t('ai:chat.clear')}
            >
              <TrashCan size={14} />
            </button>
          )}
          <button
            onClick={onClose}
            className="rounded p-1 text-on-surface-variant transition-colors hover:bg-error-container/30 hover:text-error"
            aria-label={t('common:close')}
          >
            <Close size={14} />
          </button>
        </div>
      </div>
      {/* Messages */}
      <div className="relative min-h-0 flex-1">
        <div ref={scrollRef} onScroll={onScroll} className="absolute inset-0 space-y-4 overflow-y-auto px-3.5 py-4">
          {messages.length === 0 ? (
            <div className="flex h-full flex-col items-center justify-center gap-3 px-6 text-center">
              <div className="rounded-2xl bg-primary-fixed p-3 text-primary shadow-sm">
                <MagicWandFilled size={22} />
              </div>
              <p className="max-w-[15rem] text-xs leading-relaxed text-on-surface-variant">{t('ai:chat.intro')}</p>
              <div className="mt-1 flex flex-col gap-1.5">
                {SUGGESTION_KEYS.map((sk) => {
                  const label = t(`ai:chat.${sk}`);
                  return (
                    <button
                      key={sk}
                      onClick={() => sendQuestion(label)}
                      className="rounded-full border border-primary/30 bg-primary-fixed/40 px-3 py-1.5 text-[11px] font-label text-primary transition-colors hover:bg-primary-fixed/70"
                    >
                      {label}
                    </button>
                  );
                })}
              </div>
            </div>
          ) : (
            messages.map((m, i) => (
              <MessageBubble
                key={i}
                message={m}
                isLastAssistant={m.role === 'assistant' && i === lastIndex && !sending}
                onRegenerate={regenerate}
                onRefine={startRefine}
                onUndo={onUndo}
                onAutoLayout={onAutoLayout}
                canApply={canApply}
                isViewer={isViewer}
                getCurrentDefinition={getCurrentDefinition}
                applyDefinition={applyDefinition}
                onApplied={handleApplied}
              />
            ))
          )}
        </div>
        {!atBottom && messages.length > 0 && (
          <button
            onClick={scrollToBottom}
            className="absolute bottom-2 right-3 flex h-7 w-7 items-center justify-center rounded-full border border-outline-variant/40 bg-surface-high text-on-surface-variant shadow-md transition-colors hover:text-on-surface"
            title={t('ai:chat.scrollToBottom')}
            aria-label={t('ai:chat.scrollToBottom')}
          >
            <ChevronDown size={15} />
          </button>
        )}
      </div>
      {/* Error + Retry */}
      {error && (
        <div role="alert" className="mx-3.5 mb-2 flex items-start justify-between gap-2 whitespace-pre-wrap rounded-lg border border-error/30 bg-error-container/20 px-2.5 py-2 text-xs text-on-error-container">
          <span className="min-w-0 flex-1">{error}</span>
          <button
            onClick={() => { setError(null); regenerate(); }}
            className="flex shrink-0 items-center gap-1 rounded px-1.5 py-0.5 font-label font-semibold text-on-error-container transition-colors hover:bg-error-container/40"
          >
            <Reset size={11} /> {t('ai:chat.retry')}
          </button>
        </div>
      )}
      {/* Composer */}
      <div className="relative border-t border-outline-variant/15 bg-surface-low px-3.5 pb-3 pt-2.5">
        {mention !== null && nodeOptions.length > 0 && (
          <div className="absolute bottom-full left-3.5 right-3.5 mb-1 max-h-44 overflow-y-auto rounded-xl border border-outline-variant/40 bg-surface-high py-1 shadow-lg">
            {nodeOptions.map((n, i) => (
              <button
                key={n.id}
                onMouseDown={(e) => { e.preventDefault(); insertMention(n.label); }}
                className={`flex w-full items-center gap-1.5 px-3 py-1.5 text-left text-xs transition-colors ${i === mention.index ? 'bg-primary-fixed text-on-primary-fixed' : 'text-on-surface hover:bg-surface'}`}
              >
                <MagicWandFilled size={11} className="shrink-0 text-primary" />
                <span className="truncate">{n.label}</span>
              </button>
            ))}
          </div>
        )}
        {(refineTarget || (selection && selection.nodeLabels.length > 0)) && (
          <div className="mb-1.5 flex flex-wrap items-center gap-1.5">
            {refineTarget && (
              <span className="flex items-center gap-1 rounded-full bg-primary-fixed px-2 py-0.5 text-[10px] font-label font-semibold text-on-primary-fixed">
                <MagicWand size={10} /> {t('ai:chat.refining')}
                <button onClick={() => setRefineTarget(null)} aria-label={t('common:close')} className="hover:text-on-primary-fixed"><Close size={10} /></button>
              </span>
            )}
            {selection && selection.nodeLabels.length > 0 && (
              <button
                onClick={() => setScopeToSelection((v) => !v)}
                title={t('ai:chat.scopeSelectionHint')}
                className={`flex items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-label font-semibold transition-colors ${scopeToSelection ? 'bg-primary text-on-primary' : 'bg-surface-high text-on-surface-variant hover:text-on-surface'}`}
              >
                {scopeToSelection && <Checkmark size={10} />} {t('ai:chat.scopeSelection', { count: selection.nodeLabels.length })}
              </button>
            )}
          </div>
        )}
        <div className="flex items-end gap-2 rounded-2xl border border-outline-variant/40 bg-surface-high px-3 py-2 transition-colors focus-within:border-primary/50 focus-within:ring-2 focus-within:ring-primary/15">
          <textarea
            ref={textareaRef}
            value={input}
            onChange={handleChange}
            onKeyDown={handleKeyDown}
            placeholder={t('ai:chat.placeholder')}
            rows={1}
            disabled={sending}
            aria-label={t('ai:chat.placeholder')}
            className="max-h-[168px] min-h-[1.5rem] flex-1 resize-none border-0 bg-transparent text-sm leading-relaxed text-on-surface outline-none placeholder:text-on-surface-variant/60"
          />
          {sending ? (
            <button
              onClick={handleStop}
              className="flex h-8 w-8 shrink-0 items-center justify-center rounded-xl bg-error text-on-error shadow-sm transition-all hover:brightness-110 active:scale-95"
              title={t('ai:chat.stop')}
              aria-label={t('ai:chat.stop')}
            >
              <Checkbox size={13} className="fill-current" />
            </button>
          ) : (
            <button
              onClick={() => sendQuestion(input)}
              disabled={input.trim().length === 0}
              className="flex h-8 w-8 shrink-0 items-center justify-center rounded-xl bg-primary text-on-primary shadow-sm transition-all hover:brightness-110 hover:shadow active:scale-95 disabled:cursor-not-allowed disabled:bg-primary/40 disabled:shadow-none"
              title={t('ai:chat.send')}
              aria-label={t('ai:chat.send')}
            >
              <Send size={15} />
            </button>
          )}
        </div>
        <p className="mt-1.5 px-1 text-[10px] text-on-surface-variant/70">{t('ai:chat.enterHint')}</p>
      </div>
    </aside>
  );
}

function MessageBubble({
  message, isLastAssistant, onRegenerate, onRefine, onUndo, onAutoLayout, canApply, isViewer, getCurrentDefinition, applyDefinition, onApplied,
}: Readonly<{
  message: ChatMessage;
  isLastAssistant: boolean;
  onRegenerate: () => void;
  onRefine: (def: WorkflowDefinition, applyBase: WorkflowDefinition) => void;
  onUndo?: () => void;
  onAutoLayout?: () => void;
  canApply: boolean;
  isViewer: boolean;
  getCurrentDefinition: () => WorkflowDefinition;
  applyDefinition: (def: { nodes: Node[]; edges: Edge[] }) => void;
  onApplied?: (nodeCount: number, edgeCount: number) => void;
}>) {
  const { t } = useTranslation(['ai']);

  // Diff base for the ProposalCard: the live snapshot (baseDef) only exists in the
  // session where the question was asked. After a reload it's gone — so the current
  // canvas is reconstructed as the base, as long as its hash still matches the
  // proposal's baseDefinitionHash (the apply path merges against the current canvas
  // anyway and uses the same hash-based staleness guard). If the canvas has moved on,
  // or the definition JSON was dropped for exceeding the persistence cap, all that's
  // left is the "expired" notice.
  // useMemo: hashing the whole canvas is too expensive to redo on every render of every
  // bubble. A little staleness is fine — clicking Apply re-checks the hash (staleness
  // guard), so a stale memoized base only affects what's displayed. (Must run before the
  // early return for user messages below: hooks must run unconditionally.)
  const effectiveBase = useMemo(() => {
    if (message.baseDef) return message.baseDef;
    if (message.proposal && message.proposal.definitionJson.length > 0) {
      const liveCanvas = getCurrentDefinition();
      if (hashDefinition(liveCanvas) === message.proposal.baseDefinitionHash) {
        return liveCanvas;
      }
    }
    return undefined;
  }, [message.baseDef, message.proposal, getCurrentDefinition]);

  if (message.role === 'user') {
    return (
      <div className="flex justify-end">
        <div className="max-w-[85%] whitespace-pre-wrap rounded-2xl rounded-br-md bg-primary px-3 py-2 text-xs leading-relaxed text-on-primary shadow-sm">
          {message.content}
        </div>
      </div>
    );
  }

  const idle = !message.streaming && !message.building;
  const showActions = idle && message.content.length > 0;

  return (
    <div className="group flex items-start gap-2">
      <div className="mt-0.5 flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-primary-fixed text-primary">
        <MagicWandFilled size={13} />
      </div>
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        <div className="rounded-2xl rounded-bl-md border border-outline-variant/25 bg-surface-high px-3 py-2 shadow-sm">
          {message.streaming && message.content.length === 0 ? (
            <span className="flex items-center gap-1.5 text-xs text-on-surface-variant">
              <CircleDash size={13} className="animate-spin" /> {t('ai:chat.sending')}
            </span>
          ) : (
            <div className="relative">
              <Markdown>{message.content}</Markdown>
              {message.streaming && !message.building && (
                <span className="ml-0.5 inline-block h-3.5 w-[2px] -translate-y-px animate-pulse bg-primary align-middle" />
              )}
            </div>
          )}
        </div>

        {/* Read-only tool calls (analyze_workflow, …) — shown for transparency about what the assistant checked */}
        {message.toolCalls && message.toolCalls.length > 0 && (
          <div className="flex flex-col gap-0.5 pl-1">
            {message.toolCalls.map((tc) => (
              <span key={tc.toolId} className="flex items-center gap-1.5 text-xs text-on-surface-variant">
                {tc.done
                  ? <Checkmark size={12} className="text-emerald-600 dark:text-emerald-400" />
                  : <CircleDash size={12} className="animate-spin" />}
                <Tools size={11} className="opacity-60" />
                <code className="font-mono">{tc.toolName}</code>
                <span className="opacity-70">{tc.done ? t('ai:chat.toolDone') : t('ai:chat.toolRunning')}</span>
              </span>
            ))}
          </div>
        )}

        {/* Actions (copy/regenerate) + usage footer */}
        {showActions && (
          <div className="flex items-center gap-1 pl-1 opacity-0 transition-opacity group-hover:opacity-100 focus-within:opacity-100">
            <CopyButton text={message.content} size={12} />
            {isLastAssistant && (
              <button
                onClick={onRegenerate}
                title={t('ai:chat.regenerate')}
                aria-label={t('ai:chat.regenerate')}
                className="rounded p-1 text-on-surface-variant transition-colors hover:bg-surface-high hover:text-on-surface"
              >
                <Renew size={12} />
              </button>
            )}
            {message.meta && <UsageFooter meta={message.meta} />}
          </div>
        )}

        {message.building && !message.proposal && (
          <div className="flex items-center gap-2 rounded-lg border border-primary/30 bg-primary-fixed/40 px-3 py-2 text-xs font-label text-primary">
            <CircleDash size={14} className="animate-spin" /> {t('ai:chat.building')}
          </div>
        )}
        {message.proposal && effectiveBase && (
          <ProposalCard
            proposal={message.proposal}
            baseDef={effectiveBase}
            canApply={canApply}
            isViewer={isViewer}
            getCurrentDefinition={getCurrentDefinition}
            applyDefinition={applyDefinition}
            onApplied={onApplied}
            onRefine={onRefine}
            onUndo={onUndo}
            onAutoLayout={onAutoLayout}
          />
        )}
        {/* Reload case with no reconstructable base (the canvas has moved on, or the
            definition JSON exceeded the persistence cap) → only a read-only notice remains. */}
        {message.proposal && !effectiveBase && (
          <div className="rounded-lg border border-outline-variant/30 bg-surface-high px-3 py-2 text-xs text-on-surface-variant">
            {t('ai:chat.proposalExpired')}
          </div>
        )}
      </div>
    </div>
  );
}

/** Header dropdown: workflow-scoped AI activity log (asked/applied) — loaded lazily on open. */
function ActivityMenu({ workflowId }: Readonly<{ workflowId: string }>) {
  const { t, i18n } = useTranslation(['ai']);
  const [open, setOpen] = useState(false);
  const [entries, setEntries] = useState<ChatActivityEntry[] | null>(null);
  const [loading, setLoading] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as globalThis.Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [open]);

  const toggle = useCallback(async () => {
    const next = !open;
    setOpen(next);
    if (next) {
      setLoading(true);
      try { setEntries(await aiApi.chatActivity(workflowId)); }
      catch { setEntries([]); }
      finally { setLoading(false); }
    }
  }, [open, workflowId]);

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={toggle}
        className="rounded p-1 text-on-surface-variant transition-colors hover:bg-surface-high hover:text-on-surface"
        title={t('ai:chat.activity')}
        aria-label={t('ai:chat.activity')}
      >
        <History size={14} />
      </button>
      {open && (
        <div className="absolute right-0 top-full z-20 mt-1 w-72 rounded-lg border border-outline-variant/30 bg-surface-low p-1 shadow-lg">
          <p className="px-2 py-1 text-[11px] font-label font-semibold uppercase tracking-wide text-on-surface-variant">
            {t('ai:chat.activity')}
          </p>
          <div className="max-h-72 overflow-y-auto">
            {loading && <p className="px-2 py-2 text-xs text-on-surface-variant">{t('ai:chat.activityLoading')}</p>}
            {!loading && entries && entries.length === 0 && (
              <p className="px-2 py-2 text-xs text-on-surface-variant">{t('ai:chat.activityEmpty')}</p>
            )}
            {!loading && entries?.map((e, idx) => (
              <div key={idx} className="flex items-center justify-between gap-2 rounded px-2 py-1 text-xs">
                <span className="min-w-0 truncate">
                  <span className={`font-label font-semibold ${e.action === 'AI_PROPOSAL_APPLIED' ? 'text-emerald-600 dark:text-emerald-400' : 'text-on-surface'}`}>
                    {e.action === 'AI_PROPOSAL_APPLIED' ? t('ai:chat.activityApplied') : t('ai:chat.activityAsked')}
                  </span>
                  {e.username && <span className="text-on-surface-variant"> · {e.username}</span>}
                </span>
                <span className="shrink-0 text-[10px] text-on-surface-variant">{new Date(e.timestamp).toLocaleString(i18n.language)}</span>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

/** Compact thread switcher in the header: active chat name + dropdown (switch/rename/delete/new). */
function ThreadMenu({
  threads, activeId, disabled, onSelect, onNew, onRename, onDelete,
}: Readonly<{
  threads: ChatThreadMeta[];
  activeId: string;
  disabled?: boolean;
  onSelect: (id: string) => void;
  onNew: () => void;
  onRename: (id: string, name: string) => void;
  onDelete: (id: string) => void;
}>) {
  const { t } = useTranslation(['ai']);
  const [open, setOpen] = useState(false);
  const [renaming, setRenaming] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as globalThis.Node)) { setOpen(false); setRenaming(null); }
    };
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [open]);

  const active = threads.find((th) => th.id === activeId);
  const activeName = active?.name ?? t('ai:chat.threadDefault', { n: 1 });

  const commitRename = (id: string) => {
    const name = renameValue.trim();
    if (name) onRename(id, name);
    setRenaming(null);
  };

  return (
    <div ref={ref} className="relative min-w-0">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex min-w-0 items-center gap-1 rounded px-1.5 py-1 text-sm font-headline font-bold text-on-surface hover:bg-surface-high"
        title={t('ai:chat.threads')}
        aria-label={t('ai:chat.threads')}
      >
        <span className="max-w-[11rem] truncate">{activeName}</span>
        <ChevronDown size={13} className="shrink-0 text-on-surface-variant" />
      </button>
      {open && (
        <div className="absolute left-0 top-full z-20 mt-1 w-64 rounded-lg border border-outline-variant/30 bg-surface-low p-1 shadow-lg">
          <div className="max-h-64 overflow-y-auto">
            {threads.length === 0 && (
              <p className="px-2 py-1.5 text-xs text-on-surface-variant">{t('ai:chat.noThreads')}</p>
            )}
            {threads.map((th) => (
              <div
                key={th.id}
                className={`group/th flex items-center gap-1 rounded px-1.5 py-1 ${th.id === activeId ? 'bg-surface-high' : 'hover:bg-surface-high'}`}
              >
                {renaming === th.id ? (
                  <input
                    autoFocus
                    value={renameValue}
                    onChange={(e) => setRenameValue(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') commitRename(th.id);
                      if (e.key === 'Escape') setRenaming(null);
                    }}
                    onBlur={() => commitRename(th.id)}
                    className="min-w-0 flex-1 rounded border border-outline-variant bg-surface-low px-1 py-0.5 text-xs text-on-surface"
                  />
                ) : (
                  <button
                    type="button"
                    disabled={disabled}
                    onClick={() => { onSelect(th.id); setOpen(false); }}
                    className="min-w-0 flex-1 truncate text-left text-xs text-on-surface disabled:opacity-50"
                  >
                    {th.name}
                  </button>
                )}
                <button
                  type="button"
                  disabled={disabled}
                  title={t('ai:chat.renameThread')}
                  aria-label={t('ai:chat.renameThread')}
                  onClick={() => { setRenaming(th.id); setRenameValue(th.name); }}
                  className="rounded p-0.5 text-on-surface-variant opacity-0 transition-opacity hover:text-on-surface group-hover/th:opacity-100 disabled:hover:text-on-surface-variant"
                >
                  <Edit size={12} />
                </button>
                <button
                  type="button"
                  disabled={disabled}
                  title={t('ai:chat.deleteThread')}
                  aria-label={t('ai:chat.deleteThread')}
                  onClick={() => onDelete(th.id)}
                  className="rounded p-0.5 text-on-surface-variant opacity-0 transition-opacity hover:text-error group-hover/th:opacity-100 disabled:hover:text-on-surface-variant"
                >
                  <TrashCan size={12} />
                </button>
              </div>
            ))}
          </div>
          <button
            type="button"
            disabled={disabled}
            onClick={() => { onNew(); setOpen(false); }}
            className="mt-1 flex w-full items-center gap-1.5 rounded px-1.5 py-1.5 text-xs text-primary hover:bg-surface-high disabled:opacity-40"
          >
            <Add size={13} /> {t('ai:chat.newThread')}
          </button>
        </div>
      )}
    </div>
  );
}

function UsageFooter({ meta }: Readonly<{ meta: ChatDoneMeta }>) {
  const { t } = useTranslation(['ai']);
  const tokens = meta.promptTokens != null && meta.completionTokens != null
    ? meta.promptTokens + meta.completionTokens
    : null;
  // Generation throughput: completion tokens per second (end-to-end duration).
  const tpsValue = meta.completionTokens != null && meta.durationMs > 0
    ? meta.completionTokens / (meta.durationMs / 1000)
    : null;
  const tps = tpsValue != null ? (tpsValue < 10 ? tpsValue.toFixed(1) : Math.round(tpsValue).toString()) : null;

  let label: string;
  if (tokens != null && tps != null) {
    label = t('ai:chat.usageTokensTps', { model: meta.model, ms: meta.durationMs, tokens, tps });
  } else if (tokens != null) {
    label = t('ai:chat.usageTokens', { model: meta.model, ms: meta.durationMs, tokens });
  } else {
    label = t('ai:chat.usageNoTokens', { model: meta.model, ms: meta.durationMs });
  }
  return (
    <span className="ml-1 select-none text-[10px] text-on-surface-variant/70" title={t('ai:chat.usageTitle')}>
      {label}
    </span>
  );
}

function ProposalCard({
  proposal, baseDef, canApply, isViewer, getCurrentDefinition, applyDefinition, onApplied, onRefine, onUndo, onAutoLayout,
}: Readonly<{
  proposal: WorkflowChatProposal;
  baseDef: WorkflowDefinition;
  canApply: boolean;
  isViewer: boolean;
  getCurrentDefinition: () => WorkflowDefinition;
  applyDefinition: (def: { nodes: Node[]; edges: Edge[] }) => void;
  onApplied?: (nodeCount: number, edgeCount: number) => void;
  onRefine: (def: WorkflowDefinition, applyBase: WorkflowDefinition) => void;
  onUndo?: () => void;
  onAutoLayout?: () => void;
}>) {
  const { t } = useTranslation(['ai']);
  const [applied, setApplied] = useState(false);
  const [undone, setUndone] = useState(false);
  const [discarded, setDiscarded] = useState(false);
  const [stale, setStale] = useState(false);
  const [droppedEdges, setDroppedEdges] = useState(0);

  const proposedDef = useMemo<WorkflowDefinition | null>(() => {
    try {
      const parsed = JSON.parse(proposal.definitionJson) as WorkflowDefinition;
      return { nodes: parsed.nodes ?? [], edges: parsed.edges ?? [] };
    } catch {
      return null;
    }
  }, [proposal.definitionJson]);

  const changelog = useMemo<ChangelogEntry[]>(
    () => (proposedDef ? buildChangelog(baseDef, proposedDef) : []),
    [baseDef, proposedDef],
  );

  // Default: everything selected except pure layout moves.
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set(changelog.filter((e) => !e.layoutOnly).map((e) => e.id)),
  );
  const toggle = useCallback((id: string) =>
    setSelected((prev) => { const next = new Set(prev); if (next.has(id)) next.delete(id); else next.add(id); return next; }), []);
  const setAll = useCallback((on: boolean) =>
    setSelected(on ? new Set(changelog.map((e) => e.id)) : new Set()), [changelog]);

  const handleApply = useCallback(() => {
    if (!proposedDef || !canApply) return;
    const canvas = getCurrentDefinition();
    if (hashDefinition(canvas) !== proposal.baseDefinitionHash) { setStale(true); return; }
    const { nodes, edges, droppedEdges: dropped } = assembleSelectiveDefinition(canvas, proposedDef, selected);
    applyDefinition({ nodes, edges });
    setDroppedEdges(dropped);
    setApplied(true);
    onApplied?.(nodes.length, edges.length);
  }, [proposedDef, canApply, getCurrentDefinition, proposal.baseDefinitionHash, selected, applyDefinition, onApplied]);

  if (!proposedDef) return null;

  if (discarded) {
    return (
      <div className="flex items-center gap-1.5 rounded-lg border border-outline-variant/25 bg-surface-high px-3 py-1.5 text-[11px] font-label italic text-on-surface-variant">
        <Close size={12} /> {t('ai:chat.discarded')}
      </div>
    );
  }

  const selectedCount = changelog.filter((e) => selected.has(e.id)).length;

  return (
    <div className="rounded-lg border border-primary/30 bg-primary-fixed/40 p-2">
      <div className="mb-1.5 flex items-center justify-between gap-2">
        <span className="flex items-center gap-1.5 text-[10px] font-label font-bold uppercase tracking-wide text-primary">
          <MagicWandFilled size={11} /> {t('ai:chat.proposalTitle')}
        </span>
        {!applied && changelog.length > 0 && canApply && (
          <span className="flex items-center gap-1.5 text-[10px] text-on-surface-variant">
            <button onClick={() => setAll(true)} className="hover:text-on-surface hover:underline">{t('ai:chat.selectAll')}</button>
            <span>·</span>
            <button onClick={() => setAll(false)} className="hover:text-on-surface hover:underline">{t('ai:chat.selectNone')}</button>
          </span>
        )}
      </div>
      {/* Structured changelog with (when the user has write access) checkboxes for selectively applying changes */}
      {changelog.length === 0 ? (
        <div className="px-1 py-1 text-[11px] italic text-on-surface-variant">{t('ai:chat.noChanges')}</div>
      ) : (
        <ul className="space-y-0.5">
          {changelog.map((e) => (
            <li key={`${e.target}:${e.id}`} className="flex items-center gap-1.5 text-[11px] leading-snug">
              {!applied && canApply && (
                <input
                  type="checkbox"
                  checked={selected.has(e.id)}
                  onChange={() => toggle(e.id)}
                  aria-label={`${e.kind} ${e.label}`}
                  className="h-3 w-3 shrink-0 accent-primary"
                />
              )}
              <ChangeIcon kind={e.kind} />
              <span className="truncate text-on-surface">{e.label}</span>
              {e.activityType && <span className="shrink-0 text-[10px] text-on-surface-variant">· {e.activityType}</span>}
              {e.layoutOnly && <span className="shrink-0 rounded bg-surface-high px-1 text-[9px] text-on-surface-variant">{t('ai:chat.layoutOnly')}</span>}
            </li>
          ))}
        </ul>
      )}
      {stale && (
        <div role="alert" className="mt-2 flex items-start gap-1.5 rounded border border-amber-300/50 bg-amber-50 px-2 py-1 text-[11px] text-amber-800 dark:border-amber-800/50 dark:bg-amber-950/40 dark:text-amber-300">
          <WarningAltFilled size={12} className="mt-px shrink-0" /> {t('ai:chat.staleProposal')}
        </div>
      )}
      {applied && droppedEdges > 0 && (
        <div className="mt-2 flex items-start gap-1.5 rounded border border-amber-300/40 bg-amber-50/60 px-2 py-1 text-[11px] text-amber-800 dark:border-amber-800/40 dark:bg-amber-950/30 dark:text-amber-300">
          <WarningAltFilled size={12} className="mt-px shrink-0" /> {t('ai:chat.edgesSkipped', { count: droppedEdges })}
        </div>
      )}
      <div className="mt-2 flex items-center justify-end gap-2">
        {applied ? (
          <>
            <span className="mr-auto flex items-center gap-1 text-[11px] font-label font-semibold text-green-700 dark:text-green-400">
              <Checkmark size={13} /> {undone ? t('ai:chat.undone') : t('ai:chat.applied')}
            </span>
            {!undone && onUndo && (
              <button onClick={() => { onUndo(); setUndone(true); }} className="flex items-center gap-1 rounded-md px-2 py-1 text-[11px] font-label font-semibold text-on-surface-variant transition-colors hover:bg-surface-high hover:text-on-surface">
                <Reset size={12} /> {t('ai:chat.undo')}
              </button>
            )}
            {!undone && onAutoLayout && (
              <button onClick={onAutoLayout} className="flex items-center gap-1 rounded-md px-2 py-1 text-[11px] font-label font-semibold text-on-surface-variant transition-colors hover:bg-surface-high hover:text-on-surface">
                <MagicWand size={12} /> {t('ai:chat.autoLayout')}
              </button>
            )}
          </>
        ) : (
          <>
            <button
              onClick={() => onRefine(proposedDef, baseDef)}
              title={t('ai:chat.refineHint')}
              className="mr-auto flex items-center gap-1 rounded-md px-2 py-1 text-[11px] font-label font-semibold text-on-surface-variant transition-colors hover:bg-surface-high hover:text-on-surface"
            >
              <MagicWand size={12} /> {t('ai:chat.refine')}
            </button>
            <button
              onClick={() => setDiscarded(true)}
              className="flex items-center gap-1 rounded-md px-2.5 py-1 text-[11px] font-label font-semibold text-on-surface-variant transition-colors hover:bg-surface-high hover:text-on-surface"
            >
              <Close size={12} /> {t('ai:chat.discard')}
            </button>
            {canApply ? (
              <button
                onClick={handleApply}
                disabled={stale || selectedCount === 0}
                className="flex items-center gap-1.5 rounded-md bg-primary px-3 py-1 text-[11px] font-label font-semibold text-on-primary shadow-sm transition-all hover:brightness-110 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Checkmark size={12} /> {selectedCount < changelog.length ? t('ai:chat.applySelected', { count: selectedCount }) : t('ai:chat.apply')}
              </button>
            ) : (
              <span className="text-[11px] italic text-on-surface-variant">
                {isViewer ? t('ai:chat.viewerCannotApply') : t('ai:chat.lockRequiredToApply')}
              </span>
            )}
          </>
        )}
      </div>
    </div>
  );
}

function ChangeIcon({ kind }: Readonly<{ kind: ChangelogEntry['kind'] }>) {
  if (kind === 'add') return <Add size={11} className="shrink-0 text-green-600 dark:text-green-400" />;
  if (kind === 'remove') return <Subtract size={11} className="shrink-0 text-red-600 dark:text-red-400" />;
  return <Edit size={11} className="shrink-0 text-amber-600 dark:text-amber-400" />;
}

export default AiWorkflowChatPanel;
