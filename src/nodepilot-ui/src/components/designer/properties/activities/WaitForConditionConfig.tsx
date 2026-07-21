import { useTranslation } from 'react-i18next';
import { Field, VariableInsertField, type ConfigProps } from '../shared';

type ConditionType = 'script' | 'pathExists' | 'serviceRunning' | 'portOpen' | 'httpOk';

/**
 * Config editor for the Wait-For-Condition activity.
 *
 * Since 2026-05-17 the activity supports four typed sub-modes in addition to the classic
 * `script` mode. The typed modes accept `{{upstream}}` templates in their fields — the
 * engine quotes them safely before passing them to PowerShell. The classic `script` mode
 * still forbids templates (to prevent injection).
 */
export function WaitForConditionConfig({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const conditionType = ((config.conditionType as ConditionType) || 'script');
  const intervalSeconds = (config.intervalSeconds as number) ?? 5;

  return (
    <>
      <Field label="Bedingungstyp">
        <select
          value={conditionType}
          onChange={(e) => onUpdate({ conditionType: e.target.value as ConditionType })}
          className="input-field"
        >
          <option value="script">Free-form PowerShell-Ausdruck (keine Templates)</option>
          <option value="pathExists">Datei/Ordner existiert (Test-Path)</option>
          <option value="serviceRunning">Service läuft (Get-Service)</option>
          <option value="portOpen">TCP-Port offen (TcpClient)</option>
          <option value="httpOk">HTTP 2xx (Invoke-WebRequest)</option>
        </select>
        <p className="text-[11px] text-on-surface-variant mt-1">
          Getypte Modi akzeptieren <code className="font-mono">{'{{upstream.param.x}}'}</code> in den Feldern darunter —
          die Engine quotet die Werte vor dem PS-Aufruf. Der Script-Modus ist frei, verbietet aber Templates
          (sonst Injection-Vektor).
        </p>
      </Field>

      {conditionType === 'script' && (
        <ScriptModeFields config={config} onUpdate={onUpdate} upstreamVars={upstreamVars} />
      )}
      {conditionType === 'pathExists' && (
        <VariableInsertField
          label="Pfad"
          value={(config.path as string) || ''}
          onChange={(v) => onUpdate({ path: v })}
          upstreamVars={upstreamVars}
          placeholder="C:\\path\\to\\flag.txt — oder {{prep.param.flagPath}}"
        />
      )}
      {conditionType === 'serviceRunning' && (
        <VariableInsertField
          label="Service-Name"
          value={(config.serviceName as string) || ''}
          onChange={(v) => onUpdate({ serviceName: v })}
          upstreamVars={upstreamVars}
          placeholder="Spooler"
        />
      )}
      {conditionType === 'portOpen' && (
        <>
          <VariableInsertField
            label="Host"
            value={(config.host as string) || ''}
            onChange={(v) => onUpdate({ host: v })}
            upstreamVars={upstreamVars}
            placeholder="db.internal — oder {{discover.param.host}}"
          />
          <Field label="Port (1..65535)">
            <input
              type="number"
              min={1}
              max={65535}
              value={(config.port as number) ?? 443}
              onChange={(e) => onUpdate({ port: Math.max(1, Math.min(65535, parseInt(e.target.value, 10) || 443)) })}
              className="input-field"
            />
          </Field>
        </>
      )}
      {conditionType === 'httpOk' && (
        <VariableInsertField
          label="URL"
          value={(config.url as string) || ''}
          onChange={(v) => onUpdate({ url: v })}
          upstreamVars={upstreamVars}
          placeholder="https://api.internal/healthz"
        />
      )}

      <Field label={t('config.waitForCondition.pollInterval')}>
        <input
          type="number"
          min={1}
          value={intervalSeconds}
          onChange={(e) => onUpdate({ intervalSeconds: Math.max(1, parseInt(e.target.value, 10) || 5) })}
          className="input-field"
        />
        <p className="text-[11px] text-on-surface-variant mt-1">
          Abstand zwischen zwei Polls. Timeout (Gesamtlaufzeit) wird oben über das <em>Timeout</em>-Feld
          gesetzt — Default 300 s.
        </p>
      </Field>
    </>
  );
}

function ScriptModeFields({ config, onUpdate, upstreamVars = [] }: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const script = (config.script as string) || '';
  const snippets: Array<{ label: string; script: string }> = [
    { label: t('config.waitForCondition.snippetServiceRunning'), script: `(Get-Service -Name 'Spooler').Status -eq 'Running'` },
    { label: t('config.waitForCondition.snippetPortOpen'), script: `& { $c = [System.Net.Sockets.TcpClient]::new(); try { $t = $c.ConnectAsync('example.com', 443); $t.Wait(1500) -and $c.Connected } catch { $false } finally { $c.Close() } }` },
    { label: t('config.waitForCondition.snippetFileExists'), script: `Test-Path 'C:\\path\\to\\flag.txt'` },
    { label: t('config.waitForCondition.snippetHttpOk'), script: `& { try { (Invoke-WebRequest -Uri 'https://example.com/health' -UseBasicParsing -TimeoutSec 5).StatusCode -lt 300 } catch { $false } }` },
  ];

  return (
    <>
      <VariableInsertField
        label={t('config.waitForCondition.conditionScriptLabel')}
        value={script}
        onChange={(v) => onUpdate({ script: v })}
        upstreamVars={upstreamVars}
        multiline
        rows={4}
        mono
        placeholder={t('config.waitForCondition.conditionScriptPlaceholder')}
      />
      <p className="text-[11px] text-on-surface-variant -mt-2">
        Templates wie <code className="font-mono">{'{{step.param.x}}'}</code> sind hier <strong>nicht</strong> erlaubt
        — verwende die getypten Modi oben oder baue den Wert in einem vorgelagerten <em>runScript</em>-Step zusammen.
      </p>

      <div className="flex flex-wrap gap-1.5">
        {snippets.map((s) => (
          <button
            key={s.label}
            type="button"
            onClick={() => onUpdate({ script: s.script })}
            className="text-[10px] font-label px-2 py-0.5 rounded bg-surface-high hover:bg-surface-highest text-on-surface-variant transition-colors"
            title={s.script}
          >
            + {s.label}
          </button>
        ))}
      </div>
    </>
  );
}
