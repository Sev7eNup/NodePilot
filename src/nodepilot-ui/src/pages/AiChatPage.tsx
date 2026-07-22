import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import {
  Add, BareMetalServer, Chat, Checkbox, Checkmark, ChevronDown, CircleDash, Code, Document,
  Download, Edit, Email, FlowModeler, Renew, Reset, Save, Send, Time, Tools, TrashCan, WarningAlt,
} from '@carbon/icons-react';
import {
  askStream, getKnowledgeCapabilities,
  type AiChatTurn, type ChatDoneMeta, type KnowledgeCapabilities,
} from '../api/ai';
import { Markdown } from '../components/common/Markdown';
import { CopyButton } from '../components/common/CopyButton';
import {
  useAiChatStore, aiChatScopeKey, aiChatFullKey,
  type ChatMessage, type ChatThreadMeta,
} from '../stores/aiChatStore';
import { useAuthStore } from '../stores/authStore';
import { buildChatMarkdown, downloadTextFile } from '../lib/chatExport';

// A non-`__new__` sentinel workflowId so the store's `isPersistableScope` KEEPS this page's
// threads across reloads (unlike an unsaved canvas). One shared scope per user.
const GLOBAL_SCOPE = 'global';
const EMPTY_THREAD: ChatMessage[] = [];
const EMPTY_THREADS: ChatThreadMeta[] = [];
// The backend caps history at 20 turns / 50k chars (AiKnowledgeController) → trim hard here.
const MAX_HISTORY_TURNS = 19;
const MAX_HISTORY_CHARS = 48_000;

function isAbort(err: unknown): boolean {
  return (err instanceof DOMException || err instanceof Error) && err.name === 'AbortError';
}

function trimHistory(history: AiChatTurn[]): AiChatTurn[] {
  let turns = history.slice(-MAX_HISTORY_TURNS);
  let total = turns.reduce((s, m) => s + m.content.length, 0);
  while (turns.length > 0 && total > MAX_HISTORY_CHARS) {
    total -= turns[0].content.length;
    turns = turns.slice(1);
  }
  return turns;
}

function appendToLastAssistant(prev: ChatMessage[], text: string): ChatMessage[] {
  const next = prev.slice();
  for (let i = next.length - 1; i >= 0; i--) {
    if (next[i].role === 'assistant') { next[i] = { ...next[i], content: next[i].content + text }; break; }
  }
  return next;
}

function patchLastAssistant(prev: ChatMessage[], patch: Partial<ChatMessage>): ChatMessage[] {
  const next = prev.slice();
  for (let i = next.length - 1; i >= 0; i--) {
    if (next[i].role === 'assistant') { next[i] = { ...next[i], ...patch }; break; }
  }
  return next;
}

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

function finalizeStreaming(prev: ChatMessage[]): ChatMessage[] {
  return prev.map((m) => (m.streaming ? { ...m, streaming: false } : m));
}

/**
 * Global "AI Chat" — a read-only knowledge & operations assistant over NodePilot's docs,
 * installed workflows/operations, and (when enabled) source code. Feature-parity with the
 * workflow designer's in-canvas assistant (threads, regenerate, copy, usage footer, scroll-to-
 * bottom, streaming cursor) minus the canvas-only bits (proposals/apply/undo/mentions).
 */
