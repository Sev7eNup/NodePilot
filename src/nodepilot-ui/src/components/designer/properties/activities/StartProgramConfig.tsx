import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';
import { resolveKnownLauncher } from '../../../../lib/knownProgramLaunchers';

export function StartProgramConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const useShell = (config.useShellExecute as boolean) || false;
  const waitForExit = config.waitForExit !== false;
  // The engine needs an absolute path — it never searches the target's PATH. Complete the few
  // unambiguous launcher names on blur rather than while typing, so the field does not rewrite
  // itself mid-keystroke.
  const completeLauncher = (value: string) => {
    const resolved = resolveKnownLauncher(value);
    if (resolved) onUpdate({ filePath: resolved });
  };
  return (
    <>
      <VariableInsertField
        label={t('config.startProgram.filePath')}
        value={(config.filePath as string) || ''}
        onChange={(v) => onUpdate({ filePath: v })}
        onBlur={completeLauncher}
        upstreamVars={upstreamVars}
        placeholder={'C:\\Program Files\\7-Zip\\7z.exe'}
        mono
      />
      <VariableInsertField
        label={t('config.startProgram.arguments')}
        value={(config.arguments as string) || ''}
        onChange={(v) => onUpdate({ arguments: v })}
        upstreamVars={upstreamVars}
        multiline
        rows={2}
        placeholder="a -tzip archive.zip *.txt"
        mono
      />
      <VariableInsertField
        label={t('config.startProgram.workingDirectory')}
        value={(config.workingDirectory as string) || ''}
        onChange={(v) => onUpdate({ workingDirectory: v })}
        upstreamVars={upstreamVars}
        placeholder={'C:\\Temp'}
        mono
      />
      <Field label="">
        <label className="flex items-start gap-2 cursor-pointer select-none py-1">
          <input
            type="checkbox"
            checked={useShell}
            onChange={(e) => onUpdate({ useShellExecute: e.target.checked })}
            className="mt-0.5 w-4 h-4 rounded border-outline-variant accent-primary"
          />
          <div className="flex-1">
            <div className="text-sm font-medium text-on-surface">{t('config.startProgram.useShellExecute')}</div>
            <div className="text-[11px] text-on-surface-variant leading-snug">
              {t(useShell
                ? 'config.startProgram.useShellExecuteOnHint'
                : 'config.startProgram.useShellExecuteOffHint')}
            </div>
          </div>
        </label>
      </Field>
      <Field label="">
        <label className="flex items-start gap-2 cursor-pointer select-none py-1">
          <input
            type="checkbox"
            checked={waitForExit}
            onChange={(e) => onUpdate({ waitForExit: e.target.checked })}
            className="mt-0.5 w-4 h-4 rounded border-outline-variant accent-primary"
          />
          <div className="flex-1">
            <div className="text-sm font-medium text-on-surface">
              {t(waitForExit ? 'config.startProgram.waitForExitOn' : 'config.startProgram.waitForExitOff')}
            </div>
            <div className="text-[11px] text-on-surface-variant leading-snug">
              {t(waitForExit
                ? 'config.startProgram.waitForExitOnHint'
                : 'config.startProgram.waitForExitOffHint')}
            </div>
          </div>
        </label>
      </Field>
      {waitForExit && (
        <Field label={t('config.startProgram.successExitCodes')}>
          <input
            type="text"
            value={(config.successExitCodes as string) || '0'}
            onChange={(e) => onUpdate({ successExitCodes: e.target.value })}
            className="input-field font-mono text-sm"
            placeholder="0"
          />
        </Field>
      )}
    </>
  );
}
