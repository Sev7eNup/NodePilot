import { Trans, useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { FieldGrid } from '../panelChrome';

const ACTIONS = [
  { value: 'status', key: 'actionStatus' },
  { value: 'start', key: 'actionStart' },
  { value: 'stop', key: 'actionStop' },
  { value: 'restart', key: 'actionRestart' },
  { value: 'create', key: 'actionCreate' },
  { value: 'delete', key: 'actionDelete' },
  { value: 'setStartType', key: 'actionSetStartType' },
] as const;

const STARTUP_TYPES = [
  { value: 'Automatic', key: 'startupAutomatic' },
  { value: 'AutomaticDelayedStart', key: 'startupAutomaticDelayedStart' },
  { value: 'Manual', key: 'startupManual' },
  { value: 'Disabled', key: 'startupDisabled' },
] as const;

const CODE = 'bg-surface-high px-1 rounded';

export function ServiceManagementConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const action = (config.action as string) || 'status';

  return (
    <>
      <FieldGrid>
        <VariableInsertField
          label={t('config.serviceManagement.serviceName')}
          value={(config.serviceName as string) || ''}
          onChange={(v) => onUpdate({ serviceName: v })}
          upstreamVars={upstreamVars}
          placeholder="Spooler"
        />
        <Field label={t('config.serviceManagement.action')}>
          <select
            value={action}
            onChange={(e) => onUpdate({ action: e.target.value })}
            className="input-field"
          >
            {ACTIONS.map((a) => (
              <option key={a.value} value={a.value}>{t(`config.serviceManagement.${a.key}`)}</option>
            ))}
          </select>
        </Field>
      </FieldGrid>

      {action === 'delete' && (
        <div className="text-[11px] text-error font-semibold leading-snug">
          <Trans
            i18nKey="config.serviceManagement.deleteWarning"
            ns="properties"
            components={[<code key={0} className={CODE} />]}
          />
        </div>
      )}

      {action === 'create' && (
        <>
          <VariableInsertField
            label={t('config.serviceManagement.binaryPath')}
            value={(config.binaryPath as string) || ''}
            onChange={(v) => onUpdate({ binaryPath: v })}
            upstreamVars={upstreamVars}
            placeholder="C:\\Tools\\my-service.exe"
          />
          <VariableInsertField
            label={t('config.serviceManagement.displayName')}
            value={(config.displayName as string) || ''}
            onChange={(v) => onUpdate({ displayName: v })}
            upstreamVars={upstreamVars}
            placeholder="Mein Service"
          />
          <VariableInsertField
            label={t('config.serviceManagement.description')}
            value={(config.description as string) || ''}
            onChange={(v) => onUpdate({ description: v })}
            upstreamVars={upstreamVars}
            placeholder={t('config.serviceManagement.descriptionPlaceholder')}
          />
          <Field label={t('config.serviceManagement.startType')}>
            <select
              value={(config.startupType as string) || 'Automatic'}
              onChange={(e) => onUpdate({ startupType: e.target.value })}
              className="input-field"
            >
              {STARTUP_TYPES.map((st) => (
                <option key={st.value} value={st.value}>{t(`config.serviceManagement.${st.key}`)}</option>
              ))}
            </select>
          </Field>
          <div className="text-[11px] text-on-surface-variant leading-snug">
            <Trans
              i18nKey="config.serviceManagement.createHint"
              ns="properties"
              components={[
                <code key={0} className={CODE} />,
                <code key={1} className={CODE} />,
              ]}
            />
          </div>
        </>
      )}

      {action === 'setStartType' && (
        <Field label={t('config.serviceManagement.startType')}>
          <select
            value={(config.startupType as string) || 'Automatic'}
            onChange={(e) => onUpdate({ startupType: e.target.value })}
            className="input-field"
          >
            {STARTUP_TYPES.map((st) => (
              <option key={st.value} value={st.value}>{t(`config.serviceManagement.${st.key}`)}</option>
            ))}
          </select>
        </Field>
      )}
    </>
  );
}