export function AiChatPage() {
  const { t } = useTranslation(['ai', 'common']);

  const capsQuery = useQuery({
    queryKey: ['ai-knowledge-capabilities'],
    queryFn: getKnowledgeCapabilities,
    staleTime: 60_000,
  });
  const caps = capsQuery.data;

  const userId = useAuthStore((s) => s.userId);
  const scope = aiChatScopeKey(userId, GLOBAL_SCOPE);
  const threads = useAiChatStore((s) => s.threadsByScope[scope] ?? EMPTY_THREADS);
  const activeThreadId = useAiChatStore((s) => s.activeThreadByScope[scope]);
  const ensureActiveThread = useAiChatStore((s) => s.ensureActiveThread);
  const createThread = useAiChatStore((s) => s.newThread);
  const renameThread = useAiChatStore((s) => s.renameThread);
  const removeThread = useAiChatStore((s) => s.deleteThread);
  const setActiveThread = useAiChatStore((s) => s.setActiveThread);
  const updateMessages = useAiChatStore((s) => s.updateMessages);

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
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const atBottomRef = useRef(true);

  useEffect(() => () => abortRef.current?.abort(), []);

  // Auto-scroll only when already at the bottom (don't yank the user reading history).
  useEffect(() => {
    const el = scrollRef.current;
    if (el && atBottomRef.current && typeof el.scrollTo === 'function') {
      el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
    }
  }, [messages, sending]);

  // Auto-grow the composer with its content (capped at 168px).
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

  const streamAssistant = useCallback(
    async (question: string, history: AiChatTurn[]) => {
      setError(null);
      setMessages((prev) => [...prev, { role: 'assistant', content: '', streaming: true }]);
      setSending(true);
      atBottomRef.current = true;
      setAtBottom(true);
      const ac = new AbortController();
      abortRef.current = ac;
      try {
        await askStream(
          {
            question,
            history: trimHistory(history),
            // Local zone + current offset so the assistant knows "now" and can present times
            // in the user's zone (removes the "14:42 UTC vs 16:42 local" confusion).
            timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
            utcOffsetMinutes: -new Date().getTimezoneOffset(),
          },
          {
            signal: ac.signal,
            onDelta: (text) => setMessages((prev) => appendToLastAssistant(prev, text)),
            onToolCall: (toolName, toolId) => setMessages((prev) => addToolCallToLast(prev, toolId, toolName)),
            onToolResult: (_toolName, toolId) => setMessages((prev) => markToolDoneOnLast(prev, toolId)),
            onDone: (meta) => setMessages((prev) => patchLastAssistant(prev, { meta })),
          },
        );
      } catch (err: unknown) {
        if (!isAbort(err)) setError(t('ai:chat.errorPrefix', { message: err instanceof Error ? err.message : String(err) }));
      } finally {
        setMessages(finalizeStreaming);
        setSending(false);
        abortRef.current = null;
      }
    },
    [setMessages, t],
  );

  const sendQuestion = useCallback(
    (raw: string) => {
      const typed = raw.trim();
      if (!typed || sending) return;
      const history: AiChatTurn[] = messages.map((m) => ({ role: m.role, content: m.content }));
      setMessages((prev) => [...prev, { role: 'user', content: typed }]);
      setInput('');
      void streamAssistant(typed, history);
    },
    [sending, messages, setMessages, streamAssistant],
  );

  // Regenerate / retry: re-answer the last user turn, discarding the old assistant answer.
  const regenerate = useCallback(() => {
    if (sending) return;
    let lastUserIdx = -1;
    for (let i = messages.length - 1; i >= 0; i--) { if (messages[i].role === 'user') { lastUserIdx = i; break; } }
    if (lastUserIdx < 0) return;
    const question = messages[lastUserIdx].content;
    const history: AiChatTurn[] = messages.slice(0, lastUserIdx).map((m) => ({ role: m.role, content: m.content }));
    setMessages((prev) => prev.slice(0, lastUserIdx + 1));
    void streamAssistant(question, history);
  }, [sending, messages, setMessages, streamAssistant]);

  const handleStop = useCallback(() => abortRef.current?.abort(), []);

  const handleKeyDown = useCallback((e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); sendQuestion(input); return; }
    // Up-arrow in an empty composer recalls the last question (shell-history style).
    if (e.key === 'ArrowUp' && input.length === 0) {
      const lastUser = [...messages].reverse().find((m) => m.role === 'user');
      if (lastUser) { e.preventDefault(); setInput(lastUser.content); }
    }
  }, [sendQuestion, input, messages]);

  const exportChat = useCallback(() => {
    if (messages.length === 0) return;
    const title = threads.find((th) => th.id === threadId)?.name ?? t('ai:knowledge.title');
    const md = buildChatMarkdown(title, messages, {
      user: t('ai:knowledge.roleUser'),
      assistant: t('ai:knowledge.roleAssistant'),
      proposal: t('ai:knowledge.roleAssistant'),
    });
    const slug = title.replace(/[^\w.-]+/g, '-').replace(/^-+|-+$/g, '').toLowerCase() || 'chat';
    const date = new Date().toISOString().slice(0, 10);
    downloadTextFile(`nodepilot-ai-chat-${slug}-${date}.md`, md);
  }, [messages, threads, threadId, t]);

  const examples = useMemo(
    () => (t('ai:knowledge.examples', { returnObjects: true }) as string[]) ?? [],
    [t],
  );
  const lastIndex = messages.length - 1;

  // Disabled state: capabilities loaded and the chat is off (Llm or AiKnowledge master off).
  if (caps && !caps.enabled) {
    return (
      <div className="mx-auto max-w-3xl">
        <PageHeader t={t} />
        <div className="np-card mt-6 p-6 text-center text-on-surface-variant">
          <Chat size={32} className="mx-auto mb-3 opacity-50" />
          <p className="font-medium text-on-surface">{t('ai:knowledge.disabledTitle')}</p>
          <p className="mt-1 text-sm">{t('ai:knowledge.disabledBody')}</p>
        </div>
      </div>
    );
  }

  return (
    <div className="mx-auto flex h-[calc(100dvh-6rem)] min-h-0 w-full max-w-3xl flex-col">
      {/* Header: title/subtitle + thread menu + export/clear */}
      <div className="flex items-start justify-between gap-3">
        <PageHeader t={t} />
        <div className="flex shrink-0 items-center gap-1.5">
          <ThreadMenu
            threads={threads}
            activeId={threadId}
            disabled={sending}
            onSelect={(id) => { setActiveThread(scope, id); setError(null); }}
            onNew={() => { createThread(scope, t('ai:chat.threadDefault', { n: threads.length + 1 })); setError(null); }}
            onRename={(id, name) => renameThread(scope, id, name)}
            onDelete={(id) => { removeThread(scope, id); setError(null); }}
            t={t}
          />
          <button
            onClick={exportChat}
            disabled={messages.length === 0}
            title={t('ai:knowledge.export')}
            aria-label={t('ai:knowledge.export')}
            className="rounded-lg p-2 text-on-surface-variant transition-colors hover:bg-surface-highest hover:text-on-surface disabled:pointer-events-none disabled:opacity-40"
          >
            <Download size={18} />
          </button>
          <button
            onClick={() => { setMessages(() => []); setError(null); }}
            disabled={messages.length === 0 || sending}
            title={t('ai:knowledge.clear')}
            aria-label={t('ai:knowledge.clear')}
            className="rounded-lg p-2 text-on-surface-variant transition-colors hover:bg-red-500/10 hover:text-red-500 disabled:pointer-events-none disabled:opacity-40"
          >
            <TrashCan size={18} />
          </button>
        </div>
      </div>

      {caps && <SourceBadges caps={caps} t={t} />}

      {/* Messages */}
      <div className="relative mt-6 min-h-0 flex-1">
        <div ref={scrollRef} onScroll={onScroll} className="absolute inset-0 space-y-5 overflow-y-auto pr-1">
          {messages.length === 0 ? (
            <EmptyState examples={examples} onPick={sendQuestion} t={t} />
          ) : (
            messages.map((m, i) => (
              <MessageBubble
                key={i}
                message={m}
                isLastAssistant={m.role === 'assistant' && i === lastIndex && !sending}
                onRegenerate={regenerate}
                t={t}
              />
            ))
          )}
        </div>
        {!atBottom && messages.length > 0 && (
          <button
            onClick={scrollToBottom}
            className="absolute bottom-2 right-3 flex h-8 w-8 items-center justify-center rounded-full border border-outline-variant/40 bg-surface-high text-on-surface-variant shadow-md transition-colors hover:text-on-surface"
            title={t('ai:chat.scrollToBottom')}
            aria-label={t('ai:chat.scrollToBottom')}
          >
            <ChevronDown size={16} />
          </button>
        )}
      </div>

      {/* Error + Retry */}
      {error && (
        <div role="alert" className="mt-2 flex items-start justify-between gap-2 whitespace-pre-wrap rounded-lg border border-error/30 bg-error-container/20 px-2.5 py-2 text-xs text-on-error-container">
          <span className="min-w-0 flex-1">{error}</span>
          <button
            onClick={() => { setError(null); regenerate(); }}
            className="flex shrink-0 items-center gap-1 rounded px-1.5 py-0.5 font-label font-semibold text-on-error-container transition-colors hover:bg-error-container/40"
          >
            <Reset size={11} /> {t('ai:chat.retry')}
          </button>
        </div>
      )}

      {/* Composer (in-pill) */}
      <div className="mt-3">
        <div className="flex items-end gap-2 rounded-2xl border border-outline-variant/40 bg-surface-high px-3 py-2 transition-colors focus-within:border-primary/50 focus-within:ring-2 focus-within:ring-primary/15">
          <textarea
            ref={textareaRef}
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder={t('ai:knowledge.inputPlaceholder')}
            rows={1}
            disabled={sending}
            aria-label={t('ai:knowledge.inputPlaceholder')}
            className="max-h-[168px] min-h-[1.5rem] flex-1 resize-none border-0 bg-transparent text-sm leading-relaxed text-on-surface outline-none placeholder:text-on-surface-variant/60"
          />
          {sending ? (
            <button
              onClick={handleStop}
              title={t('ai:chat.stop')}
              aria-label={t('ai:chat.stop')}
              className="flex h-8 w-8 shrink-0 items-center justify-center rounded-xl bg-error text-on-error shadow-sm transition-all hover:brightness-110 active:scale-95"
            >
              <Checkbox size={13} className="fill-current" />
            </button>
          ) : (
            <button
              onClick={() => sendQuestion(input)}
              disabled={input.trim().length === 0}
              title={t('ai:knowledge.send')}
              aria-label={t('ai:knowledge.send')}
              className="flex h-8 w-8 shrink-0 items-center justify-center rounded-xl bg-primary text-on-primary shadow-sm transition-all hover:brightness-110 hover:shadow active:scale-95 disabled:cursor-not-allowed disabled:bg-primary/40 disabled:shadow-none"
            >
              <Send size={15} />
            </button>
          )}
        </div>
        <p className="mt-1.5 px-1 text-[10px] text-on-surface-variant/70">{t('ai:chat.enterHint')}</p>
      </div>
    </div>
  );
}

