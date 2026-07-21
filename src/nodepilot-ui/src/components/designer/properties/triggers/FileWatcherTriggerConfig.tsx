import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { FieldGrid } from '../panelChrome';

export function FileWatcherTriggerConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('triggers');
  return (
    <>
      <VariableInsertField
        label={t('fileWatcherTrigger.directory')}
        value={(config.directory as string) || ''}
        onChange={(v) => onUpdate({ directory: v })}
        upstreamVars={upstreamVars}
        placeholder="C:\Data\Incoming"
      />
      <FieldGrid>
        <Field label={t('fileWatcherTrigger.fileFilter')}>
          <input
            type="text"
            value={(config.filter as string) || '*.*'}
            onChange={(e) => onUpdate({ filter: e.target.value })}
            className="input-field"
            placeholder="*.csv, *.xml, *.txt"
          />
        </Field>
        <Field label={t('fileWatcherTrigger.watchType')}>
          <select
            value={(config.watchType as string) || 'created'}
            onChange={(e) => onUpdate({ watchType: e.target.value })}
            className="input-field"
          >
            <option value="created">{t('fileWatcherTrigger.fileCreated')}</option>
            <option value="changed">{t('fileWatcherTrigger.fileChanged')}</option>
            <option value="deleted">{t('fileWatcherTrigger.fileDeleted')}</option>
            <option value="renamed">{t('fileWatcherTrigger.fileRenamed')}</option>
            <option value="any">{t('fileWatcherTrigger.allChanges')}</option>
          </select>
        </Field>
      </FieldGrid>
      <Field label="">
        <label className="flex items-center gap-2 text-sm font-label text-on-surface-variant">
          <input
            type="checkbox"
            checked={(config.includeSubdirectories as boolean) || false}
            onChange={(e) => onUpdate({ includeSubdirectories: e.target.checked })}
            className="rounded"
          />
          {t('fileWatcherTrigger.includeSubdirectories')}
        </label>
      </Field>
    </>
  );
}
