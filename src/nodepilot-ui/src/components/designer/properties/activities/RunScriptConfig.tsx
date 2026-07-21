import { useState, useCallback, lazy, Suspense } from 'react';
import { Field, SwitchField, type ConfigProps } from '../shared';
import { CodeField, FieldGrid } from '../panelChrome';
import { api } from '../../../../api/client';
import { useAiScriptStream } from '../../../../hooks/useAiScriptStream';
import type { StepTestResult } from '../../../../types/api';

// Monaco editor pulls in ~1 MB of language tokenizer assets — lazy-loaded so the
// initial workflow editor bundle stays unchanged and the chunk lands only when
// the user actually clicks "Open Editor".
const ScriptEditorDialog = lazy(() => import('../../ScriptEditorDialog'));

export function RunScriptConfig({ config, onUpdate, upstreamVars = [], workflowId, stepId, outputVariableName, isLocalTarget = true }: Readonly<ConfigProps>) {
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
      return { success: false, output: null, errorOutput: null, outputParameters: {}, durationMs: 0, errorMessage: 'Step nicht identifizierbar (fehlende workflowId oder stepId).' };
    }
    return api.post<StepTestResult>(`/workflows/${workflowId}/steps/${stepId}/test`, {
      configOverride: config,
    });
  }, [workflowId, stepId, config]);

  // AI script generation (streaming): the script types itself out live in the Monaco editor.
  const handleAiGenerate = useAiScriptStream({ workflowId, stepId, upstreamVars });

  return (
    <>
      <FieldGrid>
        <Field label="Engine">
          <select
            value={(config.engine as string) || 'auto'}
            onChange={(e) => onUpdate({ engine: e.target.value })}
            className="input-field"
          >
            <option value="auto">Auto (PS7 → PS5.1)</option>
            <option value="pwsh">PowerShell 7</option>
            <option value="powershell">Windows PS 5.1</option>
          </select>
        </Field>
        <SwitchField
          label="Auto-Logging"
          ariaLabel="Auto-Logging"
          checked={transcript}
          onChange={(checked) => onUpdate({ transcript: checked })}
          stateText={transcript ? 'Transcript an' : 'Aus'}
        />
      </FieldGrid>

      <Field label="Erfolg bei Exit-Codes">
        <input
          type="text"
          value={(config.successExitCodes as string) || ''}
          onChange={(e) => onUpdate({ successExitCodes: e.target.value || undefined })}
          className="input-field"
          placeholder="leer = nur fehler-basiert (sonst z. B. 0,1)"
        />
        <p className="text-[10px] font-label text-on-surface-variant leading-snug">
          Leer: Step scheitert nur bei einem PowerShell-Fehler/<code>throw</code> — ein <code>exit N</code> zählt
          nicht als Fehler. Sonst komma-separiert → Erfolg nur bei diesen Exit-Codes. Greift in allen Engines auf
          den letzten Native-Command-Code (<code>$LASTEXITCODE</code>); ein script-eigenes <code>exit N</code> nur
          im Prozess/isoliert-Modus.
        </p>
      </Field>

      <Field label="PowerShell Script">
        <CodeField
          language="powershell"
          value={script}
          onChange={(v) => onUpdate({ script: v })}
          upstreamVars={upstreamVars}
          upstreamRefs={upstreamRefs}
          minLines={12}
          onOpenFullscreen={() => setShowEditor(true)}
          placeholder="# Write-Output 'hello'"
        />
      </Field>

      <p className="text-[10px] font-label text-amber-900 dark:text-amber-300 bg-amber-50 dark:bg-amber-950/50 border border-amber-200 dark:border-amber-800/40 rounded-md px-2 py-1.5 leading-snug">
        <strong className="font-semibold">Hinweis:</strong>{' '}
        <code className="bg-amber-100/80 dark:bg-amber-900/40 px-1 rounded">{'{{step.output}}'}</code> wird als
        PowerShell-Single-Quoted-String <code className="bg-amber-100/80 dark:bg-amber-900/40 px-1 rounded">'…'</code> eingebettet —
        also <code className="bg-amber-100/80 dark:bg-amber-900/40 px-1 rounded">$x = {'{{step.output}}'}</code>, nicht{' '}
        <code className="bg-amber-100/80 dark:bg-amber-900/40 px-1 rounded">$x = '{'{{step.output}}'}'</code>.
      </p>

      {transcript && (
        <p className="text-[10px] font-label text-on-surface-variant leading-snug">
          Per <code>Start-Transcript</code>: zeilenweises Protokoll der Cmdlets + Outputs als separates
          „Transcript"-Feld in der Step-Ansicht. Volumen-intensiv — nur bei Bedarf.
        </p>
      )}

      <SwitchField
        label="Prozess-Isolation"
        ariaLabel="Prozess-Isolation"
        checked={isolated}
        disabled={!isLocalTarget}
        onChange={(checked) => onUpdate({ isolated: checked })}
        stateText={isolated ? 'Isolierter Prozess' : 'Aus'}
      />

      {!isLocalTarget && (
        <p className="text-[10px] font-label text-on-surface-variant leading-snug">
          Prozess-Isolation gilt nur für lokale Ausführung. Remote-Steps laufen außerhalb des
          NodePilot-Hosts via WinRM (eigene Session, aber ohne Job-Object-Limits) — das Backend
          entscheidet final.
        </p>
      )}

      {isolated && isLocalTarget && (
        <>
          <p className="text-[10px] font-label text-on-surface-variant leading-snug">
            Läuft in einem separaten Prozess (kein In-Process-Pool) — etwas langsamer, dafür
            Crash-/Leak-Containment: ein abstürzendes Skript reißt den Orchestrator nicht mit, und
            beim Host-Neustart bleiben keine verwaisten Prozesse zurück.
          </p>
          <FieldGrid>
            <Field label="Speicherlimit (MB)">
              <input
                type="number"
                min={0}
                value={(config.memoryLimitMb as number) || 0}
                onChange={(e) => {
                  const n = parseInt(e.target.value, 10);
                  onUpdate({ memoryLimitMb: Number.isFinite(n) && n > 0 ? n : undefined });
                }}
                className="input-field"
                placeholder="0 = unbegrenzt"
              />
            </Field>
            <Field label="Max. Prozesse">
              <input
                type="number"
                min={0}
                value={(config.maxProcesses as number) || 0}
                onChange={(e) => {
                  const n = parseInt(e.target.value, 10);
                  onUpdate({ maxProcesses: Number.isFinite(n) && n > 0 ? n : undefined });
                }}
                className="input-field"
                placeholder="0 = unbegrenzt"
              />
            </Field>
          </FieldGrid>
          <p className="text-[10px] font-label text-on-surface-variant leading-snug">
            Speicherlimit lässt Allokationen fehlschlagen (Skript sieht OutOfMemory), erzwingt keine
            sofortige Terminierung.
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
