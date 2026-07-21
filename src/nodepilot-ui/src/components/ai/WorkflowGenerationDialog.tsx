import { ChevronDown, ChevronRight, CircleDash, Close, Copy, MagicWandFilled } from '@carbon/icons-react';
import { useState, useCallback, useEffect, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { aiApi, type GenerateWorkflowResponse } from '../../api/ai';

interface Props {
  /** Posts the generated definition to /api/workflows. Caller closes the dialog and navigates on success. */
  onCreate: (req: { name: string; description: string; definitionJson: string }) => Promise<void>;
  onClose: () => void;
}

type Stage = 'prompt' | 'preview';

/**
 * Two-stage dialog for AI workflow generation. Stage 1 collects the prompt, stage 2
 * shows the workflow returned by the LLM as a preview (editable name + description,
 * stats, raw JSON) — once the user confirms, the workflow is persisted through the
 * existing <c>POST /api/workflows</c> mutation. Deliberately NO React Flow render in
 * the preview (risk of double-mounting the canvas, plus bundle cost) — the stats are
 * enough, and the full graph is just one click away in the editor.
 */
export function WorkflowGenerationDialog({ onCreate, onClose }: Readonly<Props>) {
  const { t } = useTranslation(['ai', 'common']);
  const [stage, setStage] = useState<Stage>('prompt');
  const [prompt, setPrompt] = useState('');
  const [generating, setGenerating] = useState(false);
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [generated, setGenerated] = useState<GenerateWorkflowResponse | null>(null);
  const [editedName, setEditedName] = useState('');
  const [editedDescription, setEditedDescription] = useState('');
  const [showJson, setShowJson] = useState(false);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);

  useEffect(() => { textareaRef.current?.focus(); }, []);

  const handleGenerate = useCallback(async () => {
    const trimmed = prompt.trim();
    if (!trimmed || generating) return;
    setError(null);
    setGenerating(true);
    try {
      const resp = await aiApi.generateWorkflow({ prompt: trimmed });
      setGenerated(resp);
      setEditedName(resp.suggestedName);
      setEditedDescription(resp.suggestedDescription ?? '');
      setStage('preview');
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(msg);
    } finally {
      setGenerating(false);
    }
  }, [prompt, generating]);

  const handleCreate = useCallback(async () => {
    if (!generated || creating) return;
    const trimmedName = editedName.trim();
    if (!trimmedName) {
      setError('Name darf nicht leer sein.');
      return;
    }
    setError(null);
    setCreating(true);
    try {
      await onCreate({
        name: trimmedName,
        description: editedDescription.trim(),
        definitionJson: generated.definitionJson,
      });
      // The caller closes the dialog and navigates away. If onCreate resolves without
      // us being closed, we leave the state as-is — there's no success indicator to
      // show from this path.
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(msg);
      setCreating(false);
    }
  }, [generated, editedName, editedDescription, onCreate, creating]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (e.key === 'Escape' && !generating && !creating) onClose();
    if (stage === 'prompt' && e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      handleGenerate();
    }
  }, [stage, generating, creating, onClose, handleGenerate]);

  // Activity histogram for the stats row. Parsing is defensive — if the JSON is
  // malformed (shouldn't happen after server-side validation), we just show 0.
  const activityHistogram = useMemo<Array<{ type: string; count: number }>>(() => {
    if (!generated) return [];
    try {
      const parsed = JSON.parse(generated.definitionJson) as { nodes?: Array<{ data?: { activityType?: string } }> };
      const counts = new Map<string, number>();
      for (const n of parsed.nodes ?? []) {
        const t = n.data?.activityType;
        if (typeof t === 'string') counts.set(t, (counts.get(t) ?? 0) + 1);
      }
      return [...counts.entries()]
        .sort((a, b) => b[1] - a[1])
        .map(([type, count]) => ({ type, count }));
    } catch {
      return [];
    }
  }, [generated]);

  return (
    <div
      className="fixed inset-0 z-[60] bg-black/30 backdrop-blur-sm flex items-center justify-center p-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="ai-workflow-dialog-title"
      onKeyDown={handleKeyDown}
    >
      <div className="bg-surface-lowest rounded-xl shadow-2xl ring-1 ring-outline-variant/20 w-full max-w-2xl max-h-[90vh] flex flex-col overflow-hidden">

        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 bg-surface-low border-b border-outline-variant/20">
          <div className="flex items-center gap-2">
            <MagicWandFilled size={16} className="text-primary" />
            <span id="ai-workflow-dialog-title" className="text-sm font-headline font-bold text-on-surface">
              {stage === 'prompt' ? 'Workflow per KI generieren' : 'Generierten Workflow überprüfen'}
            </span>
          </div>
          <button
            onClick={onClose}
            disabled={generating || creating}
            className="p-1 text-on-surface-variant hover:text-error hover:bg-error-container/30 rounded transition-colors disabled:opacity-40"
            aria-label={t('common:close')}
          >
            <Close size={14} />
          </button>
        </div>

        {/* Body */}
        <div className="flex-1 overflow-y-auto px-4 py-4 space-y-3">
          {stage === 'prompt' && (
            <>
              <p className="text-xs text-on-surface-variant font-label leading-snug">
                Beschreibe den gewünschten Workflow. Die KI baut Trigger, Aktivitäten und Verbindungen
                — wenn ein <code>runScript</code>-Step nötig ist, schreibt sie auch das PowerShell.
                Im nächsten Schritt siehst du das Ergebnis und kannst Name/Beschreibung anpassen,
                bevor der Workflow angelegt wird.
              </p>

              <textarea
                ref={textareaRef}
                value={prompt}
                onChange={(e) => setPrompt(e.target.value)}
                placeholder={'z.B. Täglich um 06:00 prüft der Workflow den Disk-Space von ServerA. Wenn freier Speicher unter 10% fällt, wird ein Cleanup-Skript ausgeführt und eine Mail an ops@firma geschickt.'}
                rows={8}
                disabled={generating}
                aria-label={t('ai:aria.prompt')}
                className="w-full input-field font-mono text-sm resize-y"
              />

              {error && (
                <div role="alert" className="bg-error-container/20 border border-error/30 rounded px-2 py-1.5 text-xs text-on-error-container font-label whitespace-pre-wrap">
                  {error}
                </div>
              )}

              <p className="text-[10px] font-label text-on-surface-variant leading-snug">
                <strong className="text-amber-700">Hinweis:</strong> Generierte Workflows starten als <strong>disabled</strong>.
                Aktivieren erst nach manueller Sichtprüfung — die KI kann subtile Aktivitäts-Configs falsch raten.
              </p>
            </>
          )}

          {stage === 'preview' && generated && (
            <>
              <div className="grid grid-cols-1 sm:grid-cols-[1fr_auto] gap-3">
                <label className="block">
                  <span className="text-[10px] font-label font-bold text-on-surface-variant uppercase tracking-widest">Name</span>
                  <input
                    type="text"
                    value={editedName}
                    onChange={(e) => setEditedName(e.target.value)}
                    disabled={creating}
                    className="input-field mt-1 w-full text-sm"
                    aria-label={t('ai:aria.name')}
                  />
                </label>
                <div className="flex items-center gap-3 text-xs text-on-surface-variant tabular-nums whitespace-nowrap pt-5">
                  <Stat label="Nodes" value={generated.nodeCount} />
                  <Stat label="Edges" value={generated.edgeCount} />
                  {generated.retried && (
                    <span className="text-amber-700" title={t('ai:workflowDialog.retriedNonJson')}>
                      ⚠ retried
                    </span>
                  )}
                </div>
              </div>

              <label className="block">
                <span className="text-[10px] font-label font-bold text-on-surface-variant uppercase tracking-widest">Beschreibung</span>
                <textarea
                  value={editedDescription}
                  onChange={(e) => setEditedDescription(e.target.value)}
                  rows={2}
                  disabled={creating}
                  aria-label={t('ai:aria.description')}
                  className="input-field mt-1 w-full text-sm resize-y"
                />
              </label>

              {activityHistogram.length > 0 && (
                <div>
                  <span className="text-[10px] font-label font-bold text-on-surface-variant uppercase tracking-widest">Activities</span>
                  <div className="mt-1 flex flex-wrap gap-1.5">
                    {activityHistogram.map(({ type, count }) => (
                      <span key={type} className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[11px] font-medium bg-surface-container text-on-surface tabular-nums">
                        {count}× {type}
                      </span>
                    ))}
                  </div>
                </div>
              )}

              <div>
                <button
                  onClick={() => setShowJson((v) => !v)}
                  className="flex items-center gap-1 text-[10px] font-label font-bold text-on-surface-variant uppercase tracking-widest hover:text-on-surface transition-colors"
                  aria-expanded={showJson}
                >
                  {showJson ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
                  Definition JSON
                </button>
                {showJson && (
                  <div className="relative mt-1">
                    <pre
                      data-testid="workflow-definition-json"
                      className="bg-surface-low rounded px-3 py-2 text-[11px] font-mono text-on-surface max-h-64 overflow-auto whitespace-pre-wrap break-all"
                    >
                      {prettifyJson(generated.definitionJson)}
                    </pre>
                    <button
                      onClick={() => navigator.clipboard?.writeText(generated.definitionJson)}
                      className="absolute top-1 right-1 p-1 text-on-surface-variant hover:text-on-surface hover:bg-surface-high rounded transition-colors"
                      title={t('ai:workflowDialog.copyJson')}
                      aria-label={t('ai:aria.copyJson')}
                    >
                      <Copy size={12} />
                    </button>
                  </div>
                )}
              </div>

              <p className="text-[10px] font-label text-on-surface-variant leading-snug">
                Modell: <code>{generated.model}</code> · Generierung: {generated.durationMs} ms
              </p>

              {error && (
                <div role="alert" className="bg-error-container/20 border border-error/30 rounded px-2 py-1.5 text-xs text-on-error-container font-label whitespace-pre-wrap">
                  {error}
                </div>
              )}
            </>
          )}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-end gap-2 px-4 py-3 bg-surface-low border-t border-outline-variant/20">
          {stage === 'prompt' && (
            <>
              <button
                onClick={onClose}
                disabled={generating}
                className="px-3 py-1.5 text-xs font-label text-on-surface-variant hover:text-on-surface hover:bg-surface-high rounded-md transition-colors disabled:opacity-40"
              >
                Abbrechen
              </button>
              <button
                onClick={handleGenerate}
                disabled={generating || prompt.trim().length === 0}
                className="flex items-center gap-1.5 px-4 py-1.5 bg-gradient-to-br from-primary to-primary-container text-on-primary text-xs font-label font-semibold rounded-md shadow-sm hover:shadow-lg hover:brightness-110 disabled:opacity-50 disabled:cursor-not-allowed transition-all cursor-pointer"
              >
                {generating ? <CircleDash size={12} className="animate-spin" /> : <MagicWandFilled size={12} />}
                {generating ? 'Generiere…' : 'Generieren'}
              </button>
            </>
          )}
          {stage === 'preview' && (
            <>
              <button
                onClick={() => { setStage('prompt'); setError(null); }}
                disabled={creating}
                className="px-3 py-1.5 text-xs font-label text-on-surface-variant hover:text-on-surface hover:bg-surface-high rounded-md transition-colors disabled:opacity-40"
              >
                Zurück
              </button>
              <button
                onClick={onClose}
                disabled={creating}
                className="px-3 py-1.5 text-xs font-label text-on-surface-variant hover:text-on-surface hover:bg-surface-high rounded-md transition-colors disabled:opacity-40"
              >
                Verwerfen
              </button>
              <button
                onClick={handleCreate}
                disabled={creating || editedName.trim().length === 0}
                className="flex items-center gap-1.5 px-4 py-1.5 bg-gradient-to-br from-primary to-primary-container text-on-primary text-xs font-label font-semibold rounded-md shadow-sm hover:shadow-lg hover:brightness-110 disabled:opacity-50 disabled:cursor-not-allowed transition-all cursor-pointer"
              >
                {creating ? <CircleDash size={12} className="animate-spin" /> : <MagicWandFilled size={12} />}
                {creating ? 'Erstelle…' : 'Erstellen & öffnen'}
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

function Stat({ label, value }: Readonly<{ label: string; value: number }>) {
  return (
    <span className="inline-flex items-baseline gap-1">
      <span className="font-semibold text-on-surface">{value}</span>
      <span>{label}</span>
    </span>
  );
}

function prettifyJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

export default WorkflowGenerationDialog;
