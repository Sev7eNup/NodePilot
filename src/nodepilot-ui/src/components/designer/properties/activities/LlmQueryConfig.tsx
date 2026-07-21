import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';

/**
 * Config editor for the `llmQuery` activity. Prompt + optional system prompt support {{variable}}
 * insertion. The "Override" section lets a node target a different OpenAI-compatible endpoint/model
 * and tune maxTokens/temperature/jsonMode — all optional; empty fields fall back to the global
 * `Llm:*` config. `timeoutSeconds` is NOT rendered here: PropertiesPanel auto-renders it because the
 * activity declares `timeout: always`.
 */
export function LlmQueryConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');

  return (
    <>
      <VariableInsertField
        label={t('config.llmQuery.prompt')}
        value={(config.prompt as string) || ''}
        onChange={(v) => onUpdate({ prompt: v })}
        upstreamVars={upstreamVars}
        multiline
        rows={5}
        placeholder={t('config.llmQuery.promptPlaceholder')}
      />

      <VariableInsertField
        label={t('config.llmQuery.systemPrompt')}
        value={(config.systemPrompt as string) || ''}
        onChange={(v) => onUpdate({ systemPrompt: v })}
        upstreamVars={upstreamVars}
        multiline
        rows={2}
        placeholder={t('config.llmQuery.systemPromptPlaceholder')}
      />

      <Field label={t('config.llmQuery.jsonMode')}>
        <label className="flex items-start gap-2 cursor-pointer select-none py-1">
          <input
            type="checkbox"
            checked={config.jsonMode === true}
            onChange={(e) => onUpdate({ jsonMode: e.target.checked })}
            className="mt-0.5 w-4 h-4 rounded border-outline-variant accent-primary"
          />
          <div className="flex-1 text-sm text-on-surface">{t('config.llmQuery.jsonModeHint')}</div>
        </label>
      </Field>

      <div className="pt-2 mt-1 border-t border-outline-variant/40 space-y-3">
        <p className="text-[11px] text-on-surface-variant leading-snug">{t('config.llmQuery.overrideHint')}</p>

        <Field label={t('config.llmQuery.baseUrl')}>
          <input
            type="text"
            value={(config.baseUrl as string) || ''}
            onChange={(e) => onUpdate({ baseUrl: e.target.value })}
            className="input-field font-mono"
            placeholder={t('config.llmQuery.baseUrlPlaceholder')}
          />
        </Field>

        <Field label={t('config.llmQuery.model')}>
          <input
            type="text"
            value={(config.model as string) || ''}
            onChange={(e) => onUpdate({ model: e.target.value })}
            className="input-field"
            placeholder={t('config.llmQuery.modelPlaceholder')}
          />
        </Field>

        <Field label={t('config.llmQuery.apiKey')}>
          <input
            type="password"
            value={(config.apiKey as string) || ''}
            onChange={(e) => onUpdate({ apiKey: e.target.value })}
            className="input-field font-mono"
            placeholder={t('config.llmQuery.apiKeyPlaceholder')}
            autoComplete="off"
          />
        </Field>

        <div className="grid grid-cols-2 gap-3">
          <Field label={t('config.llmQuery.maxTokens')}>
            <input
              type="number"
              value={(config.maxTokens as number) ?? ''}
              onChange={(e) => onUpdate({ maxTokens: e.target.value === '' ? undefined : parseInt(e.target.value, 10) })}
              className="input-field"
              min={1}
              placeholder={t('config.llmQuery.defaultPlaceholder')}
            />
          </Field>
          <Field label={t('config.llmQuery.temperature')}>
            <input
              type="number"
              value={(config.temperature as number) ?? ''}
              onChange={(e) => onUpdate({ temperature: e.target.value === '' ? undefined : parseFloat(e.target.value) })}
              className="input-field"
              min={0}
              max={2}
              step={0.1}
              placeholder={t('config.llmQuery.defaultPlaceholder')}
            />
          </Field>
        </div>
      </div>
    </>
  );
}
