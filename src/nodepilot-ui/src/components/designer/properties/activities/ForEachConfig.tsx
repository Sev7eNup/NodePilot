import { FolderTree } from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { ParameterTable } from '../ParameterTable';

export function ForEachConfig({ config, onUpdate, upstreamVars = [], onOpenWorkflowPicker }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const childWorkflowNameOrId = (config.childWorkflowNameOrId as string) || '';
  const items = (config.items as string) || '';
  const itemsFormat = (config.itemsFormat as string) || 'auto';
  const itemParameterName = (config.itemParameterName as string) || 'item';
  const indexParameterName = (config.indexParameterName as string) || 'index';
  const maxParallelism = (config.maxParallelism as number) ?? 1;
  const continueOnError = config.continueOnError === true;
  const timeoutSecondsPerItem = (config.timeoutSecondsPerItem as number) || 3600;
  const parameters = (config.parameters as Record<string, string>) || {};

  return (
    <>
      <VariableInsertField
        label={t('config.forEach.itemsLabel')}
        value={items}
        onChange={(v) => onUpdate({ items: v })}
        upstreamVars={upstreamVars}
        placeholder={t('config.forEach.itemsPlaceholder', {
          example: '{{inventory.output}}',
          array: '["host1","host2","host3"]',
        })}
        multiline
        rows={3}
        mono
      />

      <Field label={t('config.forEach.itemsFormat')}>
        <select
          value={itemsFormat}
          onChange={(e) => onUpdate({ itemsFormat: e.target.value })}
          className="input-field"
        >
          <option value="auto">{t('config.forEach.itemsFormatAuto')}</option>
          <option value="json">{t('config.forEach.itemsFormatJson')}</option>
          <option value="lines">{t('config.forEach.itemsFormatLines')}</option>
        </select>
      </Field>

      {onOpenWorkflowPicker && (
        <button
          type="button"
          onClick={onOpenWorkflowPicker}
          className="flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-primary-fixed text-primary hover:bg-primary-fixed-dim text-xs font-label font-semibold transition-colors w-full justify-center"
          title={t('config.forEach.workflowPickerTitle')}
        >
          <FolderTree size={13} />
          {t('config.forEach.workflowPicker')}
        </button>
      )}

      <VariableInsertField
        label={t('config.forEach.childWorkflow')}
        value={childWorkflowNameOrId}
        onChange={(v) => onUpdate({ childWorkflowNameOrId: v })}
        upstreamVars={upstreamVars}
        placeholder={t('config.forEach.childWorkflowPlaceholder')}
      />

      <div className="grid grid-cols-2 gap-2">
        <Field label={t('config.forEach.itemParameter')}>
          <input
            type="text"
            value={itemParameterName}
            onChange={(e) => onUpdate({ itemParameterName: e.target.value })}
            className="input-field font-mono text-sm"
            placeholder="item"
          />
        </Field>
        <Field label={t('config.forEach.indexParameter')}>
          <input
            type="text"
            value={indexParameterName}
            onChange={(e) => onUpdate({ indexParameterName: e.target.value })}
            className="input-field font-mono text-sm"
            placeholder="index"
          />
        </Field>
      </div>
      <div className="text-[11px] text-on-surface-variant leading-snug -mt-2">
        {t('config.forEach.childParamHintPrefix')}{' '}
        <code className="bg-surface-high px-1 rounded">{`{{manual.${itemParameterName || 'item'}}}`}</code>
        {' '}{t('config.forEach.childParamHintMiddle')}{' '}
        <code className="bg-surface-high px-1 rounded">{`{{manual.${indexParameterName || 'index'}}}`}</code>
        {' '}{t('config.forEach.childParamHintSuffix')}
      </div>

      <div className="grid grid-cols-2 gap-2">
        <Field label={t('config.forEach.parallelism')}>
          <input
            type="number"
            value={maxParallelism}
            onChange={(e) => onUpdate({ maxParallelism: parseInt(e.target.value) || 1 })}
            className="input-field"
            min={0}
            max={64}
          />
        </Field>
        <Field label={t('config.forEach.timeoutPerItem')}>
          <input
            type="number"
            value={timeoutSecondsPerItem}
            onChange={(e) => onUpdate({ timeoutSecondsPerItem: parseInt(e.target.value) || 3600 })}
            className="input-field"
            min={1}
          />
        </Field>
      </div>
      <div className="text-[11px] text-on-surface-variant leading-snug -mt-2">
        {t('config.forEach.parallelismHintPrefix')}{' '}
        <code className="bg-surface-high px-1 rounded">0</code>
        {' '}{t('config.forEach.parallelismHintMiddle')}{' '}
        <code className="bg-surface-high px-1 rounded mx-1">1</code>
        {' '}{t('config.forEach.parallelismHintSuffix')}
      </div>

      <Field label={t('config.forEach.errorBehavior')}>
        <label className="flex items-start gap-2 cursor-pointer select-none py-1">
          <input
            type="checkbox"
            checked={continueOnError}
            onChange={(e) => onUpdate({ continueOnError: e.target.checked })}
            className="mt-0.5 w-4 h-4 rounded border-outline-variant accent-primary"
          />
          <div className="flex-1">
            <div className="text-sm font-medium text-on-surface">
              {continueOnError ? t('config.forEach.continueOnError') : t('config.forEach.failFast')}
            </div>
            <div className="text-[11px] text-on-surface-variant leading-snug">
              {continueOnError ? (
                <>
                  {t('config.forEach.continueOnErrorHintPrefix')}{' '}
                  <code className="bg-surface-high px-1 rounded">{'{{step.param.failed}}'}</code>.
                </>
              ) : (
                t('config.forEach.failFastHint')
              )}
            </div>
          </div>
        </label>
      </Field>

      <ParameterTable
        label={t('config.forEach.additionalParameters')}
        emptyMessage={t('config.forEach.noStaticParameters')}
        parameters={parameters}
        onChange={(next) => onUpdate({ parameters: next })}
        upstreamVars={upstreamVars}
      />
    </>
  );
}
