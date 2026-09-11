import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  BareMetalServer, ChartColumn, Chat, Checkbox, Checkmark, ChevronDown, CircleDash, Code,
  DataBase, DataShare, Debug, Document, Download, Email, Events, FlowModeler, InProgress,
  Locked, Renew, Reset, Save, Send, Time, Tools, TrashCan, UserRole, WarningAlt,
} from '@carbon/icons-react';
import type { KnowledgeCapabilities } from '../../api/ai';
import { useAiCapabilities } from '../../hooks/useAiCapabilities';
import { Markdown } from '../common/Markdown';
import { CopyButton } from '../common/CopyButton';
import { UsageFooter } from './UsageFooter';
import { ChatThreadMenu } from './ChatThreadMenu';
import {
  useAiChatStore, aiChatScopeKey, aiChatFullKey,
  type ChatMessage, type ChatThreadMeta,
} from '../../stores/aiChatStore';
import { useAuthStore } from '../../stores/authStore';
import { useKnowledgeChatSessionStore } from '../../stores/knowledgeChatSessionStore';
import { buildChatMarkdown, chatFilenameSlug, downloadTextFile } from '../../lib/chatExport';

// A non-`__new__` sentinel workflowId so the store's `isPersistableScope` keeps this page's
// threads across reloads, unlike an unsaved canvas. One shared scope per user.
const GLOBAL_SCOPE = 'global';
// Phones show the first four starter prompts; the rest come back at `lg`. The full set stacks
// into a column taller than the screen and pushes the composer out of reach.
const MOBILE_EXAMPLE_COUNT = 4;
const EMPTY_THREAD: ChatMessage[] = [];
const EMPTY_THREADS: ChatThreadMeta[] = [];

/**
 * Global AI Chat: a read-only knowledge and operations assistant over the docs, the installed
 * workflows and operations, and source code when that source is enabled. It offers the same
 * features as the designer's in-canvas assistant (threads, regenerate, copy, usage footer,
 * scroll-to-bottom, streaming cursor) without the canvas-only proposals, apply, undo and
 * mentions.
 */
