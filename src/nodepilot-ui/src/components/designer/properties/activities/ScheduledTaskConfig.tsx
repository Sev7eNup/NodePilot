import { useEffect } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';

const ACTIONS = [
  { value: 'get', key: 'actionGet' },
  { value: 'start', key: 'actionStart' },
  { value: 'stop', key: 'actionStop' },
  { value: 'enable', key: 'actionEnable' },
  { value: 'disable', key: 'actionDisable' },
  { value: 'register', key: 'actionRegister' },
  { value: 'unregister', key: 'actionUnregister' },
] as const;

const TRIGGER_TYPES = [
  { value: 'once', key: 'triggerOnce' },
  { value: 'daily', key: 'triggerDaily' },
  { value: 'weekly', key: 'triggerWeekly' },
  { value: 'atLogon', key: 'triggerAtLogon' },
  { value: 'atStartup', key: 'triggerAtStartup' },
] as const;

const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as const;

const RUN_LEVELS = [
  { value: 'limited', key: 'runLevelLimited' },
  { value: 'highest', key: 'runLevelHighest' },
] as const;

export function ScheduledTaskConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const action = (config.action as string) || 'get';
  const triggerType = (config.triggerType as string) || 'daily';
  const daysOfWeek = (config.daysOfWeek as string[]) || [];
  const force = config.force === true;
  const weekdayShort = t('config.scheduledTask.weekdayShort', { returnObjects: true }) as string[];

  // Persist the visual default for `action` the moment the panel opens. Otherwise the
  // dropdown shows "Get / Status" but the saved JSON has no action key, and the run
  // fails with "unknown action ''". This matches what the user actually sees.
  useEffect(() => {
    if (config.action === undefined) {
      onUpdate({ action: 'get' });
    }
  }, [config.action, onUpdate]);

  // Same fix for triggerType: the register branch requires a triggerType, and the UI
  // shows the daily trigger as visual default. Without persistence the run would fail
  // with "unknown triggerType ''" once the user picks 'register' without touching the
  // trigger-type dropdown.
  useEffect(() => {
    if (action === 'register' && config.triggerType === undefined) {
      onUpdate({ triggerType: 'daily' });
    }
  }, [action, config.triggerType, onUpdate]);

  const toggleDay = (day: string) => {
    const next = daysOfWeek.includes(day)
      ? daysOfWeek.filter((d) => d !== day)
      : [...daysOfWeek, day];
    onUpdate({ daysOfWeek: next });
  };

  return (
    <>
      <VariableInsertField
        label={t('config.scheduledTask.taskName')}
        value={(config.taskName as string) || ''}
        onChange={(v) => onUpdate({ taskName: v })}
        upstreamVars={upstreamVars}
        placeholder="MyNightlyJob"
      />
      <VariableInsertField
        label={t('config.scheduledTask.taskPath')}
        value={(config.taskPath as string) || '\\'}
        onChange={(v) => onUpdate({ taskPath: v })}
        upstreamVars={upstreamVars}
        placeholder="\\"
      />
      <Field label={t('config.scheduledTask.action')}>
        <select
          value={action}
          onChange={(e) => onUpdate({ action: e.target.value })}
          className="input-field"
        >
          {ACTIONS.map((a) => (
            <option key={a.value} value={a.value}>{t(`config.scheduledTask.${a.key}`)}</option>
          ))}
        </select>
      </Field>

      {action === 'unregister' && (
        <div className="text-[11px] text-error font-semibold leading-snug">
          {t('config.scheduledTask.unregisterWarning')}
        </div>
      )}

      {action === 'register' && (
        <>
          <VariableInsertField
            label={t('config.scheduledTask.program')}
            value={(config.program as string) || ''}
            onChange={(v) => onUpdate({ program: v })}
            upstreamVars={upstreamVars}
            placeholder="C:\\Tools\\my.exe"
          />
          <VariableInsertField
            label={t('config.scheduledTask.arguments')}
            value={(config.arguments as string) || ''}
            onChange={(v) => onUpdate({ arguments: v })}
            upstreamVars={upstreamVars}
            placeholder="-quiet -log C:\\Logs\\out.log"
          />
          <VariableInsertField
            label={t('config.scheduledTask.workingDirectory')}
            value={(config.workingDirectory as string) || ''}
            onChange={(v) => onUpdate({ workingDirectory: v })}
            upstreamVars={upstreamVars}
            placeholder="C:\\Tools"
          />

          <Field label={t('config.scheduledTask.triggerType')}>
            <select
              value={triggerType}
              onChange={(e) => onUpdate({ triggerType: e.target.value })}
              className="input-field"
            >
              {TRIGGER_TYPES.map((tr) => (
                <option key={tr.value} value={tr.value}>{t(`config.scheduledTask.${tr.key}`)}</option>
              ))}
            </select>
          </Field>

          {(triggerType === 'once' || triggerType === 'daily' || triggerType === 'weekly') && (
            <VariableInsertField
              label={triggerType === 'once'
                ? t('config.scheduledTask.startOnce')
                : t('config.scheduledTask.startTime')}
              value={(config.startTime as string) || ''}
              onChange={(v) => onUpdate({ startTime: v })}
              upstreamVars={upstreamVars}
              placeholder={triggerType === 'once' ? '2026-05-01T03:00:00' : '03:00'}
            />
          )}

          {triggerType === 'daily' && (
            <Field label={t('config.scheduledTask.intervalDays')}>
              <input
                type="number"
                min={1}
                value={(config.daysInterval as number) || 1}
                onChange={(e) => onUpdate({ daysInterval: parseInt(e.target.value) || 1 })}
                className="input-field"
              />
            </Field>
          )}

          {triggerType === 'weekly' && (
            <>
              <Field label={t('config.scheduledTask.weekdays')}>
                <div className="flex flex-wrap gap-1">
                  {DAYS.map((d, i) => {
                    const active = daysOfWeek.includes(d);
                    return (
                      <button
                        key={d}
                        type="button"
                        onClick={() => toggleDay(d)}
                        className={`px-2.5 py-1 rounded-md text-xs font-label font-semibold transition-colors ${
                          active
                            ? 'bg-primary/15 text-primary'
                            : 'bg-surface-high hover:bg-surface-highest text-on-surface-variant'
                        }`}
                      >
                        {weekdayShort[i] ?? d.slice(0, 2)}
                      </button>
                    );
                  })}
                </div>
              </Field>
              <Field label={t('config.scheduledTask.intervalWeeks')}>
                <input
                  type="number"
                  min={1}
                  value={(config.weeksInterval as number) || 1}
                  onChange={(e) => onUpdate({ weeksInterval: parseInt(e.target.value) || 1 })}
                  className="input-field"
                />
              </Field>
            </>
          )}

          <VariableInsertField
            label={t('config.scheduledTask.runAsUser')}
            value={(config.runAsUser as string) || 'SYSTEM'}
            onChange={(v) => onUpdate({ runAsUser: v })}
            upstreamVars={upstreamVars}
            placeholder="SYSTEM / NETWORK SERVICE / DOMAIN\\User"
          />
          <Field label={t('config.scheduledTask.runLevel')}>
            <select
              value={(config.runLevel as string) || 'limited'}
              onChange={(e) => onUpdate({ runLevel: e.target.value })}
              className="input-field"
            >
              {RUN_LEVELS.map((r) => (
                <option key={r.value} value={r.value}>{t(`config.scheduledTask.${r.key}`)}</option>
              ))}
            </select>
          </Field>
          <VariableInsertField
            label={t('config.scheduledTask.description')}
            value={(config.description as string) || ''}
            onChange={(v) => onUpdate({ description: v })}
            upstreamVars={upstreamVars}
            placeholder={t('config.scheduledTask.descriptionPlaceholder')}
          />

          <Field label={t('config.scheduledTask.force')}>
            <label className="flex items-start gap-2 cursor-pointer select-none py-1">
              <input
                type="checkbox"
                checked={force}
                onChange={(e) => onUpdate({ force: e.target.checked })}
                className="mt-0.5 w-4 h-4 rounded border-outline-variant accent-primary"
              />
              <div className="flex-1 text-sm text-on-surface">
                {force ? t('config.scheduledTask.forceOn') : t('config.scheduledTask.forceOff')}
              </div>
            </label>
          </Field>

          <div className="text-[11px] text-on-surface-variant leading-snug">
            <Trans
              i18nKey="config.scheduledTask.runAsHint"
              ns="properties"
              components={[
                <code key={0} className="bg-surface-high px-1 rounded" />,
                <code key={1} className="bg-surface-high px-1 rounded" />,
                <code key={2} className="bg-surface-high px-1 rounded" />,
              ]}
            />
          </div>
        </>
      )}

      {action === 'get' && (
        <div className="text-[11px] text-on-surface-variant leading-snug">
          <Trans
            i18nKey="config.scheduledTask.getOutputHint"
            ns="properties"
            components={[
              <code key={0} className="bg-surface-high px-1 rounded">{`{{step.param.state}}`}</code>,
              <code key={1} className="bg-surface-high px-1 rounded">{`{{step.param.lastTaskResult}}`}</code>,
              <code key={2} className="bg-surface-high px-1 rounded">{`{{step.param.nextRunTime}}`}</code>,
            ]}
          />
        </div>
      )}
    </>
  );
}