import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { FieldGrid } from '../panelChrome';

/**
 * Config editor for the Power-Management activity (shutdown / restart / logoff / abort / hibernate).
 * Wraps <c>shutdown.exe</c> on the target host.
 *
 * `logoff`, `abort` and `hibernate` ignore delay/force/message — the UI hides those fields
 * in those modes so the inputs don't mislead users into thinking they take effect.
 */
export function PowerManagementConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const action = (config.action as string) || 'shutdown';
  const delaySeconds = (config.delaySeconds as number) ?? 0;
  const force = config.force !== false; // default true
  const message = (config.message as string) || '';
  const supportsDelay = action === 'shutdown' || action === 'restart';

  // Persist the visual default for `action` the moment the panel opens. Otherwise the
  // dropdown shows "Shutdown" but the saved JSON has no action key, and the run fails
  // with "'action' is required". Matches what the user actually sees.
  useEffect(() => {
    if (config.action === undefined) {
      onUpdate({ action: 'shutdown' });
    }
  }, [config.action, onUpdate]);

  return (
    <>
      {supportsDelay ? (
        <FieldGrid>
          <Field label={t('config.powerManagement.action')}>
            <select
              value={action}
              onChange={(e) => onUpdate({ action: e.target.value })}
              className="input-field"
            >
              <option value="shutdown">{t('config.powerManagement.actionShutdown')}</option>
              <option value="restart">{t('config.powerManagement.actionRestart')}</option>
              <option value="logoff">{t('config.powerManagement.actionLogoff')}</option>
              <option value="abort">{t('config.powerManagement.actionAbort')}</option>
              <option value="hibernate">{t('config.powerManagement.actionHibernate')}</option>
            </select>
          </Field>
          <Field label={t('config.powerManagement.delaySeconds')}>
            <input
              type="number"
              min={0}
              value={delaySeconds}
              onChange={(e) => onUpdate({ delaySeconds: Math.max(0, parseInt(e.target.value, 10) || 0) })}
              className="input-field"
            />
          </Field>
        </FieldGrid>
      ) : (
        <Field label={t('config.powerManagement.action')}>
          <select
            value={action}
            onChange={(e) => onUpdate({ action: e.target.value })}
            className="input-field"
          >
            <option value="shutdown">{t('config.powerManagement.actionShutdown')}</option>
            <option value="restart">{t('config.powerManagement.actionRestart')}</option>
            <option value="logoff">{t('config.powerManagement.actionLogoff')}</option>
            <option value="abort">{t('config.powerManagement.actionAbort')}</option>
            <option value="hibernate">{t('config.powerManagement.actionHibernate')}</option>
          </select>
        </Field>
      )}

      {supportsDelay && (
        <>
          <p className="text-[11px] text-on-surface-variant leading-snug">
            Bei Targets, auf denen NodePilot selbst läuft, empfiehlt sich ein Delay &gt; 0,
            damit der Step sauber zurückkehren kann bevor das OS herunterfährt.
          </p>

          <Field label={t('config.powerManagement.forceClose')}>
            <label className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                checked={force}
                onChange={(e) => onUpdate({ force: e.target.checked })}
              />
              <span>Schließe offene Anwendungen ohne Rückfrage (<code className="font-mono text-xs">/f</code>)</span>
            </label>
          </Field>

          <VariableInsertField
            label={t('config.powerManagement.message')}
            value={message}
            onChange={(v) => onUpdate({ message: v })}
            upstreamVars={upstreamVars}
            placeholder={t('config.powerManagement.messagePlaceholder')}
            multiline
            rows={2}
          />
        </>
      )}
    </>
  );
}
