import { lazy, Suspense, useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import { Chat, ChevronDown, Launch, CircleDash } from '@carbon/icons-react';
import { useLocation, useNavigate } from 'react-router';
import { useTranslation } from 'react-i18next';
import { useAiCapabilities } from '../../hooks/useAiCapabilities';
import { useMediaQuery } from '../../hooks/useMediaQuery';
import { useKnowledgeChatSessionStore } from '../../stores/knowledgeChatSessionStore';
import './aiChatWidget.css';

const KnowledgeChat = lazy(() => import('./KnowledgeChat').then((m) => ({ default: m.KnowledgeChat })));

/** One entry point for the authenticated app, including the designer's separate layout. */
export function AiChatWidget() {
  const { t } = useTranslation(['ai', 'common']);
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const caps = useAiCapabilities().data;
  const open = useKnowledgeChatSessionStore((s) => s.widgetOpen);
  const unread = useKnowledgeChatSessionStore((s) => s.unread);
  const sending = useKnowledgeChatSessionStore((s) => s.sendingKey !== null);
  const setOpen = useKnowledgeChatSessionStore((s) => s.setWidgetOpen);
  const mobile = useMediaQuery('(max-width: 639px)');
  const pageVisible = pathname === '/ai-chat';
  const available = Boolean(caps?.enabled) && !pageVisible;
  const visible = available && open;
  const launcher = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);

  useEffect(() => {
    useKnowledgeChatSessionStore.getState().setPageVisible(pageVisible);
  }, [pageVisible]);

  useEffect(() => {
    if (caps && !caps.enabled) {
      useKnowledgeChatSessionStore.getState().stop();
      setOpen(false);
    }
  }, [caps, setOpen]);

  useEffect(() => () => {
    useKnowledgeChatSessionStore.getState().stop();
    useKnowledgeChatSessionStore.getState().reset();
  }, []);

  useEffect(() => {
    // Toasts share this corner; move them clear of the launcher and open conversation.
    document.body.dataset.aiChat = visible ? 'open' : available ? 'launcher' : '';
    return () => { delete document.body.dataset.aiChat; };
  }, [available, visible]);

  useEffect(() => {
    if (!visible) return;
    panel.current?.focus({ preventScroll: true });
    const content = document.getElementById('np-app-content');
    if (mobile && content) content.inert = true;
    return () => { if (content) content.inert = false; };
  }, [visible, mobile]);

  useEffect(() => {
    if (!visible || !mobile) return;
    const viewport = window.visualViewport;
    const update = () => {
      panel.current?.style.setProperty('--chat-viewport-height', `${viewport?.height ?? window.innerHeight}px`);
      panel.current?.style.setProperty('--chat-viewport-top', `${viewport?.offsetTop ?? 0}px`);
    };
    update();
    viewport?.addEventListener('resize', update);
    viewport?.addEventListener('scroll', update);
    return () => {
      viewport?.removeEventListener('resize', update);
      viewport?.removeEventListener('scroll', update);
    };
  }, [visible, mobile]);

  const minimize = () => {
    setOpen(false);
    requestAnimationFrame(() => launcher.current?.focus({ preventScroll: true }));
  };

  if (!available) return null;
  return createPortal(
    <div className="np-shell">
      {visible && (
        <div
          ref={panel}
          id="np-ai-chat-panel"
          className="np-ai-chat-panel"
          role="dialog"
          aria-modal={mobile || undefined}
          aria-labelledby="np-ai-chat-title"
          tabIndex={-1}
          onKeyDown={(event) => {
            event.stopPropagation();
            if (event.key === 'Escape' && !event.defaultPrevented) {
              minimize();
            }
            if (event.key !== 'Tab' || !mobile) return;
            const controls = Array.from(event.currentTarget.querySelectorAll<HTMLElement>(
              'button:not(:disabled), a[href], textarea:not(:disabled), input:not(:disabled), summary, [tabindex="0"]',
            )).filter((element) => element.getClientRects().length > 0);
            const first = controls[0];
            const last = controls[controls.length - 1];
            if (event.shiftKey && (document.activeElement === first || document.activeElement === panel.current)) {
              event.preventDefault();
              last?.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
              event.preventDefault();
              first?.focus();
            }
          }}
        >
          <header className="np-ai-chat-header">
            <span className="np-ai-chat-mark"><Chat size={22} /></span>
            <div className="min-w-0 flex-1">
              <h2 id="np-ai-chat-title" className="truncate text-sm font-semibold">{t('ai:widget.title')}</h2>
              <p className="mt-0.5 text-xs text-on-surface-variant">{t('ai:widget.subtitle')}</p>
            </div>
            <button className="np-ai-chat-action" title={t('ai:widget.expand')} aria-label={t('ai:widget.expand')}
              onClick={() => { setOpen(false); navigate('/ai-chat'); }}>
              <Launch size={18} />
            </button>
            <button className="np-ai-chat-action" title={t('ai:widget.minimize')} aria-label={t('ai:widget.minimize')} onClick={minimize}>
              <ChevronDown size={20} />
            </button>
          </header>
          <div className="flex min-h-0 flex-1 flex-col p-4">
            <Suspense fallback={<div role="status" className="m-auto text-sm text-on-surface-variant">{t('common:loading')}</div>}>
              <KnowledgeChat compact />
            </Suspense>
          </div>
        </div>
      )}
      <button
        ref={launcher}
        className={`np-ai-chat-launcher ${visible ? 'np-ai-chat-launcher-open' : ''}`}
        aria-label={t(visible ? 'ai:widget.minimize' : unread ? 'ai:widget.openUnread' : 'ai:widget.open')}
        aria-expanded={visible}
        aria-controls={visible ? 'np-ai-chat-panel' : undefined}
        title={t(visible ? 'ai:widget.minimize' : 'ai:widget.open')}
        onClick={() => visible ? minimize() : setOpen(true)}
      >
        {visible ? <ChevronDown size={25} /> : <Chat size={25} />}
        {sending && !visible && <CircleDash size={17} className="np-ai-chat-indicator animate-spin" />}
        {unread && !visible && <span className="np-ai-chat-unread" />}
      </button>
      <span className="sr-only" role="status">{unread ? t('ai:widget.answerReady') : ''}</span>
    </div>,
    document.body,
  );
}
