import { useState, useCallback, lazy, Suspense } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { Field, SwitchField, type ConfigProps } from '../shared';
import { CodeField, FieldGrid } from '../panelChrome';
import { api } from '../../../../api/client';
import { useAiScriptStream } from '../../../../hooks/useAiScriptStream';
import type { StepTestResult } from '../../../../types/api';

// Monaco editor pulls in ~1 MB of language tokenizer assets — lazy-loaded so the
// initial workflow editor bundle stays unchanged and the chunk lands only when
// the user actually clicks "Open Editor".
const ScriptEditorDialog = lazy(() => import('../../ScriptEditorDialog'));

const CODE_HINT = 'bg-amber-100/80 dark:bg-amber-900/40 px-1 rounded';

export function RunScriptConfig({ config, onUpdate, upstreamVars = [], workflowId, stepId, outputVariableName, isLocalTarget = true }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const [showEditor, setShowEditor] = useState(false);
  const script = (config.script as string) || '';
  const transcript = (config.transcript as boolean) === true;
  const isolated = (config.isolated as boolean) === true;

  const psVars = upstreamVars
    .filter(v => v.expression.includes('.param.'))
    .map(v => ({ name: '$' + v.variable.split('.').pop(), label: v.label }));

  const upstreamRefs = upstreamVars.map(v => ({ expression: v.expression, label: v.label }));

  // Wire the in-editor Run button to the existing step-test endpoint. We send the live
  // config as ConfigOverride so the test reflects whatever the user is editing right now,
  // even if they haven't saved the workflow yet.
  const canRun = !!workflowId && !!stepId;
  const runStepTest = useCallback(async (): Promise<StepTestResult> => {
    if (!workflowId || !stepId) {
      return { success: false, output: null, errorOutput: null, outputParameters: {}, durationMs: 0, errorMessage: t('config.runScript.stepNotIdentifiable') };
    }
    return api.post<StepTestResult>(`/workflows/${workflowId}/steps/${stepId}/test`, {
      configOverride: config,
    });
  }, [workflowId, stepId, config, t]);

  // AI script generation (streaming): the script types itself out live in the Monaco editor.
  const handleAiGenerate = useAiScriptStream({ workflowId, stepId, upstreamVars });

  return (
    <>
      <FieldGrid>
        <Field label={t('config.runScript.engineLabel')}>
          <select
            value={(config.engine as string) || 'auto'}
            onChange={(e) => onUpdate({ engine: e.target.value })}
            className="input-field"
          >
            <option value="auto">{t('config.runScript.engineAuto')}</option>
            <option value="pwsh">{t('config.runScript.enginePwsh')}</option>
            <option value="powershell">{t('config.runScript.enginePowerShell')}</option>
          </select>
        </Field>
        <SwitchField
          label={t('config.runScript.autoLogging')}
          ariaLabel={t('config.runScript.autoLogging')}
          checked={transcript}
          onChange={(checked) => onUpdate({ transcript: checked })}
          stateText={transcript ? t('config.runScript.autoLoggingOn') : t('config.runScript.autoLoggingOff')}
        />
      </FieldGrid>

      <Field label={t('config.runScript.successExitCodes')}>
        <input
          type="text"
          value={(config.successExitCodes as string) || ''}
          onChange={(e) => onUpdate({ successExitCodes: e.target.value || undefined })}
          className="input-field"
          placeholder={t('config.runScript.successExitCodesPlaceholder')}
        />
        <p className="text-[10px] font-label text-on-surface-variant leading-snug">
          <Trans
            i18nKey="config.runScript.successExitCodesHint"
            ns="properties"
            components={[
              <code key={0} />,
              <code key={1} />,
              <code key={2} />,
              <code key={3} />,
            ]}
          />
        </p>
      </Field>

      <Field label={t('config.runScript.scriptLabel')}>
        <CodeField
          language="powershell"
          value={script}
          onChange={(v) => onUpdate({ script: v })}
          upstreamVars={upstreamVars}
          upstreamRefs={upstreamRefs}
          minLines={12}
          onOpenFullscreen={() => setShowEditor(true)}
          placeholder={t('config.runScript.scriptEditorPlaceholder')}
        />
      </Field>

      <p className="text-[10px] font-label text-amber-900 dark:text-amber-300 bg-amber-50 dark:bg-amber-950/50 border border-amber-200 dark:border-amber-800/40 rounded-md px-2 py-1.5 leading-snug">
        <Trans
          i18nKey="config.runScript.quotingHint"
          ns="properties"
          components={[
            <strong key={0} className="font-semibold" />,
            <code key={1} className={CODE_HINT}>{`{{step.output}}`}</code>,
            <code key={2} className={CODE_HINT}>{'…'}</code>,
            <code key={3} className={CODE_HINT}>{`$x = {{step.output}}`}</code>,
            <code key={4} className={CODE_HINT}>{`$x = '{{step.output}}'`}</code>,
          ]}
        />
      </p>

      {transcript && (
        <p className="text-[10px] font-label text-on-surface-variant leading-snug">
          <Trans
            i18nKey="config.runScript.transcriptHint"
            ns="properties"
            components={[<code key={0} />]}
          />
        </p>
      )}

      <SwitchField
        label={t('config.runScript.isolated')}
        ariaLabel={t('config.runScript.isolated')}
        checked={isolated}
        disabled={!isLocalTarget}
        onChange={(checked) => onUpdate({ isolated: checked })}
        stateText={isolated ? t('config.runScript.isolatedOn') : t('config.runScript.isolatedOff')}
      />

      {!isLocalTarget && (
        <p className="text-[10px] font-label text-on-surface-variant leading-snug">
          {t('config.runScript.remoteIsolatedHint')}
        </p>
      )}

      {isolated && isLocalTarget && (
        <>
          <p className="text-[10px] font-label text-on-surface-variant leading-snug">
            {t('config.runScript.isolatedHint')}
          </p>
          <FieldGrid>
            <Field label={t('config.runScript.memoryLimit')}>
              <input
                type="number"
                min={0}
                value={(config.memoryLimitMb as number) || 0}
                onChange={(e) => {
                  const n = parseInt(e.target.value, 10);
                  onUpdate({ memoryLimitMb: Number.isFinite(n) && n > 0 ? n : undefined });
                }}
                className="input-field"
                placeholder={t('config.runScript.unlimitedPlaceholder')}
              />
            </Field>
            <Field label={t('config.runScript.maxProcesses')}>
              <input
                type="number"
                min={0}
                value={(config.maxProcesses as number) || 0}
                onChange={(e) => {
                  const n = parseInt(e.target.value, 10);
                  onUpdate({ maxProcesses: Number.isFinite(n) && n > 0 ? n : undefined });
                }}
                className="input-field"
                placeholder={t('config.runScript.unlimitedPlaceholder')}
              />
            </Field>
          </FieldGrid>
          <p className="text-[10px] font-label text-on-surface-variant leading-snug">
            {t('config.runScript.memoryLimitHint')}
          </p>
        </>
      )}

      {showEditor && (
        <Suspense fallback={null}>
          <ScriptEditorDialog
            value={script}
            onChange={(v) => onUpdate({ script: v })}
            onClose={() => setShowEditor(false)}
            availableVars={psVars}
            upstreamRefs={upstreamRefs}
            outputVariableName={outputVariableName}
            onRun={canRun ? runStepTest : undefined}
            onAiGenerate={handleAiGenerate}
          />
        </Suspense>
      )}
    </>
  );
}