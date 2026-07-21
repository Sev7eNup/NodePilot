import { Chat } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import {
  useSectionForm,
  Card,
  HotReloadHint,
  Toggle,
  TextInput,
  NumberInput,
  ErrorsAndSave,
} from './SectionFormHelpers';

type AiKnowledgeDto = {
  enabled: boolean;
  docsEnabled: boolean;
  operationalEnabled: boolean;
  sourceCodeEnabled: boolean;
  docsRootPath: string | null;
  sourceCodeRootPath: string | null;
  docsMaxFileBytes: number;
  docsMaxResults: number;
  sourceCodeMaxFileBytes: number;
  sourceCodeMaxResults: number;
};

/**
 * "AI Knowledge (Chat)" section — governs the global AI-Chat assistant: a master switch plus
 * three per-source toggles (docs / operational data / source code) and the two live-read root
 * paths. Hot-reloadable (the chat reads IOptionsMonitor per turn), so a save takes effect without
 * a restart. Source-code exposure carries an inline confidentiality warning.
 */
export function AiKnowledgeSection() {
  const { t } = useTranslation(['adminSettings', 'common']);
  const ui = useSectionForm<AiKnowledgeDto>('AiKnowledge', {
    enabled: false,
    docsEnabled: true,
    operationalEnabled: true,
    sourceCodeEnabled: false,
    docsRootPath: null,
    sourceCodeRootPath: null,
    docsMaxFileBytes: 262144,
    docsMaxResults: 20,
    sourceCodeMaxFileBytes: 262144,
    sourceCodeMaxResults: 20,
  });
  if (ui.loading) return <Card icon={Chat} title={t('aiKnowledge.cardTitle')}><p className="text-sm">{t('loading')}</p></Card>;
  const { form, set, data, isEnvLocked, save, errors } = ui;

  const payload = () => ({
    Enabled: form.enabled,
    DocsEnabled: form.docsEnabled,
    OperationalEnabled: form.operationalEnabled,
    SourceCodeEnabled: form.sourceCodeEnabled,
    DocsRootPath: form.docsRootPath,
    SourceCodeRootPath: form.sourceCodeRootPath,
    DocsMaxFileBytes: form.docsMaxFileBytes,
    DocsMaxResults: form.docsMaxResults,
    SourceCodeMaxFileBytes: form.sourceCodeMaxFileBytes,
    SourceCodeMaxResults: form.sourceCodeMaxResults,
  });

  return (
    <div className="space-y-4">
      <Card icon={Chat} title={t('aiKnowledge.cardTitle')}>
        <HotReloadHint isHotReloadable={data.isHotReloadable} />
        <p className="text-xs text-on-surface-variant mb-3">{t('aiKnowledge.intro')}</p>

        <Toggle label={t('aiKnowledge.enabled')} checked={form.enabled}
          onChange={(v) => set({ ...form, enabled: v })}
          configKey="AiKnowledge:Enabled" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />

        <h4 className="font-medium text-sm mt-4 mb-2">{t('aiKnowledge.sourcesTitle')}</h4>
        <Toggle label={t('aiKnowledge.docsEnabled')} checked={form.docsEnabled}
          onChange={(v) => set({ ...form, docsEnabled: v })}
          configKey="AiKnowledge:DocsEnabled" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Toggle label={t('aiKnowledge.operationalEnabled')} checked={form.operationalEnabled}
          onChange={(v) => set({ ...form, operationalEnabled: v })}
          configKey="AiKnowledge:OperationalEnabled" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        <Toggle label={t('aiKnowledge.sourceCodeEnabled')} checked={form.sourceCodeEnabled}
          onChange={(v) => set({ ...form, sourceCodeEnabled: v })}
          configKey="AiKnowledge:SourceCodeEnabled" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        {form.sourceCodeEnabled && (
          <div className="mt-2 rounded-md border border-amber-500/40 bg-amber-500/10 p-2.5 text-xs text-amber-700 dark:text-amber-300">
            {t('aiKnowledge.sourceCodeWarning')}
          </div>
        )}

        <h4 className="font-medium text-sm mt-4 mb-2">{t('aiKnowledge.rootsTitle')}</h4>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <TextInput label={t('aiKnowledge.docsRootPath')} value={form.docsRootPath ?? ''}
            onChange={(v) => set({ ...form, docsRootPath: v || null })}
            configKey="AiKnowledge:DocsRootPath" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
            placeholder="{ContentRoot}\knowledge\docs" />
          <TextInput label={t('aiKnowledge.sourceCodeRootPath')} value={form.sourceCodeRootPath ?? ''}
            onChange={(v) => set({ ...form, sourceCodeRootPath: v || null })}
            configKey="AiKnowledge:SourceCodeRootPath" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked}
            placeholder="{ContentRoot}\knowledge\source" />
        </div>
        <p className="text-xs text-on-surface-variant mt-1">{t('aiKnowledge.rootsHint')}</p>

        <h4 className="font-medium text-sm mt-4 mb-2">{t('aiKnowledge.capsTitle')}</h4>
        <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
          <NumberInput label={t('aiKnowledge.docsMaxResults')} value={form.docsMaxResults}
            onChange={(v) => set({ ...form, docsMaxResults: v })} min={1} max={100}
            configKey="AiKnowledge:DocsMaxResults" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
          <NumberInput label={t('aiKnowledge.sourceCodeMaxResults')} value={form.sourceCodeMaxResults}
            onChange={(v) => set({ ...form, sourceCodeMaxResults: v })} min={1} max={100}
            configKey="AiKnowledge:SourceCodeMaxResults" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
          <NumberInput label={t('aiKnowledge.docsMaxFileBytes')} value={form.docsMaxFileBytes}
            onChange={(v) => set({ ...form, docsMaxFileBytes: v })} min={4096} max={8388608}
            configKey="AiKnowledge:DocsMaxFileBytes" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
          <NumberInput label={t('aiKnowledge.sourceCodeMaxFileBytes')} value={form.sourceCodeMaxFileBytes}
            onChange={(v) => set({ ...form, sourceCodeMaxFileBytes: v })} min={4096} max={8388608}
            configKey="AiKnowledge:SourceCodeMaxFileBytes" effectiveSource={data.effectiveSource} isEnvLocked={isEnvLocked} />
        </div>

        <ErrorsAndSave errors={errors} onSave={() => save(payload())} />
        {ui.dialog}
      </Card>
    </div>
  );
}