// Icons paired to the ordered `knowledge.examples` prompts (docs how-to, ops, config).
// Falls back to Chat when more prompts than icons are configured.
const EXAMPLE_ICONS: (typeof Document)[] = [
  Document,        // set up a webhook trigger
  WarningAlt,      // which workflows failed
  FlowModeler,     // what does 'Nightly Backup' do
  Email,           // email alerts for failed runs
  Time,            // workflows on a schedule
  BareMetalServer, // machines unreachable
  Renew,           // retry a failed run
  Save,            // system-configuration backup
];

function EmptyState({
  examples, onPick, t,
}: Readonly<{
  examples: string[];
  onPick: (q: string) => void;
  t: (k: string) => string;
}>) {
  return (
    <div className="flex h-full flex-col items-center justify-center px-2 py-8 text-center">
      <div className="flex h-14 w-14 items-center justify-center rounded-2xl bg-primary-fixed/40 text-primary">
        <Chat size={28} />
      </div>
      <h2 className="mt-4 text-lg font-semibold text-on-surface">{t('ai:knowledge.emptyTitle')}</h2>
      <p className="mt-1 max-w-md text-sm text-on-surface-variant">{t('ai:knowledge.emptyHint')}</p>
      {examples.length > 0 && (
        <div className="mt-6 grid w-full max-w-xl grid-cols-1 gap-2 sm:grid-cols-2">
          {examples.map((ex, i) => {
            const Icon = EXAMPLE_ICONS[i] ?? Chat;
            return (
              <button
                key={ex}
                onClick={() => onPick(ex)}
                className="group flex items-start gap-2.5 rounded-xl border border-outline-variant/40 bg-surface-high px-3 py-2.5 text-left text-sm text-on-surface transition-colors hover:border-primary/40 hover:bg-surface-highest"
              >
                <Icon size={18} className="mt-0.5 shrink-0 text-on-surface-variant transition-colors group-hover:text-primary" />
                <span className="min-w-0">{ex}</span>
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}

function PageHeader({ t }: { t: (k: string) => string }) {
  return (
    <div className="min-w-0">
      <h1 className="flex items-center gap-2 text-xl font-bold text-on-surface">
        <Chat size={22} className="text-primary" />
        {t('ai:knowledge.title')}
      </h1>
      <p className="mt-0.5 text-sm text-on-surface-variant">{t('ai:knowledge.subtitle')}</p>
    </div>
  );
}

function SourceBadges({ caps, t }: { caps: KnowledgeCapabilities; t: (k: string) => string }) {
  const badges: { on: boolean; icon: typeof Document; label: string }[] = [
    { on: caps.docs, icon: Document, label: t('ai:knowledge.sourceDocs') },
    { on: caps.operational, icon: FlowModeler, label: t('ai:knowledge.sourceOperational') },
    { on: caps.sourceCode, icon: Code, label: t('ai:knowledge.sourceCode') },
  ];
  const active = badges.filter((b) => b.on);
  if (active.length === 0) return null;
  return (
    <div className="mt-3 flex flex-wrap items-center gap-1.5">
      <span className="text-xs text-on-surface-variant/70">{t('ai:knowledge.sourcesLabel')}</span>
      {active.map((b) => (
        <span key={b.label} className="inline-flex items-center gap-1 rounded-full bg-surface-highest px-2 py-0.5 text-xs text-on-surface-variant">
          <b.icon size={12} />
          {b.label}
        </span>
      ))}
    </div>
  );
}

function MessageBubble({
  message, isLastAssistant, onRegenerate, t,
}: Readonly<{
  message: ChatMessage;
  isLastAssistant: boolean;
  onRegenerate: () => void;
  t: (k: string, opts?: Record<string, unknown>) => string;
}>) {
  if (message.role === 'user') {
    return (
      <div className="flex justify-end">
        <div className="max-w-[85%] whitespace-pre-wrap rounded-2xl rounded-br-md bg-primary px-4 py-2.5 text-sm leading-relaxed text-on-primary shadow-sm">
          {message.content}
        </div>
      </div>
    );
  }

  const showActions = !message.streaming && message.content.length > 0;

  return (
    <div className="group flex items-start gap-2">
      <div className="mt-0.5 flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-primary-fixed text-primary">
        <Chat size={13} />
      </div>
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        <div className="rounded-2xl rounded-bl-md border border-outline-variant/20 bg-surface-high px-4 py-3 shadow-sm">
          {message.streaming && message.content.length === 0 ? (
            <span className="flex items-center gap-1.5 text-xs text-on-surface-variant">
              <CircleDash size={13} className="animate-spin" /> {t('ai:chat.sending')}
            </span>
          ) : (
            <div className="relative">
              <Markdown size="base">{message.content}</Markdown>
              {message.streaming && (
                <span className="ml-0.5 inline-block h-3.5 w-[2px] -translate-y-px animate-pulse bg-primary align-middle" />
              )}
            </div>
          )}
        </div>

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

        {showActions && (
          <div className="flex items-center gap-1 pl-1 opacity-0 transition-opacity focus-within:opacity-100 group-hover:opacity-100">
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
            {message.meta && <UsageFooter meta={message.meta} t={t} />}
          </div>
        )}
      </div>
    </div>
  );
}

function UsageFooter({ meta, t }: Readonly<{ meta: ChatDoneMeta; t: (k: string, opts?: Record<string, unknown>) => string }>) {
  const tokens = meta.promptTokens != null && meta.completionTokens != null
    ? meta.promptTokens + meta.completionTokens
    : null;
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

function ThreadMenu({
  threads, activeId, disabled, onSelect, onNew, onRename, onDelete, t,
}: Readonly<{
  threads: ChatThreadMeta[];
  activeId: string;
  disabled?: boolean;
  onSelect: (id: string) => void;
  onNew: () => void;
  onRename: (id: string, name: string) => void;
  onDelete: (id: string) => void;
  t: (k: string, opts?: Record<string, unknown>) => string;
}>) {
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
        className="flex min-w-0 items-center gap-1 rounded-lg border border-outline-variant/40 px-2.5 py-1.5 text-sm text-on-surface transition-colors hover:bg-surface-highest"
        title={t('ai:chat.threads')}
        aria-label={t('ai:chat.threads')}
      >
        <span className="max-w-[11rem] truncate">{activeName}</span>
        <ChevronDown size={13} className="shrink-0 text-on-surface-variant" />
      </button>
      {open && (
        <div className="absolute right-0 top-full z-20 mt-1 w-64 rounded-lg border border-outline-variant/30 bg-surface-low p-1 shadow-lg">
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
