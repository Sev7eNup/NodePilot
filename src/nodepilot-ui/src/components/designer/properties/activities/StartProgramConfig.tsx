import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';

export function StartProgramConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const useShell = (config.useShellExecute as boolean) || false;
  const waitForExit = config.waitForExit !== false;
  return (
    <>
      <VariableInsertField
        label={t('config.startProgram.filePath')}
        value={(config.filePath as string) || ''}
        onChange={(v) => onUpdate({ filePath: v })}
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
              {useShell
                ? 'Über OS-Shell starten — nötig für Dateiassoziationen (.xlsx, .pdf) und UI-Apps. Stdout/Stderr werden NICHT eingefangen.'
                : 'Direkt starten mit stdout/stderr-Capture. Für Konsolen-Programme (7z.exe, robocopy, powershell.exe).'}
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
              {waitForExit ? 'Auf Beendigung warten' : 'Fire-and-forget'}
            </div>
            <div className="text-[11px] text-on-surface-variant leading-snug">
              {waitForExit
                ? 'Step blockiert bis Prozess fertig. ExitCode + Output stehen downstream zur Verfügung.'
                : 'Prozess wird gestartet, Step succeedet sofort. Nur PID wird zurückgegeben.'}
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
