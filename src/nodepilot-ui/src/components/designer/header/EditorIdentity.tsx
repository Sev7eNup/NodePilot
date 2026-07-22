import { ArrowLeft, ChatBot } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router-dom';
import { WorkflowDesignerIcon } from '../WorkflowDesignerIcon';
import { useDesignStore } from '../../../stores/designStore';

/**
 * Left identity zone of the editor header (shared by both toolbar layouts): back navigation,
 * the Workflow Designer brand, the Standard/Expert mode toggle and the AI-assistant button.
 * Self-contained except for the AI-panel toggle, which the page owns.
 */
export function EditorIdentity({ aiChatOpen, onToggleAiChat }: Readonly<{
  aiChatOpen: boolean;
  onToggleAiChat: () => void;
}>) {
  const { t } = useTranslation(['editor', 'ai']);
  const location = useLocation();
  const routerNavigate = useNavigate();
  const fromWorkflow = (location.state as { fromWorkflow?: { id: string; name: string } } | null)?.fromWorkflow;
  const designerMode = useDesignStore((s) => s.designerMode);
  const setDesignerMode = useDesignStore((s) => s.setDesignerMode);

  const handleBack = () => {
    // Workflow-to-workflow entries carry fromWorkflow state and can safely use the browser
    // stack. Direct editor entries fall back to the workflow list to match the button label.
    if (fromWorkflow) routerNavigate(-1);
    else routerNavigate('/workflows');
  };

  return (
    <div className="flex items-center gap-3 min-w-0 shrink-0">
      <button
        onClick={handleBack}
        className="text-on-surface-variant hover:text-on-surface transition-colors shrink-0"
        title={fromWorkflow ? t('editor:backTo', { name: fromWorkflow.name, defaultValue: `Zurück zu: ${fromWorkflow.name}` }) : t('editor:backToWorkflowList', { defaultValue: 'Zurück zur Workflow-Liste' })}
        aria-label={fromWorkflow ? t('editor:backTo', { name: fromWorkflow.name, defaultValue: `Zurück zu ${fromWorkflow.name}` }) : t('editor:backToWorkflowList', { defaultValue: 'Zurück zur Workflow-Liste' })}
      >
        <ArrowLeft size={20} />
      </button>
      <WorkflowDesignerIcon className="shrink-0 h-6 w-6 xl:h-8 xl:w-8 drop-shadow-[0_3px_10px_color-mix(in_srgb,var(--color-primary)_45%,transparent)]" />
      {/* The wordmark disappears below xl so the centered name has room; the logo stays as a
          brand anchor. Colours follow the active skin (primary → primary-container). */}
      <h2 className="hidden xl:block font-headline leading-none">
        <span className="font-black text-xl bg-gradient-to-r from-primary to-primary-container bg-clip-text text-transparent">Workflow</span>
        <span className="block font-semibold text-[9px] tracking-[0.35em] uppercase text-primary-container ml-0.5">Designer</span>
      </h2>
      <div
        className="flex items-center rounded-md bg-surface-high p-0.5 shrink-0"
        role="group"
        aria-label={t('editor:designerMode.label')}
      >
        {(['standard', 'expert'] as const).map((mode) => (
          <button
            key={mode}
            type="button"
            onClick={() => setDesignerMode(mode)}
            aria-pressed={designerMode === mode}
            className={`rounded px-2 py-1 text-[10px] font-label font-semibold transition-colors ${
              designerMode === mode
                ? 'bg-primary text-on-primary shadow-sm'
                : 'text-on-surface-variant hover:text-on-surface'
            }`}
            title={t(`editor:designerMode.${mode}Description`)}
          >
            {t(`editor:designerMode.${mode}`)}
          </button>
        ))}
      </div>
      {/* AI workflow assistant — purple, next to the Standard/Expert toggle. Visible to all
          roles; when Llm:Enabled=false the panel shows a 503 error. */}
      <button
        type="button"
        onClick={onToggleAiChat}
        aria-pressed={aiChatOpen}
        data-testid="toggle-ai-assistant"
        className={`flex items-center gap-1.5 rounded-md px-2.5 py-1 text-[10px] font-label font-semibold shadow-sm transition-all shrink-0 ${
          aiChatOpen
            ? 'bg-primary-container text-on-primary-container shadow-inner'
            : 'bg-gradient-to-br from-primary to-primary-container text-on-primary hover:shadow'
        }`}
        title={t('ai:chat.buttonTitle')}
      >
        <ChatBot size={15} />
        <span className="hidden xl:inline">{t('ai:chat.buttonLabel')}</span>
      </button>
    </div>
  );
}