export function KnowledgeChat({ compact = false }: Readonly<{ compact?: boolean }>) {
  const { t } = useTranslation(['ai', 'common']);

  const capsQuery = useAiCapabilities();
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
  }, [scope, activeThreadId, ensureActiveThread, t]);

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

  const input = useKnowledgeChatSessionStore((s) => s.drafts[fullKey] ?? '');
  const sending = useKnowledgeChatSessionStore((s) => s.sendingKey !== null);
  const error = useKnowledgeChatSessionStore((s) => s.errors[fullKey]);
  const setDraft = useKnowledgeChatSessionStore((s) => s.setDraft);
  const clearError = useKnowledgeChatSessionStore((s) => s.clearError);
  const forgetThread = useKnowledgeChatSessionStore((s) => s.forgetThread);
  const send = useKnowledgeChatSessionStore((s) => s.send);
  const retry = useKnowledgeChatSessionStore((s) => s.regenerate);
  const handleStop = useKnowledgeChatSessionStore((s) => s.stop);
  const setInput = useCallback((value: string) => setDraft(fullKey, value), [fullKey, setDraft]);
  const [atBottom, setAtBottom] = useState(true);
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  const atBottomRef = useRef(true);

  useEffect(() => {
    if (compact && caps?.enabled && !window.matchMedia('(max-width: 639px)').matches) {
      textareaRef.current?.focus({ preventScroll: true });
    }
  }, [compact, caps?.enabled]);

  // Auto-scroll only when already at the bottom, so reading history is not interrupted.
  useEffect(() => {
    const el = scrollRef.current;
    if (el && atBottomRef.current && typeof el.scrollTo === 'function') {
      el.scrollTo({ top: el.scrollHeight, behavior: 'instant' });
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
    if (el && typeof el.scrollTo === 'function') el.scrollTo({ top: el.scrollHeight, behavior: 'instant' });
    atBottomRef.current = true;
    setAtBottom(true);
  }, []);

  const sendQuestion = useCallback((raw: string) => {
    if (!caps?.enabled) return;
    atBottomRef.current = true;
    setAtBottom(true);
    void send(scope, threadId, raw);
  }, [caps?.enabled, send, scope, threadId]);
  const regenerate = useCallback(() => {
    if (caps?.enabled) void retry(scope, threadId);
  }, [caps?.enabled, retry, scope, threadId]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); sendQuestion(input); return; }
    // Up-arrow in an empty composer recalls the last question, like a shell history.
    if (e.key === 'ArrowUp' && input.length === 0) {
      const lastUser = [...messages].reverse().find((m) => m.role === 'user');
      if (lastUser) { e.preventDefault(); setInput(lastUser.content); }
    }
  }, [sendQuestion, input, messages, setInput]);

  const exportChat = useCallback(() => {
    if (messages.length === 0) return;
    const title = threads.find((th) => th.id === threadId)?.name ?? t('ai:knowledge.title');
    const md = buildChatMarkdown(title, messages, {
      user: t('ai:knowledge.roleUser'),
      assistant: t('ai:knowledge.roleAssistant'),
      proposal: t('ai:knowledge.roleAssistant'),
    });
    const slug = chatFilenameSlug(title);
    const date = new Date().toISOString().slice(0, 10);
    downloadTextFile(`nodepilot-ai-chat-${slug}-${date}.md`, md);
  }, [messages, threads, threadId, t]);

  // Starter prompts follow the enabled sources. The ops set relies on the DB and text2sql
  // tools, which are off by default and global-Admin-only, so offering those prompts without
  // the source only yields a "source not available" answer. Both sets are held back until
  // `caps` resolves, otherwise the lite set flashes before swapping.
  const examples = useMemo(() => {
    if (!caps) return [];
    const key = caps.db ? 'ai:knowledge.examples' : 'ai:knowledge.examplesLite';
    return (t(key, { returnObjects: true }) as string[]) ?? [];
  }, [t, caps]);
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
    <div className={compact ? "np-chat-content flex min-h-0 flex-1 flex-col" : "mx-auto flex h-[calc(100dvh-6rem)] min-h-0 w-full max-w-3xl flex-col"}>
      {/* Header: title/subtitle + thread menu + export/clear */}
      <div className="flex items-start justify-between gap-3">
        {!compact && <PageHeader t={t} />}
        <div className={`flex items-center gap-1.5 ${compact ? 'w-full' : 'shrink-0'}`}>
          <ChatThreadMenu
            threads={threads}
            activeId={threadId}
            disabled={sending}
            onSelect={(id) => setActiveThread(scope, id)}
            onNew={() => createThread(scope, t('ai:chat.threadDefault', { n: threads.length + 1 }))}
            onRename={(id, name) => renameThread(scope, id, name)}
            onDelete={(id) => { removeThread(scope, id); forgetThread(aiChatFullKey(scope, id)); }}
            triggerClassName="flex min-h-9 min-w-0 items-center gap-1 rounded-lg border border-outline-variant/40 px-2.5 py-1.5 text-sm text-on-surface transition-colors hover:bg-surface-highest"
            align={compact ? "left" : "right"}
          />
          {compact && <span className="flex-1" />}
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
            onClick={() => { setMessages(() => []); clearError(fullKey); }}
            disabled={messages.length === 0 || sending}
            title={t('ai:knowledge.clear')}
            aria-label={t('ai:knowledge.clear')}
            className="rounded-lg p-2 text-on-surface-variant transition-colors hover:bg-error/10 hover:text-error disabled:pointer-events-none disabled:opacity-40"
          >
            <TrashCan size={18} />
          </button>
        </div>
      </div>

      {caps && (compact ? <details className="np-chat-sources mt-2 text-xs text-on-surface-variant"><summary className="cursor-pointer py-1">{t('ai:widget.sources')}</summary><SourceBadges caps={caps} t={t} compact /></details> : <SourceBadges caps={caps} t={t} />)}

      {/* Messages */}
      <div className={`relative min-h-0 flex-1 ${compact ? 'mt-3' : 'mt-6'}`}>
        <div ref={scrollRef} onScroll={onScroll} data-testid="ai-chat-scroll" className="absolute inset-0 space-y-5 overflow-y-auto pr-1">
          {messages.length === 0 ? (
            <EmptyState
              examples={compact ? examples.slice(0, 3) : examples}
              compact={compact}
              icons={caps?.db ? EXAMPLE_ICONS_OPS : EXAMPLE_ICONS_LITE}
              onPick={sendQuestion}
              t={t}
            />
          ) : (
            messages.map((m, i) => (
              <MessageBubble
                key={i}
                message={m}
                compact={compact}
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
          <span className="min-w-0 flex-1">{t('ai:chat.errorPrefix', { message: error })}</span>
          <button
            onClick={() => { clearError(fullKey); regenerate(); }}
            className="flex shrink-0 items-center gap-1 rounded px-1.5 py-0.5 font-label font-semibold text-on-error-container transition-colors hover:bg-error-container/40"
          >
            <Reset size={11} /> {t('ai:chat.retry')}
          </button>
        </div>
      )}

      <span role="status" aria-live="polite" className="sr-only">
        {sending ? t('ai:chat.sending') : messages.some((m) => m.role === 'assistant') ? t('ai:widget.answerReady') : ''}
      </span>
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
            disabled={!caps?.enabled}
            maxLength={8000}
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
              disabled={!caps?.enabled || !threadId || input.trim().length === 0}
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

// Icons paired positionally with the ordered example prompts, one entry per i18n array item.
// Falls back to Chat when more prompts than icons are configured.
const EXAMPLE_ICONS_OPS: (typeof Document)[] = [
  WarningAlt,      // last 10 failed runs
  Debug,           // which step broke
  ChartColumn,     // most-failing workflows this week
  InProgress,      // runs stuck in Running
  BareMetalServer, // machines unreachable
  Time,            // scheduled in the next 24h
  Events,          // audit trail
  Email,           // email on failure (docs)
];

const EXAMPLE_ICONS_LITE: (typeof Document)[] = [
  Time,      // scheduled in the next 24h
  Document,  // webhook trigger setup
  Email,     // email on failure
  Renew,     // retry a failed run
  DataShare, // pass data between steps
  Locked,    // edit lock
  Save,      // config backup + restore
  UserRole,  // what the Operator role may do
];

function EmptyState({
  examples, icons, onPick, t, compact,
}: Readonly<{
  compact?: boolean;
  examples: string[];
  icons: (typeof Document)[];
  onPick: (q: string) => void;
  t: (k: string) => string;
}>) {
  return (
    // `m-auto` instead of `justify-center`: both centre the block while it fits, but once the
    // prompt list outgrows the scroll port, such as on a phone in portrait, `justify-center`
    // pushes the overflow out on both sides and the part above the scroll origin can no longer
    // be scrolled into view. An `auto` margin collapses to 0 when there is no free space, so
    // the block falls back to top-aligned and stays reachable.
    <div className={`flex min-h-full flex-col px-2 ${compact ? 'py-3' : 'py-8'}`}>
      <div data-testid="ai-chat-empty" className="m-auto flex w-full flex-col items-center text-center">
        <div className="flex h-14 w-14 items-center justify-center rounded-2xl bg-primary-fixed/40 text-primary">
          <Chat size={28} />
        </div>
        <h2 className="mt-4 text-lg font-semibold text-on-surface">{t('ai:knowledge.emptyTitle')}</h2>
        {/* Desktop only, like the page subtitle: on a phone the heading alone is enough, and
            the wrapped lines push the prompts further down. */}
        <p className={`mt-1 max-w-md text-sm text-on-surface-variant ${compact ? 'hidden' : 'hidden lg:block'}`}>{t('ai:knowledge.emptyHint')}</p>
        {examples.length > 0 && (
          <div className={`grid w-full max-w-xl grid-cols-1 gap-2 ${compact ? 'mt-4' : 'mt-6 sm:grid-cols-2'}`}>
            {examples.map((ex, i) => {
              const Icon = icons[i] ?? Chat;
              return (
                <button
                  key={ex}
                  onClick={() => onPick(ex)}
                  className={`group ${i < MOBILE_EXAMPLE_COUNT ? 'flex' : 'hidden lg:flex'} items-start gap-2.5 rounded-xl border border-outline-variant/40 bg-surface-high px-3 py-2.5 text-left text-sm text-on-surface transition-colors hover:border-primary/40 hover:bg-surface-highest`}
                >
                  <Icon size={18} className="mt-0.5 shrink-0 text-on-surface-variant transition-colors group-hover:text-primary" />
                  <span className="min-w-0">{ex}</span>
                </button>
              );
            })}
          </div>
        )}
      </div>
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
      {/* Phones drop the subtitle: it wraps to three lines next to the thread menu and costs more
          vertical room than the chat can spare. */}
      <p className="mt-0.5 hidden text-sm text-on-surface-variant lg:block">{t('ai:knowledge.subtitle')}</p>
    </div>
  );
}

function SourceBadges({ caps, t, compact = false }: { caps: KnowledgeCapabilities; t: (k: string) => string; compact?: boolean }) {
  const badges: { on: boolean; icon: typeof Document; label: string }[] = [
    { on: caps.docs, icon: Document, label: t('ai:knowledge.sourceDocs') },
    { on: caps.operational, icon: FlowModeler, label: t('ai:knowledge.sourceOperational') },
    { on: caps.sourceCode, icon: Code, label: t('ai:knowledge.sourceCode') },
    { on: caps.db, icon: DataBase, label: t('ai:knowledge.sourceDb') },
  ];
  const active = badges.filter((b) => b.on);
  if (active.length === 0) return null;
  return (
    // Desktop-only: on a phone the badge row wraps to two lines and buys the reader nothing the
    // answers don't already show. The space goes to the conversation instead.
    <div className={`flex-wrap items-center gap-1.5 ${compact ? 'flex pb-1' : 'mt-3 hidden lg:flex'}`}>
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
  message, isLastAssistant, onRegenerate, t, compact,
}: Readonly<{
  compact?: boolean;
  message: ChatMessage;
  isLastAssistant: boolean;
  onRegenerate: () => void;
  t: (k: string, opts?: Record<string, unknown>) => string;
}>) {
  if (message.role === 'user') {
    return (
      <div className="flex justify-end">
        <div className={`max-w-[85%] whitespace-pre-wrap rounded-2xl rounded-br-md bg-primary px-4 py-2.5 ${compact ? 'text-xs' : 'text-sm'} leading-relaxed text-on-primary shadow-sm`}>
          {message.content}
        </div>
      </div>
    );
  }

  const showActions = !message.streaming && message.content.length > 0;

  return (
    <div className={`group flex items-start ${compact ? 'gap-0' : 'gap-2'}`}>
      <div className={`${compact ? 'hidden' : 'flex'} mt-0.5 h-6 w-6 shrink-0 items-center justify-center rounded-full bg-primary-fixed text-primary`}>
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
              <Markdown size={compact ? 'sm' : 'base'}>{message.content}</Markdown>
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
                  ? <Checkmark size={12} className="text-primary" />
                  : <CircleDash size={12} className="animate-spin" />}
                <Tools size={11} className="opacity-60" />
                {compact ? <span>{t('ai:widget.searching')}</span> : <code className="font-mono">{tc.toolName}</code>}
                <span className="opacity-70">{tc.done ? t('ai:chat.toolDone') : t('ai:chat.toolRunning')}</span>
              </span>
            ))}
          </div>
        )}

        {showActions && (
          <div className={`flex flex-wrap items-center gap-1 pl-1 transition-opacity ${compact ? '' : 'opacity-0 focus-within:opacity-100 group-hover:opacity-100'}`}>
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
            {message.meta && (compact ? <details className="ml-auto text-xs text-on-surface-variant"><summary className="cursor-pointer">{t('ai:widget.details')}</summary><UsageFooter meta={message.meta} /></details> : <UsageFooter meta={message.meta} />)}
          </div>
        )}
      </div>
    </div>
  );
}
